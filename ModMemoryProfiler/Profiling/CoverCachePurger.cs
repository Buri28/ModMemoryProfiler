using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace ModMemoryProfiler.Profiling
{
    // 曲のカバー画像キャッシュを明示的に空にする。
    //
    // ゲーム本体の SpriteAsyncLoader が読み込んだカバー画像を保持し続けるため、
    // Resources.UnloadUnusedAssets を呼んでも「参照が生きている」として解放されない。
    // 実測でも解放量はほぼゼロだった。
    //
    // SpriteAsyncLoader 自身が ClearCache() を持っているので、それを呼んで参照を切ってから
    // 解放すれば回収できる、というのがここの狙い。
    //
    // SpriteAsyncLoader は Unity オブジェクトではない素の C# クラスなので
    // Resources.FindObjectsOfTypeAll では見つけられない。そこで読み込みメソッドをフックして
    // インスタンスを捕獲しておく。
    //
    // 型やメンバはゲームのバージョンで変わりうるため、コンパイル時に依存させず
    // 全てリフレクションで解決する。見つからなければ黙って無効になるだけにする。
    internal static class CoverCachePurger
    {
        private const string LoaderTypeName = "SpriteAsyncLoader";
        private const string LoadMethodName = "LoadSpriteAsync";
        private const string ClearMethodName = "ClearCache";

        private static readonly List<WeakReference> _loaders = new List<WeakReference>();
        private static readonly object _lock = new object();

        private static MethodInfo? _clearMethod;

        internal static bool Available => _clearMethod != null;

        internal static void Install(Harmony harmony)
        {
            try
            {
                Type? loaderType = AccessTools.TypeByName(LoaderTypeName);
                if (loaderType == null)
                {
                    Plugin.Log.Info($"CoverCachePurger: {LoaderTypeName} not found. Disabled.");
                    return;
                }

                _clearMethod = AccessTools.Method(loaderType, ClearMethodName);
                MethodInfo? loadMethod = AccessTools.Method(loaderType, LoadMethodName);

                if (_clearMethod == null || loadMethod == null)
                {
                    Plugin.Log.Info($"CoverCachePurger: {ClearMethodName}/{LoadMethodName} not found. Disabled.");
                    _clearMethod = null;
                    return;
                }

                harmony.Patch(loadMethod,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CoverCachePurger), nameof(Postfix_Capture))));

                Plugin.Log.Info("CoverCachePurger: ready.");
            }
            catch (Exception e)
            {
                Plugin.Log.Warn($"CoverCachePurger: install failed: {e.Message}");
                _clearMethod = null;
            }
        }

        // 読み込みのたびに呼ばれる。インスタンスは通常1個だが、
        // 複数あっても取りこぼさないように集合で持つ。
        private static void Postfix_Capture(object __instance)
        {
            if (__instance == null)
                return;

            try
            {
                lock (_lock)
                {
                    foreach (WeakReference wr in _loaders)
                    {
                        if (ReferenceEquals(wr.Target, __instance))
                            return; // 既知
                    }
                    _loaders.Add(new WeakReference(__instance));
                }
            }
            catch { /* 計測が本編を壊さないこと最優先 */ }
        }

        // 捕獲済みの全ローダーのキャッシュを空にする。戻り値は成功した個数。
        // 参照を切るだけで、実際の解放は続けて呼ぶ Resources.UnloadUnusedAssets が行う。
        internal static int ClearAll()
        {
            if (_clearMethod == null)
                return 0;

            var alive = new List<WeakReference>();
            int cleared = 0;

            lock (_lock)
            {
                foreach (WeakReference wr in _loaders)
                {
                    object? target = wr.Target;
                    if (target == null)
                        continue; // 既に回収済み

                    alive.Add(wr);
                    try
                    {
                        _clearMethod.Invoke(target, null);
                        cleared++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.Warn($"CoverCachePurger: ClearCache failed: {e.Message}");
                    }
                }

                _loaders.Clear();
                _loaders.AddRange(alive);
            }

            return cleared;
        }

        internal static int KnownLoaderCount
        {
            get { lock (_lock) { return _loaders.Count; } }
        }
    }
}
