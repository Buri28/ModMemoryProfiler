using HarmonyLib;
using ModMemoryProfiler.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

namespace ModMemoryProfiler.Profiling
{
    // MOD別のフレーム時間を計測する。
    //
    // MOD製アセンブリに属する MonoBehaviour の Update/LateUpdate/FixedUpdate だけを Harmony で包む。
    // 全 Harmony パッチを包むより対象が桁違いに少なく、かつ「毎フレーム走るコスト」はほぼここに集まる。
    internal static class CpuProfiler
    {
        // 計測対象メソッド → MOD名
        private static readonly Dictionary<MethodBase, string> _methodOwner = new Dictionary<MethodBase, string>(512);
        // MOD名 → 累積 tick
        private static readonly Dictionary<string, long> _accumulated = new Dictionary<string, long>();
        private static readonly object _lock = new object();

        private static int _patchedMethods;
        internal static int PatchedMethods => _patchedMethods;

        internal static void Install(Harmony harmony)
        {
            if (!PluginConfig.Instance.EnableCpuProfiling)
            {
                Plugin.Log.Info("CpuProfiler: disabled by config.");
                return;
            }

            string[] excluded = PluginConfig.Instance.CpuExcludeMods
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            HarmonyMethod prefix    = new HarmonyMethod(AccessTools.Method(typeof(CpuProfiler), nameof(Prefix)));
            HarmonyMethod finalizer = new HarmonyMethod(AccessTools.Method(typeof(CpuProfiler), nameof(Finalizer)));

            string[] targetNames = { "Update", "LateUpdate", "FixedUpdate" };

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? mod = ModRegistry.Resolve(asm);
                if (mod == null)
                    continue;
                if (Array.Exists(excluded, x => mod.IndexOf(x.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = Array.FindAll(e.Types, t => t != null)!;
                }
                catch (Exception e)
                {
                    Plugin.DebugLog($"CpuProfiler: skip assembly {asm.GetName().Name}: {e.Message}");
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null || type.IsAbstract || !typeof(MonoBehaviour).IsAssignableFrom(type))
                        continue;

                    foreach (string name in targetNames)
                    {
                        // DeclaredOnly: 基底クラスのメソッドを二重に包まないため
                        MethodInfo? m = type.GetMethod(name,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, Type.EmptyTypes, null);

                        if (m == null || m.IsAbstract || m.ContainsGenericParameters)
                            continue;

                        try
                        {
                            harmony.Patch(m, prefix: prefix, finalizer: finalizer);
                            lock (_lock)
                            {
                                _methodOwner[m] = mod;
                            }
                            _patchedMethods++;
                        }
                        catch (Exception e)
                        {
                            Plugin.DebugLog($"CpuProfiler: patch failed {type.FullName}.{name}: {e.Message}");
                        }
                    }
                }
            }

            Plugin.Log.Info($"CpuProfiler: {_patchedMethods} methods patched.");
        }

        private static void Prefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        // postfix ではなく finalizer にしているのは、対象が例外を投げたときも
        // 計測を締めるため。__exception をそのまま返すことで、例外は握り潰さずに再送出される。
        private static Exception? Finalizer(long __state, MethodBase __originalMethod, Exception? __exception)
        {
            // __state が 0 = prefix が走らなかった（他MODの prefix が例外を投げた、
            // あるいは false を返して後続の prefix ごと飛ばされた）。
            // そのまま引くと現在時刻そのものが所要時間になり、その区間の ms/f が丸ごと壊れる。
            if (__state == 0)
                return __exception;

            long elapsed = Stopwatch.GetTimestamp() - __state;
            lock (_lock)
            {
                if (_methodOwner.TryGetValue(__originalMethod, out string mod))
                {
                    _accumulated.TryGetValue(mod, out long total);
                    _accumulated[mod] = total + elapsed;
                }
            }
            return __exception;
        }

        // 区間の累積値を「1フレームあたりの ms」に直して返し、カウンタをリセットする。
        internal static Dictionary<string, double> DrainMsPerFrame(int framesInInterval)
        {
            var result = new Dictionary<string, double>();
            if (framesInInterval <= 0)
                framesInInterval = 1;

            double ticksToMs = 1000.0 / Stopwatch.Frequency;
            lock (_lock)
            {
                foreach (var kv in _accumulated)
                    result[kv.Key] = kv.Value * ticksToMs / framesInInterval;
                _accumulated.Clear();
            }
            return result;
        }
    }
}
