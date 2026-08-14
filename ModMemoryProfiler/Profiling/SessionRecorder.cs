using ModMemoryProfiler.Configuration;
using ModMemoryProfiler.Output;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace ModMemoryProfiler.Profiling
{
    // 計測の司令塔。DontDestroyOnLoad な GameObject に載って常駐し、
    // 一定間隔＋曲終了時にスナップショットを取って CSV に書き出す。
    //
    // Zenject や BSML には依存しない（依存を増やすほど壊れやすくなるため）。
    // 曲中かどうかは GameCore シーンのロード/アンロードで判定する。
    internal class SessionRecorder : MonoBehaviour
    {
        private const string GameSceneName = "GameCore";

        private CsvSink? _sink;
        private DateTime _startedAt;
        private float _nextSampleTime;
        private int _framesSinceLastSample;
        private int _songsPlayed;
        private bool _inSong;

        // UI 表示用。CSV に書いたのと同じスナップショットを保持しておき、
        // 「起動直後（baseline）から今までに何MB積み上がったか」を差分で見せる。
        private Dictionary<string, ModStats>? _baseline;
        private Dictionary<string, ModStats>? _latest;
        private DateTime _latestAt;

        // ベースラインとして採用した局面。ランクの高い局面が来たら乗り換える。
        // 起動直後のスナップショットを基準にすると、その後に読まれるメニューのアセット一式が
        // まるごと「増分」に化けて、全MODがリークしているように見えてしまうため。
        // 最終的には songEnd（1曲プレイし終えた状態）を基準にしたい。曲を跨いで積み上がる分だけが残る。
        private string _baselinePhase = "";
        private int _baselineRank = -1;

        private static int PhaseRank(string phase)
        {
            switch (phase)
            {
                case "songEnd": return 2; // 最良: 1曲プレイ後の定常状態
                case "menu":    return 1; // 次善: メニューのロードが終わった状態
                default:        return 0; // baseline / songStart / song / manual
            }
        }

        internal static SessionRecorder? Instance { get; private set; }

        internal int SongsPlayed => _songsPlayed;
        internal bool InSong => _inSong;

        // OwnershipTracker がレート制限の強さを切り替えるために参照する。
        // フックは任意のスレッドから走るので static かつ volatile にしておく。
        internal static volatile bool IsInSong;

        // マネージドヒープの区間内 最小/最大。
        //
        // GC.GetTotalMemory の生の値は「ゴミが溜まっては GC で消える」ため大きく上下し、
        // スナップショット時点の1点だけを見ても傾向が読めない（実際それで長く読み違えた）。
        // 見るべきは GC 直後の値＝区間内の最小値で、これが上がり続けていれば
        // 「回収できないオブジェクトが積み上がっている」＝本物のリークと判断できる。
        private long _managedMin = long.MaxValue;
        private long _managedMax;
        private long _managedFloorBaseline;
        private long _managedFloorLatest;

        // Resources.UnloadUnusedAssets の実行予約と、直近の結果（UI 表示用）
        private bool _unloadRequested;
        private bool _unloadRunning;
        private double _lastUnloadMs;
        private string? _lastUnloadSummary;
        internal string? CsvPath => _sink?.FilePath;

        internal static void Create()
        {
            if (Instance != null)
                return;

            var go = new GameObject("ModMemoryProfiler.SessionRecorder");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            Instance = go.AddComponent<SessionRecorder>();
        }

        internal static void Destroy()
        {
            if (Instance == null)
                return;
            Destroy(Instance.gameObject);
            Instance = null;
        }

        private void Awake()
        {
            _startedAt = DateTime.Now;

            string dir = System.IO.Path.Combine(
                UnityEngine.Application.dataPath, "..", "UserData", "ModMemoryProfiler");
            try
            {
                _sink = new CsvSink(dir);
                Plugin.Log.Info($"Recording to {_sink.FilePath}");
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"Failed to open CSV: {e}");
                enabled = false;
                return;
            }

            SceneManager.sceneLoaded   += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;

            _nextSampleTime = Time.realtimeSinceStartup + PluginConfig.Instance.SampleIntervalSeconds;

            // 起動直後のベースラインを1本取っておく（以降は全てこれとの差分で見る）
            TakeAndWrite("baseline");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded   -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            _sink?.Dispose();
            _sink = null;
        }

        private void Update()
        {
            _framesSinceLastSample++;

            // 毎フレーム見る。カウンタを読むだけなので割り当ても走査も発生しない。
            long managed = GC.GetTotalMemory(false);
            if (managed < _managedMin) _managedMin = managed;
            if (managed > _managedMax) _managedMax = managed;

            // 解放はシーンのアンロード処理中ではなく、メニューに戻りきった次のフレーム以降に行う。
            // アンロード中に重い処理を挟むと遷移そのものを乱すため。
            if (_unloadRequested && !_inSong && !_unloadRunning)
            {
                _unloadRequested = false;
                StartCoroutine(UnloadUnusedAssetsAndMeasure());
            }

            if (Time.realtimeSinceStartup < _nextSampleTime)
                return;

            _nextSampleTime = Time.realtimeSinceStartup + PluginConfig.Instance.SampleIntervalSeconds;

            // 曲中の走査はフレーム落ちを招くため、既定では取らない
            if (_inSong && !PluginConfig.Instance.SampleDuringSong)
            {
                // CPU の累積だけは捨てずに区間を締める（メモリ走査はしない）
                CpuProfiler.DrainMsPerFrame(_framesSinceLastSample);
                _framesSinceLastSample = 0;
                return;
            }

            TakeAndWrite(_inSong ? "song" : "menu");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != GameSceneName)
                return;
            _inSong = true;
            IsInSong = true;
            TakeAndWrite("songStart");
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (scene.name != GameSceneName)
                return;
            _inSong = false;
            IsInSong = false;
            _songsPlayed++;
            _unloadRequested = PluginConfig.Instance.UnloadUnusedAssetsOnMenu;
            // ★リーク判定の基準点。songEnd 行同士を比較すれば「1曲あたり何MB積み上がったか」が出る。
            // 設定に関わらず必ず取る。
            TakeAndWrite("songEnd");
        }

        // スナップショットを取って CSV に書く
        private void TakeAndWrite(string phase)
        {
            if (_sink == null)
                return;

            try
            {
                Dictionary<string, ModStats> stats = MemoryCensus.TakeSnapshot();
                Dictionary<string, double> cpu = CpuProfiler.DrainMsPerFrame(_framesSinceLastSample);
                _framesSinceLastSample = 0;

                // CPU 側にしか出てこない MOD（アセットを持たないMOD）も行を作る
                foreach (var kv in cpu)
                {
                    if (!stats.ContainsKey(kv.Key))
                        stats[kv.Key] = new ModStats();
                    stats[kv.Key].MsPerFrame = kv.Value;
                }

                DateTime now = DateTime.Now;
                double elapsedMin = (now - _startedAt).TotalMinutes;

                foreach (var kv in stats)
                    _sink.WriteRow(now, elapsedMin, phase, _songsPlayed, kv.Key, kv.Value);

                WriteTotalRow(now, elapsedMin, phase, stats);
                _sink.Flush();

                _latest = stats;
                _latestAt = now;

                // より信頼できる局面のスナップショットが来たらベースラインを差し替える。
                // 同ランクなら最初のものを保持する（2曲目以降の増分を見たいので上書きしない）。
                int rank = PhaseRank(phase);
                if (_baseline == null || rank > _baselineRank)
                {
                    _baseline = stats;
                    _baselineRank = rank;
                    _baselinePhase = phase;
                }

                Plugin.DebugLog($"snapshot written: phase={phase} mods={stats.Count}");
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"snapshot failed: {e}");
            }
        }

        // プロセス全体の値。mod 列を "(TOTAL)" として同じ CSV に混ぜる。
        // 列は増やさず、既存の数値列を流用する（フィルタ一発で分離できるようにするため）。
        // どの列が何を意味するかは README の「(TOTAL) 行」の表と必ず一致させること。
        private void WriteTotalRow(DateTime now, double elapsedMin, string phase,
                                   Dictionary<string, ModStats> stats)
        {
            long managedHeap = GC.GetTotalMemory(false);

            // Unity が確保している総量（ネイティブ側を含む）。
            // 以前はここに Profiler.GetMonoUsedSizeLong を入れていたが、Unity の Mono では
            // GC.GetTotalMemory と完全に同じ値を返し、1列まるごと無駄になっていた。
            // ネイティブ側の増加はマネージドヒープには出ないので、そちらを見る方が有用。
            long unityAllocated;
            try { unityAllocated = Profiler.GetTotalAllocatedMemoryLong(); }
            catch { unityAllocated = 0; }

            // 個数列は全MODの素直な合計を入れる（意味の流用なし）。
            // リーク判定で最初に見るのがここなのに、従来は手で足し合わせる必要があった。
            var t = new ModStats();
            foreach (ModStats s in stats.Values)
            {
                t.TextureCount += s.TextureCount;
                t.RenderTextureCount += s.RenderTextureCount;
                t.SpriteCount += s.SpriteCount;
                t.MeshCount += s.MeshCount;
                t.AudioCount += s.AudioCount;
                t.GameObjectCount += s.GameObjectCount;
                t.MonoBehaviourCount += s.MonoBehaviourCount;
            }

            // 以下は意味を流用している列。README の「(TOTAL) 行」の表と必ず一致させること。
            t.TextureBytes = managedHeap;          // = managedHeapMB（この瞬間の値）
            t.RenderTextureBytes = unityAllocated; // = unityAllocatedMB（ネイティブ含む）

            // ★リーク判定の主指標。meshMB / audioMB 列は TOTAL 行では未使用なので流用する。
            // managedHeapMin が単調増加していれば、GC で回収できないオブジェクトが
            // 積み上がっている＝マネージドリーク。
            long floor = (_managedMin == long.MaxValue) ? managedHeap : _managedMin;
            t.MeshBytes = floor;
            t.AudioBytes = (_managedMax == 0) ? managedHeap : _managedMax;
            _managedMin = long.MaxValue;
            _managedMax = 0;

            // UI 用。起動直後の床を基準に、そこからどれだけ上がったかを見せる。
            if (_managedFloorBaseline == 0)
                _managedFloorBaseline = floor;
            _managedFloorLatest = floor;
            // Unity の GC は Boehm で世代を持たないため CollectionCount(0/1/2) は全て同値。
            // 3列に分けても情報が増えないので 1 列だけ使う。
            t.MaterialCount = GC.CollectionCount(0);
            t.LiveAssetCount = OwnershipTracker.TrackedCount;
            // レート制限で帰属を諦めた累計件数。0 でない場合、MOD別の数値は
            // その分だけ (Untracked) に逃げている。
            t.UnfreedBundles = (int)Math.Min(int.MaxValue, OwnershipTracker.SkippedLookups);
            // afterUnload 行に限り、UnloadUnusedAssets の所要時間(ms)を入れる。
            // 実用に耐える速さかどうかの判断材料になる。
            if (phase == "afterUnload")
                t.MsPerFrame = _lastUnloadMs;

            _sink!.WriteRow(now, elapsedMin, phase, _songsPlayed, "(TOTAL)", t);
        }

        // ── 解放の検証 ───────────────────────────────────────────────

        // 参照が切れているだけのアセットを実際に解放し、何がどれだけ回収できたかを測る。
        //
        // 直前の songEnd スナップショットが「解放前」、この後に取る afterUnload が「解放後」。
        // 差分が大きければ「参照は切れていたが Unity が解放していなかっただけ」、
        // ほとんど減らなければ「誰かが掴んでいる本物のリーク」と切り分けられる。
        private System.Collections.IEnumerator UnloadUnusedAssetsAndMeasure(bool purgeCoverCache = false)
        {
            _unloadRunning = true;

            Dictionary<string, ModStats>? before = _latest;

            // 先にカバー画像キャッシュの参照を切る。これをやらないと、
            // キャッシュが握っている分は「使用中」とみなされて解放されない。
            int purged = purgeCoverCache ? CoverCachePurger.ClearAll() : 0;

            // 先にマネージド側を回収しておく。C# 側から参照が残っていると
            // UnloadUnusedAssets はそのアセットを「使用中」とみなして解放しない。
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            AsyncOperation op = Resources.UnloadUnusedAssets();
            while (!op.isDone)
                yield return null;
            sw.Stop();

            _lastUnloadMs = sw.Elapsed.TotalMilliseconds;
            TakeAndWrite(purgeCoverCache ? "afterPurge" : "afterUnload");

            if (before != null && _latest != null)
            {
                int dObj = TotalLiveObjects(_latest) - TotalLiveObjects(before);
                double dMb = TotalAssetMb(_latest) - TotalAssetMb(before);
                string what = purgeCoverCache ? $"purge({purged})" : "unload";
                _lastUnloadSummary = $"{what} {dObj} objs {dMb:F0}MB in {_lastUnloadMs:F0}ms";
                Plugin.Log.Info($"{what}: {dObj} objects, {dMb:F1} MB, {_lastUnloadMs:F0} ms");
            }

            _unloadRunning = false;
        }

        private static int TotalLiveObjects(Dictionary<string, ModStats> stats)
        {
            int n = 0;
            foreach (ModStats s in stats.Values)
                n += LiveCount(s);
            return n;
        }

        private static double TotalAssetMb(Dictionary<string, ModStats> stats)
        {
            double mb = 0;
            foreach (var kv in stats)
            {
                if (kv.Key == "(TOTAL)") continue; // 混ざっていないはずだが念のため
                mb += TotalMb(kv.Value);
            }
            return mb;
        }

        // 生きているアセットの名前一覧を書き出す。何が積み上がっているのかの正体を見るため。
        internal void DumpAssets()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(_sink?.FilePath ?? "") ?? "";
                // MonoBehaviour の型は種類が多いので、上限を広めに取る
                string path = MemoryCensus.DumpAssetNames(dir, 1500);
                _lastUnloadSummary = "dumped " + System.IO.Path.GetFileName(path);
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"asset dump failed: {e}");
                _lastUnloadSummary = "dump failed (see log)";
            }
        }

        // UI のボタンから即実行する。曲中は重すぎるので受け付けない。
        // 直前の値が古いと回収量の差分が正しく出ないため、必ず取り直してから解放する。
        internal void UnloadNow()
        {
            if (_inSong)
            {
                Plugin.Log.Info("UnloadUnusedAssets skipped: in song.");
                _lastUnloadSummary = "unload skipped (in song)";
                return;
            }
            if (_unloadRunning)
                return;

            TakeAndWrite("beforeUnload");
            StartCoroutine(UnloadUnusedAssetsAndMeasure());
        }

        // カバー画像キャッシュを空にしてから解放する。
        // 曲リストのカバーは次に表示されたときに再読み込みされる。
        internal void PurgeCoverCacheNow()
        {
            if (_inSong)
            {
                Plugin.Log.Info("Purge skipped: in song.");
                _lastUnloadSummary = "purge skipped (in song)";
                return;
            }
            if (_unloadRunning)
                return;
            if (!CoverCachePurger.Available)
            {
                Plugin.Log.Warn("Purge unavailable: SpriteAsyncLoader not found.");
                _lastUnloadSummary = "purge unavailable (see log)";
                return;
            }

            TakeAndWrite("beforePurge");
            StartCoroutine(UnloadUnusedAssetsAndMeasure(purgeCoverCache: true));
        }

        // ── UI 向け ─────────────────────────────────────────────────

        // UI の Refresh ボタンから呼ぶ。CSV にも1行残る（手動採取であることが後で分かるように phase=manual）。
        internal void SnapshotNow()
        {
            TakeAndWrite("manual");
        }

        // 起動直後からの増分でMODを並べたランキング文字列を作る。
        // 絶対値ではなく差分で見せるのは、起動時から存在するアセットが (Untracked) に偏って
        // 順位が意味を持たなくなるため。増分こそがリークの手がかりになる。
        internal string BuildReport(int topN)
        {
            if (_latest == null || _baseline == null)
                return "No snapshot yet.";

            // 並び順は「増えた個数」を主キーにする。アイコン等の小さなアセットは
            // MB がほとんど動かないまま個数だけ増えるため、MB 順だと見落とす。
            var rows = new List<(string Mod, double DeltaMb, int DeltaCount, int Sprites, double MsPerFrame)>();
            foreach (var kv in _latest)
            {
                ModStats n = kv.Value;
                _baseline.TryGetValue(kv.Key, out ModStats b);
                double baseMb = b != null ? TotalMb(b) : 0.0;
                int baseCount = b != null ? LiveCount(b) : 0;
                rows.Add((kv.Key, TotalMb(n) - baseMb, LiveCount(n) - baseCount, n.SpriteCount, n.MsPerFrame));
            }
            rows.Sort((x, y) => y.DeltaCount.CompareTo(x.DeltaCount));

            var sb = new System.Text.StringBuilder();
            sb.Append("<mspace=0.42em>");
            sb.AppendLine($"{_latestAt:HH:mm:ss} songs={_songsPlayed} vs {_baselinePhase}"
                        + (_baselineRank < 2 ? "  <color=#FFC864>(play 1 song)</color>" : ""));

            // ★リークの主指標。GC 直後の使用量（床）が起動時からどれだけ上がったか。
            // ここが増え続けていれば、回収できないオブジェクトが積み上がっている。
            if (_managedFloorBaseline > 0)
            {
                double floorMb = _managedFloorLatest / 1024.0 / 1024.0;
                double riseMb = (_managedFloorLatest - _managedFloorBaseline) / 1024.0 / 1024.0;
                string color = riseMb >= 500 ? "#FF6B6B" : "#FFFFFF";
                sb.AppendLine($"<color={color}>GC floor {floorMb:F0}MB (+{riseMb:F0} since start)</color>");
            }

            // 帰属を諦めた分があるなら黙って隠さない。数値が過小である可能性を明示する。
            long skipped = OwnershipTracker.SkippedLookups;
            if (skipped > 0)
                sb.AppendLine($"<color=#FFC864>rate-limited {skipped} (raise the cap)</color>");

            // 直近の解放結果。回収できているかがその場で分かる。
            if (_lastUnloadSummary != null)
                sb.AppendLine($"<color=#8FD98F>{_lastUnloadSummary}</color>");
            // ヘッダと本文は必ず同じ書式で組む。空白を手打ちすると桁がずれる。
            sb.AppendLine(Row("MOD", "+num", "+MB", "sprite", "ms/f"));

            int shown = 0;
            foreach (var r in rows)
            {
                // 何も動いていないMODは行を埋めるだけなので出さない
                if (shown >= topN)
                    break;
                if (r.DeltaCount <= 0 && r.DeltaMb <= 0.01 && r.MsPerFrame <= 0.001)
                    continue;

                // 個数が増え続けているMODを赤くして、目で追えるようにする
                string color = r.DeltaCount >= 50 ? "#FF6B6B" : "#FFFFFF";
                string line = Row(r.Mod,
                    r.DeltaCount.ToString(), r.DeltaMb.ToString("F1"),
                    r.Sprites.ToString(), r.MsPerFrame.ToString("F2"));
                sb.AppendLine($"<color={color}>{line}</color>");
                shown++;
            }

            if (shown == 0)
                sb.AppendLine("(no measurable asset yet)");

            sb.Append("</mspace>");
            return sb.ToString();
        }

        // 1行分の桁揃え。ヘッダ・本文ともにこれを通すことで列がずれないようにする。
        // 等幅は BuildReport 側の <mspace> タグで担保している。
        private static string Row(string mod, string delta, string now, string ms, string bundles)
        {
            string name = mod.Length > 16 ? mod.Substring(0, 16) : mod.PadRight(16);
            return $"{name} {delta,5} {now,5} {ms,5} {bundles,3}";
        }

        private static double TotalMb(ModStats s)
            => (s.TextureBytes + s.RenderTextureBytes + s.MeshBytes + s.AudioBytes) / 1024.0 / 1024.0;

        // 生存オブジェクトの総数。アセットだけでなく GameObject / MonoBehaviour も含めて、
        // 「何かが解放されずに積み上がっている」を1つの数字で見られるようにする。
        private static int LiveCount(ModStats s)
            => s.LiveAssetCount + s.GameObjectCount + s.MonoBehaviourCount;
    }
}
