using HarmonyLib;
using IPA;
using IPA.Config;
using IPA.Config.Stores;
using ModMemoryProfiler.Configuration;
using ModMemoryProfiler.Profiling;
using IPALogger = IPA.Logging.Logger;

namespace ModMemoryProfiler
{
    // IPAプラグインのエントリーポイント。BeatSaber起動時に1回だけ初期化される。
    //
    // このMODは診断専用。ゲームの挙動には一切干渉せず、計測結果を CSV に出すだけ。
    // FPS/フレームタイムの画面表示は意図的に実装していない（既存MODと役割が重複するため）。
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        internal static IPALogger Log { get; private set; } = null!;
        internal static Harmony HarmonyInstance { get; private set; } = null!;
        internal static PluginConfig Config { get; private set; } = null!;

        // 詳細ログ。Config.DebugLogging が ON のときだけ Debug レベルで出力する（既定OFF）。
        internal static void DebugLog(string message)
        {
            if (Config != null && Config.DebugLogging)
                Log.Debug(message);
        }

        [Init]
        public Plugin(IPALogger logger, Config conf)
        {
            Log             = logger;
            Config          = conf.Generated<PluginConfig>();
            PluginConfig.Instance = Config;
            HarmonyInstance = new Harmony("com.buri28.modmemoryprofiler");
        }

        // アプリ起動時（Init後）に呼ばれる。
        // 他のMODが全てロードされた後でないとアセンブリ一覧が揃わないため、
        // 計測の初期化は Init ではなくここで行う。
        [OnStart]
        public void OnApplicationStart()
        {
            if (!Config.Enabled)
            {
                Log.Info("ModMemoryProfiler is disabled by config.");
                return;
            }

            // 1. どのアセンブリがどのMODかの対応表を作る
            ModRegistry.Build();

            // 2. アセット生成をフックして「持ち主」の記録を開始する
            if (Config.TrackOwnership)
                OwnershipTracker.Install(HarmonyInstance);

            // 3. MOD製 MonoBehaviour のフレーム時間計測を仕掛ける
            CpuProfiler.Install(HarmonyInstance);

            // 4. 常駐して定期スナップショットを取る
            SessionRecorder.Create();

            // 5. ゲーム内タブを足す（SessionRecorder と同じ常駐オブジェクトに載せる）
            if (Config.ShowInGameTab)
                SessionRecorder.Instance!.gameObject.AddComponent<UI.ProfilerSettingsController>();

            Log.Info("ModMemoryProfiler started.");
        }

        [OnExit]
        public void OnApplicationQuit()
        {
            SessionRecorder.Destroy();
            HarmonyInstance.UnpatchSelf();
            Log.Info("ModMemoryProfiler exited.");
        }
    }
}
