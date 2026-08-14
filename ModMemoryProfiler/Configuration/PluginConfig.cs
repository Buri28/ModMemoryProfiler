using IPA.Config.Stores;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo(GeneratedStore.AssemblyVisibilityTarget)]

namespace ModMemoryProfiler.Configuration
{
    // ModMemoryProfiler の設定。UserData/ModMemoryProfiler.json に永続化される。
    public class PluginConfig
    {
        public static PluginConfig Instance { get; set; } = null!;

        // MOD 有効/無効。false で計測もフックも一切行わない。
        public virtual bool Enabled { get; set; } = true;

        // 詳細ログ。true のとき Debug レベルの診断ログを出す。
        public virtual bool DebugLogging { get; set; } = false;

        // ゲーム内タブ（曲選択画面の GameplaySetup）を表示するか。
        // 表示は最後のスナップショットの使い回しなので、開いていても計測負荷は増えない。
        public virtual bool ShowInGameTab { get; set; } = true;

        // ── サンプリング ──────────────────────────────────────────
        // スナップショット取得間隔（秒）。
        public virtual int SampleIntervalSeconds { get; set; } = 30;

        // 曲中にもスナップショットを取るか。
        // 走査は数万オブジェクトを舐めるためフレーム落ちの可能性がある。既定は false（メニューのみ）。
        // 曲終了直後のスナップショットは、この設定に関わらず必ず取得する（リーク判定の基準になるため）。
        public virtual bool SampleDuringSong { get; set; } = false;

        // ── メモリ計測 ────────────────────────────────────────────
        // MonoBehaviour のインスタンス数を MOD 別に数える。
        // 型からアセンブリが直接引けるので帰属が正確。MOD製オブジェクトの解放漏れが直接見える。
        public virtual bool CountMonoBehaviours { get; set; } = true;

        // Profiler.GetRuntimeMemorySizeLong を使う。false なら常に概算式にフォールバックする。
        public virtual bool UseUnityProfilerApi { get; set; } = true;

        // ── 解放の検証 ────────────────────────────────────────────
        // 曲終了後のメニュー復帰時に GC.Collect() → Resources.UnloadUnusedAssets() を実行する。
        //
        // Unity は参照が切れたアセットを自動では解放せず、この API を呼ぶかシーンが
        // 破棄されるまでメモリに残し続ける。カバー画像などが「参照は切れているのに
        // 残っているだけ」なのか「誰かが掴んでいる本物のリーク」なのかを切り分けられる。
        //
        // 実行前後のスナップショットを両方 CSV に残すので、回収量と所要時間が分かる。
        // 数百ms〜秒単位かかる重い処理のため、曲中には絶対に実行しない。既定 OFF。
        public virtual bool UnloadUnusedAssetsOnMenu { get; set; } = false;

        // ── 所有者トラッキング ────────────────────────────────────
        // アセット生成時にスタックトレースを辿って生成元MODを記録する。
        // これを切ると「MOD別」ではなく全体量しか出なくなる。
        public virtual bool TrackOwnership { get; set; } = true;

        // Object.Instantiate もフックするか。
        // 複製されたオブジェクトの持ち主が分かる反面、ノーツ生成などで毎フレーム走る最も熱いパスなので、
        // 曲中が重くなる場合はここだけ切れば他の計測は生かしたまま負荷を落とせる。
        public virtual bool TrackInstantiate { get; set; } = true;

        // 1秒あたりのスタックトレース取得回数の上限（過負荷の保険）。
        //
        // 超えた分は記録を見送り、そのアセットは (Untracked) に落ちる。
        // 上限は全MOD共通の早い者勝ちなので、大量に確保するMOD＝リーク候補ほど
        // 見送られやすいという偏りが原理的に残る。見送り件数は UI と CSV に出しているので、
        // そこが 0 でないまま増え続ける場合はこの値を上げて測り直すこと。
        public virtual int MaxOwnershipLookupsPerSecond { get; set; } = 2000;

        // 曲中でないときに上限を何倍まで緩めるか。
        // オブジェクトの大量生成はシーン遷移・メニューのロード時に集中するため、
        // そこを間引くと帰属が (Untracked) に落ちて調査にならない。
        // 曲中でなければフレーム落ちは実害が小さいので、正確さを優先する。
        public virtual int OutOfSongLookupMultiplier { get; set; } = 20;

        // ── CPU 計測 ──────────────────────────────────────────────
        // MOD製 MonoBehaviour の Update/LateUpdate/FixedUpdate を計測する。
        public virtual bool EnableCpuProfiling { get; set; } = true;

        // 計測対象から除外するMOD名（; 区切り、部分一致）。重すぎる場合の逃げ道。
        public virtual string CpuExcludeMods { get; set; } = "";

        public virtual void Changed() { }

        public virtual void OnReload() { }
    }
}
