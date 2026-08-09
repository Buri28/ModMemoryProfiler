using IPA.Loader;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace ModMemoryProfiler.Profiling
{
    // アセンブリ → MOD名 の対応を解決する。
    // BSIPA がロード済みの全プラグインからテーブルを作り、以降は O(1) で引けるようにする。
    internal static class ModRegistry
    {
        // ゲーム本体・Unity など、MODに帰属しないもの
        internal const string BaseGame = "(BaseGame)";
        // 生成元が記録されていない既存アセット（起動時から存在するもの等）
        internal const string Untracked = "(Untracked)";

        private static readonly Dictionary<Assembly, string> _assemblyToMod = new Dictionary<Assembly, string>();
        private static readonly List<string> _modNames = new List<string>();

        internal static IReadOnlyList<string> ModNames => _modNames;

        internal static void Build()
        {
            _assemblyToMod.Clear();
            _modNames.Clear();

            foreach (PluginMetadata meta in PluginManager.EnabledPlugins)
            {
                Assembly? asm;
                try
                {
                    asm = meta.Assembly;
                }
                catch (Exception e)
                {
                    // アセンブリが未ロードのプラグインは無視する
                    Plugin.DebugLog($"skip plugin '{meta.Name}': {e.Message}");
                    continue;
                }

                if (asm == null)
                    continue;

                string name = string.IsNullOrEmpty(meta.Name) ? asm.GetName().Name : meta.Name;
                _assemblyToMod[asm] = name;
                if (!_modNames.Contains(name))
                    _modNames.Add(name);
            }

            // 自分自身も計測対象に含める（このMOD自体がリークしていないかの確認用）
            Assembly self = typeof(ModRegistry).Assembly;
            if (!_assemblyToMod.ContainsKey(self))
            {
                _assemblyToMod[self] = "ModMemoryProfiler";
                _modNames.Add("ModMemoryProfiler");
            }

            _modNames.Sort(StringComparer.OrdinalIgnoreCase);
            Plugin.Log.Info($"ModRegistry: {_modNames.Count} mods registered.");
        }

        // アセンブリからMOD名を引く。MODでなければ null。
        internal static string? Resolve(Assembly? asm)
        {
            if (asm == null)
                return null;
            return _assemblyToMod.TryGetValue(asm, out string name) ? name : null;
        }

        // 型からMOD名を引く。MOD製の型でなければ BaseGame。
        internal static string ResolveType(Type? type)
        {
            if (type == null)
                return BaseGame;
            return Resolve(type.Assembly) ?? BaseGame;
        }

        internal static bool IsModAssembly(Assembly? asm) => Resolve(asm) != null;
    }
}
