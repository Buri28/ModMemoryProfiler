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
                {
                    ModStats s = kv.Value;
                    _sink.WriteRow(now, elapsedMin, phase, _songsPlayed, kv.Key,
                        s.TextureBytes, s.RenderTextureBytes, s.MeshBytes, s.AudioBytes,
                        s.MaterialCount, s.GameObjectCount, s.MonoBehaviourCount,
                        s.LiveAssetCount, s.UnfreedBundles, s.MsPerFrame);
                }

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

            // 全MODを合計したオブジェクト数。リーク判定で最初に見るのがこの2つなのに、
            // 従来は CSV から手で足し合わせる必要があった。
            int totalGameObjects = 0;
            int totalMonoBehaviours = 0;
            foreach (ModStats s in stats.Values)
            {
                totalGameObjects += s.GameObjectCount;
                totalMonoBehaviours += s.MonoBehaviourCount;
            }

            _sink!.WriteRow(now, elapsedMin, phase, _songsPlayed, "(TOTAL)",
                textureBytes: managedHeap,            // = managedHeapMB
                renderTextureBytes: unityAllocated,   // = unityAllocatedMB（ネイティブ含む）
                meshBytes: 0,
                audioBytes: 0,
                // Unity の GC は Boehm で世代を持たないため、CollectionCount(0/1/2) は
                // 全て同じ値になる。3列に分けても情報が増えないので 1 列だけ使う。
                materialCount: GC.CollectionCount(0),  // = gcCount
                gameObjectCount: totalGameObjects,
                monoBehaviourCount: totalMonoBehaviours,
                liveAssetCount: OwnershipTracker.TrackedCount,
                // 「レート制限で帰属を諦めた累計件数」を流用。0 でない場合、
                // MOD別の数値はその分だけ (Untracked) に逃げている。
                unfreedBundles: (int)Math.Min(int.MaxValue, OwnershipTracker.SkippedLookups),
                msPerFrame: 0);
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

            var rows = new List<(string Mod, double DeltaMb, double NowMb, double MsPerFrame, int Bundles)>();
            foreach (var kv in _latest)
            {
                double now = TotalMb(kv.Value);
                double base_ = _baseline.TryGetValue(kv.Key, out ModStats b) ? TotalMb(b) : 0.0;
                rows.Add((kv.Key, now - base_, now, kv.Value.MsPerFrame, kv.Value.UnfreedBundles));
            }
            rows.Sort((x, y) => y.DeltaMb.CompareTo(x.DeltaMb));

            var sb = new System.Text.StringBuilder();
            sb.Append("<mspace=0.42em>");
            sb.AppendLine($"{_latestAt:HH:mm:ss} songs={_songsPlayed} vs {_baselinePhase}"
                        + (_baselineRank < 2 ? "  <color=#FFC864>(play 1 song)</color>" : ""));

            // 帰属を諦めた分があるなら黙って隠さない。数値が過小である可能性を明示する。
            long skipped = OwnershipTracker.SkippedLookups;
            if (skipped > 0)
                sb.AppendLine($"<color=#FFC864>rate-limited {skipped} (raise the cap)</color>");
            // ヘッダと本文は必ず同じ書式で組む。空白を手打ちすると桁がずれる。
            sb.AppendLine(Row("MOD", "+MB", "MB", "ms/f", "bnd"));

            int shown = 0;
            foreach (var r in rows)
            {
                // 増分も実測値も無いMODは行を埋めるだけなので出さない
                if (shown >= topN || (r.DeltaMb <= 0.01 && r.NowMb <= 0.01 && r.MsPerFrame <= 0.001))
                    continue;

                // 増えているMODを赤くして、目で追えるようにする
                string color = r.DeltaMb >= 1.0 ? "#FF6B6B" : "#FFFFFF";
                string line = Row(r.Mod,
                    r.DeltaMb.ToString("F1"), r.NowMb.ToString("F0"),
                    r.MsPerFrame.ToString("F2"), r.Bundles.ToString());
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
    }
}
