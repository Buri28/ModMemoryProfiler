using HarmonyLib;
using System;
using System.Collections;
using System.Reflection;

namespace ModMemoryProfiler.Profiling
{
    // SongCore が読み込み済みのカスタム曲数を読む。
    //
    // 曲数を変えて比較する実験を何度かやったが、CSV には条件が残らないため
    // 「どちらが曲ありだったか」が後から判別できず、丸ごと無駄になった。
    // 実測値として毎行に残しておけば、条件の取り違えは起こらない。
    //
    // さらに読み込みは非同期で、0 から実際の曲数まで数十秒かけて増える。
    // その推移が残るので「読み込みが終わる前に終了してしまった」も判別できる。
    //
    // SongCore が無い／APIが変わった場合は -1 を返すだけで、計測は止めない。
    internal static class SongCoreProbe
    {
        private const string LoaderTypeName = "SongCore.Loader";

        private static bool _resolved;
        private static FieldInfo? _customLevels;
        private static PropertyInfo? _areSongsLoaded;

        // 読み込み済みカスタム曲数。取得できない場合は -1。
        internal static int CustomLevelCount()
        {
            Resolve();
            if (_customLevels == null)
                return -1;

            try
            {
                // ConcurrentDictionary は ICollection を実装しているので Count が読める。
                // 型そのものに依存するとバージョン差で壊れるため、インターフェース経由で読む。
                if (_customLevels.GetValue(null) is ICollection c)
                    return c.Count;
            }
            catch { /* 計測が本編を壊さないこと最優先 */ }

            return -1;
        }

        // 読み込みが完了しているか。判定できない場合は null。
        internal static bool? AreSongsLoaded()
        {
            Resolve();
            if (_areSongsLoaded == null)
                return null;

            try { return _areSongsLoaded.GetValue(null) as bool?; }
            catch { return null; }
        }

        private static void Resolve()
        {
            if (_resolved)
                return;
            _resolved = true;

            try
            {
                Type? loader = AccessTools.TypeByName(LoaderTypeName);
                if (loader == null)
                {
                    Plugin.Log.Info("SongCoreProbe: SongCore.Loader not found. customLevelCount will stay -1.");
                    return;
                }

                _customLevels = AccessTools.Field(loader, "CustomLevels");
                _areSongsLoaded = AccessTools.Property(loader, "AreSongsLoaded");

                if (_customLevels == null)
                    Plugin.Log.Info("SongCoreProbe: CustomLevels field not found. customLevelCount will stay -1.");
                else
                    Plugin.Log.Info("SongCoreProbe: ready.");
            }
            catch (Exception e)
            {
                Plugin.Log.Warn($"SongCoreProbe: resolve failed: {e.Message}");
            }
        }
    }
}
