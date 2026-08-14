using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.GameplaySetup;
using ModMemoryProfiler.Configuration;
using ModMemoryProfiler.Profiling;
using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ModMemoryProfiler.UI
{
    // ゲーム内タブ。曲選択画面左側の GameplaySetup にタブを1枚足す。
    //
    // 設計上このMODはCSVが主成果物で、UIは「今どのMODが伸びているか」をその場で見るための窓。
    // 重い走査（MemoryCensus）はタブを開いただけでは走らせず、Refresh を押したときだけ実行する。
    // 表示自体は最後に取れているスナップショットの使い回しなので、開くコストはゼロに近い。
    public class ProfilerSettingsController : MonoBehaviour, INotifyPropertyChanged
    {
        // ランキングに出す最大行数。ここが実質的な高さ調整つまみ
        // （bsml 側で高さを指定するとレイアウトが壊れるため。ProfilerSettings.bsml のコメント参照）。
        private const int TopModCount = 8;

        public event PropertyChangedEventHandler? PropertyChanged;

        private static bool _tabAdded;

        // GameplaySetup.Instance が使えるようになるまでの最大待ち時間（秒）。
        // 超えたら諦めてログに残す（黙って UI が出ないのが一番困るため）。
        private const float TabWaitTimeoutSeconds = 180f;

        private IEnumerator Start()
        {
            if (_tabAdded)
                yield break;

            // 注意: GameplaySetup.Instance は BSML の ZenjectSingleton で、DiContainer が
            // 出来る前に触ると null を返さず InvalidOperationException を投げる。
            // このMODは Zenject を使わず起動直後に自前でコンポーネントを載せるので、
            // メニューシーンより先に来てしまう。null 判定ではなく例外を飲んで待つ必要がある。
            GameplaySetup? setup = null;
            float waited = 0f;

            while (setup == null)
            {
                try
                {
                    setup = GameplaySetup.Instance;
                }
                catch
                {
                    setup = null; // まだコンテナが無い。次の周回で再試行する。
                }

                if (setup != null)
                    break;

                if (waited >= TabWaitTimeoutSeconds)
                {
                    Plugin.Log.Warn($"ProfilerSettingsController: GameplaySetup unavailable after {waited:F0}s. Tab not added.");
                    yield break;
                }

                yield return new WaitForSeconds(1f);
                waited += 1f;
            }

            try
            {
                setup.AddTab(
                    "MemProfiler",
                    "ModMemoryProfiler.Resources.ProfilerSettings.bsml",
                    this,
                    MenuType.All);
                _tabAdded = true;
                Plugin.Log.Info($"ProfilerSettingsController: tab added after {waited:F0}s.");
            }
            catch (System.Exception e)
            {
                Plugin.Log.Error($"ProfilerSettingsController: AddTab failed: {e}");
            }
        }

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        // ── 計測結果の表示 ────────────────────────────────────────

        [UIValue("reportText")]
        public string ReportText
            => SessionRecorder.Instance?.BuildReport(TopModCount) ?? "Profiler is not running.";

        [UIAction("on-refresh")]
        private void OnRefresh()
        {
            SessionRecorder.Instance?.SnapshotNow();
            NotifyPropertyChanged(nameof(ReportText));
        }

        // GC + Resources.UnloadUnusedAssets を今すぐ実行する。
        // GC 単体では Unity のアセット（テクスチャ・スプライト等）は解放されないため、
        // 「GCボタン」ではなくこの2段構えである必要がある。
        [UIAction("on-unload")]
        private void OnUnload()
        {
            SessionRecorder.Instance?.UnloadNow();
            NotifyPropertyChanged(nameof(ReportText));
        }

        // カバー画像キャッシュを空にしてから解放する。
        // Free Assets だけでは、キャッシュが参照を握っている分は解放されない。
        [UIAction("on-purge")]
        private void OnPurge()
        {
            SessionRecorder.Instance?.PurgeCoverCacheNow();
            NotifyPropertyChanged(nameof(ReportText));
        }

        // 積み上がっているアセットの名前一覧を UserData に書き出す。
        // 「カバー画像なのか UI アイコンなのか」を名前で判別するための診断。
        [UIAction("on-dump")]
        private void OnDump()
        {
            SessionRecorder.Instance?.DumpAssets();
            NotifyPropertyChanged(nameof(ReportText));
        }

        [UIAction("FormatInt")]
        private string FormatInt(float value) => ((int)value).ToString();

        // ── 設定 ──────────────────────────────────────────────────
        //
        // BSIPA の生成済み Config なので、setter を呼んだ時点で
        // UserData/ModMemoryProfiler.json への保存も走る。

        [UIValue("sampleDuringSong")]
        public bool SampleDuringSong
        {
            get => PluginConfig.Instance.SampleDuringSong;
            set { PluginConfig.Instance.SampleDuringSong = value; NotifyPropertyChanged(); }
        }

        // 次の曲終了時から効く（フラグを見るのが曲終了のタイミングのため）。
        [UIValue("unloadUnusedAssetsOnMenu")]
        public bool UnloadUnusedAssetsOnMenu
        {
            get => PluginConfig.Instance.UnloadUnusedAssetsOnMenu;
            set { PluginConfig.Instance.UnloadUnusedAssetsOnMenu = value; NotifyPropertyChanged(); }
        }

        [UIValue("trackOwnership")]
        public bool TrackOwnership
        {
            get => PluginConfig.Instance.TrackOwnership;
            set { PluginConfig.Instance.TrackOwnership = value; NotifyPropertyChanged(); }
        }

        // 起動時にフックを仕掛けるかどうかの判断に使うため、実際に効くのは次回起動から。
        [UIValue("trackInstantiate")]
        public bool TrackInstantiate
        {
            get => PluginConfig.Instance.TrackInstantiate;
            set { PluginConfig.Instance.TrackInstantiate = value; NotifyPropertyChanged(); }
        }

        // 同上。Harmony パッチは起動時に一括で当てるため、次回起動から反映される。
        [UIValue("enableCpuProfiling")]
        public bool EnableCpuProfiling
        {
            get => PluginConfig.Instance.EnableCpuProfiling;
            set { PluginConfig.Instance.EnableCpuProfiling = value; NotifyPropertyChanged(); }
        }

        [UIValue("debugLogging")]
        public bool DebugLogging
        {
            get => PluginConfig.Instance.DebugLogging;
            set { PluginConfig.Instance.DebugLogging = value; NotifyPropertyChanged(); }
        }

        [UIValue("sampleIntervalSeconds")]
        public int SampleIntervalSeconds
        {
            get => PluginConfig.Instance.SampleIntervalSeconds;
            set { PluginConfig.Instance.SampleIntervalSeconds = value; NotifyPropertyChanged(); }
        }

        [UIAction("on-sample-interval-changed")]
        private void OnSampleIntervalChanged(float value) => SampleIntervalSeconds = (int)value;
    }
}
