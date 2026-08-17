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
        private float _nextStartupSampleTime;
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

        // プロセス全体の使用量（UI 表示用）。起動直後と最新。
        private long _processPrivateBaseline;
        private long _processPrivateLatest;
        private int _customLevelCountLatest = -1;
        private long _monoHeapLatest;

        // メモリ残量ゲージ用。
        // 起動直後の読み込みぶんを増加率に含めないよう、起点を少し遅らせる。
        private const double RateAnchorAfterSeconds = 300;
        // 起点からこの分数が経つまでは増加率を出さない。区間が短いと誤差で桁が変わる。
        private const double RateWindowMinutes = 3;
        // 実測ではシステム空きが約1.3GBまで落ちた時点で明確に重くなっていた。
        // ゼロではなくこの水準を「尽きた」とみなして残り時間を出す。
        private const double ExhaustionFloorBytes = 2.0 * 1024 * 1024 * 1024;

        private SystemMemory.Snapshot _sysLatest;
        private long _rateAnchorBytes;
        private DateTime _rateAnchorAt;

        // 型別一覧の自動書き出しの予約（曲終了処理中に走らせると遷移を乱すため）
        private bool _dumpRequested;

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

            // 型別一覧の書き出しも同様に、メニューに戻りきってから行う
            if (_dumpRequested && !_inSong && !_unloadRunning)
            {
                _dumpRequested = false;
                DumpAssets();
            }

            // 画面遷移が落ち着いたら、その画面名でスナップショットを1本取る。
            // ★段差の犯人を特定するための主線。定期スナップショット（既定30秒）では
            // 段差の前後がどの画面だったのか分からず、後から追えなかった。
            // 曲中は走らせない（遷移も起きないが、重い走査を確実に避けるため）。
            if (!_inSong && !_unloadRunning)
            {
                string? viewPhase = ScreenWatcher.ConsumeSettledPhase();
                if (viewPhase != null)
                {
                    TakeAndWrite(viewPhase);
                    // 走査した直後に定期サンプルが重なると二度手間なので、間隔を測り直す
                    _nextSampleTime = Time.realtimeSinceStartup + PluginConfig.Instance.SampleIntervalSeconds;
                    return;
                }
            }

            // 起動直後だけ、メモリの数字だけを短い間隔で残す。
            // 曲の読み込みは数十秒で終わるため、30秒間隔では前後2点しか残らず
            // 「読み込みに何MB掛かったか」が読めない。走査はしないので負荷はほぼ無い。
            int denseSec = PluginConfig.Instance.StartupDenseSampleSeconds;
            if (denseSec > 0 && (DateTime.Now - _startedAt).TotalSeconds < denseSec
                && Time.realtimeSinceStartup >= _nextStartupSampleTime)
            {
                _nextStartupSampleTime = Time.realtimeSinceStartup
                    + Math.Max(1, PluginConfig.Instance.StartupSampleIntervalSeconds);
                WriteMemoryOnlyRow("startup");
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
            // 曲の開始でも画面は動くが、それは songStart 行で記録される。
            // 二重に取らないよう、溜まっている遷移は捨てる。
            ScreenWatcher.Discard();
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
            // 曲終了直後のメニュー復帰ぶんは songEnd 行が拾うので捨てる
            ScreenWatcher.Discard();

            int every = PluginConfig.Instance.AutoDumpEverySongs;
            if (every > 0 && _songsPlayed % every == 0)
                _dumpRequested = true;
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

        // 走査を伴わない (TOTAL) 行だけを書く。
        // 個数の列は全て 0 になるので、phase で startup 行だと分かるようにしてある。
        private void WriteMemoryOnlyRow(string phase)
        {
            if (_sink == null)
                return;

            try
            {
                DateTime now = DateTime.Now;
                WriteTotalRow(now, (now - _startedAt).TotalMinutes, phase,
                              new Dictionary<string, ModStats>());
                _sink.Flush();
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"startup sample failed: {e}");
            }
        }

        // プロセス全体の値。mod 列を "(TOTAL)" として同じ CSV に混ぜる。
        // 列は増やさず、既存の数値列を流用する（フィルタ一発で分離できるようにするため）。
        // どの列が何を意味するかは README の「(TOTAL) 行」の表と必ず一致させること。
        private void WriteTotalRow(DateTime now, double elapsedMin, string phase,
                                   Dictionary<string, ModStats> stats)
        {
            long managedHeap = GC.GetTotalMemory(false);

            // プロセス全体。Unity の外側（ネイティブ・ランタイム・ドライバ）を含む唯一の数字。
            // スナップショット時にしか読まないので、毎回取り直して問題ない。
            ProcessMemory.Read(out long procPrivate, out long procWorkingSet);

            // Unity が確保している総量（ネイティブ側を含む）。
            // 以前はここに Profiler.GetMonoUsedSizeLong を入れていたが、Unity の Mono では
            // GC.GetTotalMemory と完全に同じ値を返し、1列まるごと無駄になっていた。
            // ネイティブ側の増加はマネージドヒープには出ないので、そちらを見る方が有用。
            long unityAllocated;
            try { unityAllocated = Profiler.GetTotalAllocatedMemoryLong(); }
            catch { unityAllocated = 0; }

            // 「確保総量」と「予約総量」の差＝アロケータが掴んでいるが未使用の分。
            // グラフィックスドライバ側は GetTotalAllocatedMemoryLong に含まれないので別途取る。
            // プロセスのコミットだけが跳ねてどの列も動かない現象を切り分けるために必要。
            long unityReserved;
            try { unityReserved = Profiler.GetTotalReservedMemoryLong(); }
            catch { unityReserved = 0; }

            // Mono がOSから借りているヒープ総量。GC.GetTotalMemory（使用量）との差が
            // 「回収済みだが返せていない隙間」＝ Boehm の断片化。
            // GetAllocatedMemoryForGraphicsDriver は全行 0 を返して機能しなかったため差し替えた。
            long monoHeap;
            try { monoHeap = Profiler.GetMonoHeapSizeLong(); }
            catch { monoHeap = 0; }

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

            // プロセス全体（専用列。意味の流用なし）
            t.ProcessPrivateBytes = procPrivate;
            t.ProcessWorkingSetBytes = procWorkingSet;
            t.UnityReservedBytes = unityReserved;
            t.MonoHeapBytes = monoHeap;
            _monoHeapLatest = monoHeap;

            SystemMemory.Snapshot sys = SystemMemory.Read();
            t.SystemFreeBytes = sys.FreePhysBytes;
            t.SystemCommitPercent = sys.CommitUsedPercent;
            _sysLatest = sys;

            // 増加率の起点。起動直後は曲やアセットの読み込みで急増するため、
            // そこを含めると「あと何分」の予測が実態より短く出てしまう。
            // 落ち着いた頃（既定5分）を過ぎた最初の1点を基準にする。
            double elapsedSec = (now - _startedAt).TotalSeconds;
            if (_rateAnchorBytes == 0 && elapsedSec >= RateAnchorAfterSeconds && procPrivate > 0)
            {
                _rateAnchorBytes = procPrivate;
                _rateAnchorAt = now;
            }
            t.CustomLevelCount = SongCoreProbe.CustomLevelCount();
            _customLevelCountLatest = t.CustomLevelCount;
            _processPrivateLatest = procPrivate;
            if (_processPrivateBaseline == 0)
                _processPrivateBaseline = procPrivate;

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

            // 基準が songEnd に昇格するまでは、増分にメニューのロード分が丸ごと含まれる。
            // 左カラムにも同じ警告は出ているが、この表だけを見ている人が
            // 「全MODがリークしている」と誤読しないよう、表の直上にも出す。
            if (_baselineRank < 2)
                sb.AppendLine("<color=#FFC864>menu load included - play 1 song</color>");

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

                // 個数が増え続けているMODを赤くして、目で追えるようにする。
                // ただし基準が songEnd に昇格するまでは増分にメニューのロード分が含まれ、
                // 全MODが赤く染まって意味を成さないので、その間は着色しない。
                string color = (_baselineRank >= 2 && r.DeltaCount >= 50) ? "#FF6B6B" : "#FFFFFF";
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

        // 左カラム。プロセスとシステム全体の統計。
        //
        // 右のMOD別ランキングと分けているのは、見る目的が違うため。
        // こちらは「あと何分プレイできるか」、右は「どのMODが伸びているか」。
        // 1文字あたり約1.26単位・幅48なので、1行は 38 文字以内に収めること。
        internal string BuildStats()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<mspace=0.42em>");

            // ★最初に出す。重くなる直接の引き金はここが尽きることなので、
            // MOD別のランキングより先に目に入る必要がある。
            AppendMemoryGauge(sb);

            if (_processPrivateLatest > 0)
            {
                double procMb = _processPrivateLatest / 1024.0 / 1024.0;
                double procRise = (_processPrivateLatest - _processPrivateBaseline) / 1024.0 / 1024.0;
                sb.AppendLine($"proc  {procMb:F0}MB (+{procRise:F0})");
            }

            // 条件（曲あり/なし）と読み込み完了を、ゲームを終了せずにその場で確認できるようにする。
            // 読み込み中に終了してしまった計測が実際にあったため、done かどうかを明示する。
            if (_customLevelCountLatest >= 0)
            {
                bool? loaded = SongCoreProbe.AreSongsLoaded();
                string state = loaded == true ? "done"
                             : loaded == false ? "<color=#FFC864>loading</color>"
                             : "?";
                sb.AppendLine($"songs {_customLevelCountLatest} ({state})");
            }

            // ★リークの主指標。GC 直後の使用量（床）が起動時からどれだけ上がったか。
            //
            // 基準が songEnd に昇格するまでは、この増分にメニューのロード分が丸ごと含まれる。
            // 起動直後でも +900MB 程度になるので、そこで赤く出すと毎回誤報になる。
            // 判定に使える状態になってからだけ着色する。
            if (_managedFloorBaseline > 0)
            {
                double floorMb = _managedFloorLatest / 1024.0 / 1024.0;
                double riseMb = (_managedFloorLatest - _managedFloorBaseline) / 1024.0 / 1024.0;
                string color = (_baselineRank >= 2 && riseMb >= 500) ? "#FF6B6B" : "#FFFFFF";
                sb.AppendLine($"<color={color}>GCuse {floorMb:F0}MB (+{riseMb:F0})</color>");
            }

            // Boehm は非圧縮GCなので、使用量が横ばいでもヒープだけ広がることがある。
            // 借りている総量と使用量の比を出しておけば、断片化が進んでいるかがその場で分かる。
            if (_monoHeapLatest > 0)
            {
                double heapMb = _monoHeapLatest / 1024.0 / 1024.0;
                double usedMb = _managedFloorLatest / 1024.0 / 1024.0;
                double ratio = usedMb > 0 ? heapMb / usedMb : 0;
                string color = ratio >= 2.0 ? "#FF6B6B" : "#FFFFFF";
                sb.AppendLine($"<color={color}>heap  {heapMb:F0}MB (x{ratio:F1} used)</color>");
            }

            sb.AppendLine($"{_latestAt:HH:mm:ss} songs={_songsPlayed}");
            sb.AppendLine($"vs {_baselinePhase}"
                        + (_baselineRank < 2 ? " <color=#FFC864>(play 1)</color>" : ""));

            // 帰属を諦めた分があるなら黙って隠さない。数値が過小である可能性を明示する。
            long skipped = OwnershipTracker.SkippedLookups;
            if (skipped > 0)
                sb.AppendLine($"<color=#FFC864>rate-limited {skipped}</color>");

            // 直近の解放結果。回収できているかがその場で分かる。
            if (_lastUnloadSummary != null)
                sb.AppendLine($"<color=#8FD98F>{_lastUnloadSummary}</color>");

            sb.Append("</mspace>");
            return sb.ToString();
        }

        // ── メモリ残量ゲージ ─────────────────────────────────────────
        //
        // 「あと何分プレイできるか」を出すのが目的。
        // 空きの絶対値だけでは判断できない（増え方が速ければ余裕があってもすぐ尽きる）ので、
        // このセッションで実測した増加率から到達時刻を割り出して併記する。
        private void AppendMemoryGauge(System.Text.StringBuilder sb)
        {
            if (!_sysLatest.Valid)
                return;

            double freeGb = _sysLatest.FreePhysBytes / 1073741824.0;
            double totalGb = _sysLatest.TotalPhysBytes / 1073741824.0;
            double usedPct = totalGb > 0 ? 100.0 * (1.0 - freeGb / totalGb) : 0;

            sb.AppendLine($"<color={LevelColor(usedPct)}>"
                + $"MEM  {Bar(usedPct)} {freeGb:F1}/{totalGb:F1}GB free</color>");

            double commitPct = _sysLatest.CommitUsedPercent;
            sb.AppendLine($"<color={LevelColor(commitPct)}>"
                + $"CMT  {Bar(commitPct)} {commitPct:F0}%</color>");

            sb.AppendLine(BuildRemainingLine());
        }

        // 増加率と、空きが尽きるまでの見込み。
        private string BuildRemainingLine()
        {
            // 数字が出るまで最短でも 起点5分 + 計測3分 = 8分かかる。
            // その間ずっと "measuring..." だけだと、壊れているのか待てばよいのか判断できない。
            // あと何分で出るかを見せる。
            DateTime now = DateTime.Now;
            if (_rateAnchorBytes == 0 || _processPrivateLatest <= 0)
            {
                double waitMin = (RateAnchorAfterSeconds - (now - _startedAt).TotalSeconds) / 60.0
                                 + RateWindowMinutes;
                return $"<color=#AAAAAA>rate in {Math.Max(1, Math.Ceiling(waitMin)):F0}min</color>";
            }

            double hours = (now - _rateAnchorAt).TotalHours;
            if (hours * 60.0 < RateWindowMinutes) // 短すぎる区間は誤差が大きく意味のある数字にならない
            {
                double waitMin = RateWindowMinutes - hours * 60.0;
                return $"<color=#AAAAAA>rate in {Math.Max(1, Math.Ceiling(waitMin)):F0}min</color>";
            }

            double mbPerHour = (_processPrivateLatest - _rateAnchorBytes) / 1048576.0 / hours;

            if (mbPerHour <= 1.0)
                return $"<color=#8FD98F>rate {mbPerHour:F0}MB/h stable</color>";

            double headroomMb = (_sysLatest.FreePhysBytes - ExhaustionFloorBytes) / 1048576.0;
            if (headroomMb <= 0)
                return $"<color=#FF6B6B>rate +{mbPerHour:F0}MB/h OUT OF MEM</color>";

            double leftHours = headroomMb / mbPerHour;
            string color = leftHours < 0.5 ? "#FF6B6B" : leftHours < 1.5 ? "#FFC864" : "#FFFFFF";
            string left = leftHours >= 1.0
                ? $"{leftHours:F1}h"
                : $"{leftHours * 60:F0}min";

            return $"<color={color}>rate +{mbPerHour:F0}MB/h ~{left} left</color>";
        }

        // 横棒。ブロック文字はゲームのフォントに含まれる保証がないため ASCII で描く。
        // 幅は左カラム(48単位 ≒ 38文字)に収まるよう 12 桁。伸ばすと行が見切れる。
        private static string Bar(double percent)
        {
            const int Width = 12;
            int filled = (int)Math.Round(Math.Max(0, Math.Min(100, percent)) / 100.0 * Width);
            return "[" + new string('#', filled) + new string('.', Width - filled) + "]";
        }

        // 実測では空きが 1.3GB まで落ちた時点で明確に重くなっていた。
        // 61GB 環境なら 98% 手前、32GB 環境なら 96% 手前が危険域になる。
        private static string LevelColor(double usedPercent)
            => usedPercent >= 92 ? "#FF6B6B"
             : usedPercent >= 80 ? "#FFC864"
             : "#8FD98F";

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
