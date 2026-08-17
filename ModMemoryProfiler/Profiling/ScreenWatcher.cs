using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace ModMemoryProfiler.Profiling
{
    // 「どの画面を開いたときに、何個のオブジェクトが増えたか」を自動で記録する。
    //
    // 実測で分かったのは、増加が一定ペースの漏れではなく段差だということ。
    // ある画面を開いた瞬間に数千個が生成され、閉じても破棄されない。
    // ところが 30 秒間隔のスナップショットでは、段差の前後がどの画面だったのか分からない。
    //
    // そこで画面の切り替わりをフックし、遷移が落ち着いたところで
    // phase="view:<画面名>" のスナップショットを1本取る。
    // これで CSV を見るだけで「この画面が N 個残す」が直接読める。
    // 手作業で Dump を押して回る必要がなくなる。
    //
    // 型はゲームのバージョンで変わりうるため、コンパイル時に依存させず
    // 全てリフレクションで解決する。見つからなければ黙って無効になるだけにする。
    internal static class ScreenWatcher
    {
        // 1.40.8 では HMUI.dll ではなく BeatSaber.ViewSystem.dll に入っている。
        // AccessTools.TypeByName は読み込み済みアセンブリ全体から探すので、
        // どの DLL にあるかは書かなくてよい。
        private const string ViewControllerTypeName = "HMUI.ViewController";

        // 画面遷移では複数の ViewController が数百ms のうちに次々と有効化される。
        // 1つ1つで走査すると重いだけで読みにくいので、静まるまで待ってから1本だけ取る。
        private const float SettleSeconds = 1.5f;

        // 遷移中に有効化された画面名。最大数個を連結して phase に載せる。
        // 実測では1回の遷移で13個が同時に有効化され、3個では犯人候補が "+10more" に
        // 隠れて読めなかったため広げてある。
        private const int MaxNamesInPhase = 6;

        private static readonly object _lock = new object();
        private static readonly List<string> _pending = new List<string>();
        private static bool _dirty;
        private static float _lastChangeTime;

        internal static bool Available { get; private set; }

        internal static void Install(Harmony harmony)
        {
            try
            {
                Type? vc = AccessTools.TypeByName(ViewControllerTypeName);
                if (vc == null)
                {
                    Plugin.Log.Info($"ScreenWatcher: {ViewControllerTypeName} not found. Disabled.");
                    return;
                }

                // DidActivate ではなく __Activate をフックする。
                // DidActivate は virtual で、派生クラスが base を呼ばない実装だと取りこぼす。
                // __Activate はフレームワーク側の入口なので必ず通る。
                MethodInfo? activate = AccessTools.Method(vc, "__Activate");
                if (activate == null)
                {
                    Plugin.Log.Info("ScreenWatcher: __Activate not found. Disabled.");
                    return;
                }

                harmony.Patch(activate,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(ScreenWatcher), nameof(Postfix_Activate))));

                Available = true;
                Plugin.Log.Info("ScreenWatcher: ready.");
            }
            catch (Exception e)
            {
                Plugin.Log.Warn($"ScreenWatcher: install failed: {e.Message}");
                Available = false;
            }
        }

        private static void Postfix_Activate(object __instance)
        {
            if (__instance == null)
                return;

            try
            {
                string name = __instance.GetType().Name;

                lock (_lock)
                {
                    if (!_pending.Contains(name))
                        _pending.Add(name);
                    _dirty = true;
                    _lastChangeTime = UnityEngine.Time.realtimeSinceStartup;
                }
            }
            catch { /* 計測が本編を壊さないこと最優先 */ }
        }

        // 遷移が静まっていれば、その画面名を返して状態をリセットする。
        // まだ動いている／何も起きていない場合は null。
        internal static string? ConsumeSettledPhase()
        {
            lock (_lock)
            {
                if (!_dirty)
                    return null;
                if (UnityEngine.Time.realtimeSinceStartup - _lastChangeTime < SettleSeconds)
                    return null;

                string phase = BuildPhase(_pending);
                _pending.Clear();
                _dirty = false;
                return phase;
            }
        }

        // 曲の開始・終了でも ViewController は動くので、そちらで既に記録されるぶんは捨てる。
        internal static void Discard()
        {
            lock (_lock)
            {
                _pending.Clear();
                _dirty = false;
            }
        }

        private static string BuildPhase(List<string> names)
        {
            var sb = new System.Text.StringBuilder("view:");
            int n = Math.Min(names.Count, MaxNamesInPhase);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append('+');
                sb.Append(Sanitize(names[i]));
            }
            if (names.Count > n)
                sb.Append("+").Append(names.Count - n).Append("more");
            return sb.ToString();
        }

        // phase 列は CSV にそのまま出るので、区切り文字になりうる文字は落とす。
        // 名前が長いと列が読みにくくなるだけなので頭だけ残す。
        private static string Sanitize(string name)
        {
            if (name.Length > 28)
                name = name.Substring(0, 28);

            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(c == ',' || c == '"' || c == '\n' || c == '\r' ? '_' : c);
            return sb.ToString();
        }
    }
}
