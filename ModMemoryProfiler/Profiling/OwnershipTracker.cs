using HarmonyLib;
using ModMemoryProfiler.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ModMemoryProfiler.Profiling
{
    // ★中核その1: アセットが「どのMODによって生成されたか」を生成の瞬間に記録する。
    //
    // Mono の GC ヒープは全MOD共有なので、後からメモリを見ても持ち主は分からない。
    // そこで生成をフックし、その時点のスタックトレースを遡って最初に見つかったMOD製アセンブリを
    // 持ち主として instanceID に紐づけて覚えておく。
    // MemoryCensus はこの対応表を引いて、実バイト数をMOD別に合計する。
    internal static class OwnershipTracker
    {
        // instanceID → MOD名。MemoryCensus が数える型だけを入れる（Prune の項を参照）。
        private static readonly Dictionary<int, string> _owners = new Dictionary<int, string>(4096);
        private static readonly object _lock = new object();

        // AssetBundle は _owners とは別テーブルで持つ。
        // センサスは AssetBundle を列挙しないので、_owners に混ぜると Prune に消され、
        // Unload 時に持ち主が引けなくなって「全MODが全バンドルをリーク」と誤報する。
        private static readonly Dictionary<int, string> _bundleOwners = new Dictionary<int, string>();

        // MOD別の AssetBundle ロード数 / アンロード数。差分＝未解放バンドル数。
        private static readonly Dictionary<string, int> _bundleLoads = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _bundleUnloads = new Dictionary<string, int>();

        // レート制限用。スタック走査中に _lock を握らないよう、専用の細かいロックで守る。
        private static readonly object _rateLock = new object();
        private static long _windowStartTicks;
        private static int _lookupsInWindow;
        private static long _skippedLookups;

        // レート制限で持ち主を諦めた回数。0 でない間は帰属が不完全なので、UI と CSV に出して知らせる。
        internal static long SkippedLookups { get { lock (_rateLock) { return _skippedLookups; } } }

        private static Harmony? _harmony;
        private static int _hooksApplied;

        internal static int HooksApplied => _hooksApplied;

        internal static void Install(Harmony harmony)
        {
            _harmony = harmony;
            _windowStartTicks = Stopwatch.GetTimestamp();

            HarmonyMethod postfixInstance = new HarmonyMethod(AccessTools.Method(typeof(OwnershipTracker), nameof(Postfix_Instance)));
            HarmonyMethod postfixResult   = new HarmonyMethod(AccessTools.Method(typeof(OwnershipTracker), nameof(Postfix_Result)));

            // ── コンストラクタ（生成されたインスタンス自身が __instance で取れる） ──
            PatchAllConstructors(typeof(Texture2D), postfixInstance);
            PatchAllConstructors(typeof(RenderTexture), postfixInstance);
            PatchAllConstructors(typeof(Material), postfixInstance);
            PatchAllConstructors(typeof(Mesh), postfixInstance);
            PatchAllConstructors(typeof(Cubemap), postfixInstance);

            // AudioClip には public なコンストラクタが無く、生成は必ず Create を通る
            PatchIfExists(typeof(AudioClip), "Create", postfixResult);

            // GameObject をコードで直接生成する経路。これが無いと GameObject の増加が
            // まるごと (Untracked) に落ちて、誰が作ったのか分からなくなる。
            // （Instantiate 経由の複製は下の Instantiate フックで拾える）
            PatchAllConstructors(typeof(GameObject), postfixInstance);
            PatchIfExists(typeof(GameObject), "CreatePrimitive", postfixResult);

            // Sprite にも public なコンストラクタが無く、コードからの生成は Create を通る。
            // アイコン類の積み上がりを名指しするための本命フック。
            PatchIfExists(typeof(Sprite), "Create", postfixResult);

            // Instantiate で複製されたオブジェクトも、複製した側の持ち物として数える。
            // ジェネリック版 Instantiate<T> は内部で非ジェネリック版に落ちるので、こちらだけ見れば足りる。
            if (PluginConfig.Instance.TrackInstantiate)
                PatchIfExists(typeof(Object), "Instantiate", postfixResult);

            // ── AssetBundle からのロード（戻り値が __result で取れる） ──
            // 本命の容疑（バンドル解放漏れ）を直接見るため、バンドル自体も追跡する。
            PatchIfExists(typeof(AssetBundle), "LoadFromFile", postfixResult);
            PatchIfExists(typeof(AssetBundle), "LoadFromMemory", postfixResult);
            PatchIfExists(typeof(AssetBundle), "LoadFromStream", postfixResult);
            PatchIfExists(typeof(AssetBundle), "LoadAsset_Internal", postfixResult);
            PatchIfExists(typeof(AssetBundle), "LoadAssetWithSubAssets_Internal", postfixResult);
            PatchIfExists(typeof(AssetBundle), "LoadAssetAsync_Internal", null); // 非同期は完了時に取れないので対象外

            // ── バンドルの解放を数える ──
            HarmonyMethod prefixUnload = new HarmonyMethod(AccessTools.Method(typeof(OwnershipTracker), nameof(Prefix_BundleUnload)));
            PatchIfExists(typeof(AssetBundle), "Unload", prefixUnload, isPrefix: true);

            Plugin.Log.Info($"OwnershipTracker: {_hooksApplied} hooks applied.");
        }

        private static void PatchAllConstructors(Type type, HarmonyMethod postfix)
        {
            foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                TryPatch(ctor, postfix, isPrefix: false);
            }
        }

        private static void PatchIfExists(Type type, string methodName, HarmonyMethod? patch, bool isPrefix = false)
        {
            if (patch == null)
                return;

            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            foreach (MethodInfo m in methods)
            {
                if (m.Name != methodName || m.IsGenericMethodDefinition)
                    continue;
                TryPatch(m, patch, isPrefix);
            }
        }

        // Unity の型には native 実装が絡んでパッチできないものがある。
        // 1つ失敗しても他が生きるように、必ず個別に try/catch する。
        private static void TryPatch(MethodBase target, HarmonyMethod patch, bool isPrefix)
        {
            try
            {
                if (isPrefix)
                    _harmony!.Patch(target, prefix: patch);
                else
                    _harmony!.Patch(target, postfix: patch);
                _hooksApplied++;
                Plugin.DebugLog($"hooked {target.DeclaringType?.Name}.{target.Name}");
            }
            catch (Exception e)
            {
                Plugin.DebugLog($"hook failed {target.DeclaringType?.Name}.{target.Name}: {e.Message}");
            }
        }

        // ── Harmony パッチ本体 ───────────────────────────────────────

        private static void Postfix_Instance(object __instance)
        {
            if (__instance is Object uo)
                Register(uo);
        }

        private static void Postfix_Result(object __result)
        {
            if (__result is Object uo)
                Register(uo);
            else if (__result is Object[] arr)
            {
                foreach (Object o in arr)
                    Register(o);
            }
        }

        private static void Prefix_BundleUnload(AssetBundle __instance)
        {
            try
            {
                int id = __instance.GetInstanceID();
                lock (_lock)
                {
                    if (!_bundleOwners.TryGetValue(id, out string owner))
                        owner = ModRegistry.Untracked;
                    else
                        _bundleOwners.Remove(id); // 解放済みなので対応表からも落とす

                    _bundleUnloads.TryGetValue(owner, out int n);
                    _bundleUnloads[owner] = n + 1;
                }
            }
            catch { /* 計測が本編を壊さないこと最優先 */ }
        }

        // ── 登録 ─────────────────────────────────────────────────────

        private static void Register(Object? obj)
        {
            if (obj == null || !PluginConfig.Instance.TrackOwnership)
                return;

            try
            {
                bool isBundle = obj is AssetBundle;

                // センサスが数えない型（Sprite / Shader / ScriptableObject 等）は記録しない。
                // 記録しても集計に使われない上、Prune が生存判定できず消してしまうか、
                // 消せないまま対応表だけが太っていく（＝このツール自身がリークする）。
                if (!isBundle && !IsCensusType(obj))
                    return;

                string? owner = ResolveOwner();
                if (owner == null)
                    return; // レート制限。持ち主不明として (Untracked) に落ちる

                int id = obj.GetInstanceID();
                if (id == 0)
                    return; // native 側が未生成。この時点の instanceID は意味を持たない

                lock (_lock)
                {
                    if (isBundle)
                    {
                        _bundleOwners[id] = owner;
                        _bundleLoads.TryGetValue(owner, out int n);
                        _bundleLoads[owner] = n + 1;
                    }
                    else
                    {
                        _owners[id] = owner;
                    }
                }
            }
            catch { /* 同上 */ }
        }

        // MemoryCensus.TakeSnapshot が列挙する型と必ず一致させること。
        // ここと向こうがずれると Prune が生きている対応を消す。
        private static bool IsCensusType(Object obj)
            => obj is Texture || obj is Sprite || obj is Mesh || obj is AudioClip
            || obj is Material || obj is GameObject;

        // スタックトレースを外側へ遡り、最初に見つかったMOD製アセンブリを持ち主とする。
        // レート制限に掛かった場合は null（＝記録を見送る）。
        //
        // 【呼び出し元キャッシュを持たない理由】
        // 以前は「呼び出し元メソッド → MOD名」をキャッシュしてスタック走査を省いていたが、
        // これは原理的に成立しない。パッチ直後のフレームは Texture2D..ctor のような
        // Unity 側の共有メソッドであり、全MODで同一のキーになる。
        // 結果、最初にテクスチャを作ったMODが以降すべてのテクスチャの持ち主として返され、
        // MOD別帰属そのものが壊れる。速度より正しさを取り、毎回走査する。
        // 負荷対策はレート制限に一本化した。
        private static string? ResolveOwner()
        {
            // レート制限: 高頻度に生成するMODがいてもゲームを止めないための保険。
            // 複数スレッドから来るので、カウンタはロックで守る（走査自体はロック外で行う）。
            //
            // 上限は曲中のみ厳しく適用する。オブジェクトが大量に生成されるのはシーン遷移や
            // メニューのロード時であり、そこを間引くと帰属がまるごと (Untracked) に落ちて
            // 「誰が作ったか」が分からなくなる。曲中でなければ多少のフレーム落ちより
            // 帰属の正確さを優先する（元々ロード中で処理が重い場面でもある）。
            int cap = PluginConfig.Instance.MaxOwnershipLookupsPerSecond;
            if (!SessionRecorder.IsInSong)
                cap *= PluginConfig.Instance.OutOfSongLookupMultiplier;

            lock (_rateLock)
            {
                long now = Stopwatch.GetTimestamp();
                if (now - _windowStartTicks > Stopwatch.Frequency)
                {
                    _windowStartTicks = now;
                    _lookupsInWindow = 0;
                }
                if (++_lookupsInWindow > cap)
                {
                    _skippedLookups++;
                    return null;
                }
            }

            StackTrace st = new StackTrace(2, false); // 自分のパッチフレームを飛ばす
            Assembly selfAsm = typeof(OwnershipTracker).Assembly;

            int frames = st.FrameCount;
            for (int i = 0; i < frames; i++)
            {
                Type? declaring = st.GetFrame(i)?.GetMethod()?.DeclaringType;
                if (declaring == null)
                    continue;

                Assembly asm = declaring.Assembly;
                if (asm == selfAsm)
                    continue; // 自分自身のフレームは無視

                string? mod = ModRegistry.Resolve(asm);
                if (mod != null)
                    return mod;
            }

            return ModRegistry.BaseGame;
        }

        // ── 参照 ─────────────────────────────────────────────────────

        internal static string? Lookup(int instanceId)
        {
            lock (_lock)
            {
                return _owners.TryGetValue(instanceId, out string owner) ? owner : null;
            }
        }

        // MOD別の未解放バンドル数（ロード数 − アンロード数）
        internal static Dictionary<string, int> GetUnfreedBundles()
        {
            var result = new Dictionary<string, int>();
            lock (_lock)
            {
                foreach (var kv in _bundleLoads)
                {
                    _bundleUnloads.TryGetValue(kv.Key, out int unloaded);
                    result[kv.Key] = kv.Value - unloaded;
                }
            }
            return result;
        }

        // センサス時に呼ぶ。生存していない instanceID を対応表から消す（対応表自体のリーク防止）。
        // aliveIds はセンサスが列挙した全 instanceID。_owners にはセンサス対象の型しか
        // 入れていないので（IsCensusType）、ここに無い＝本当に破棄された、と判断してよい。
        // AssetBundle は _bundleOwners 側にあり、この掃除の対象外。
        internal static int Prune(HashSet<int> aliveIds)
        {
            lock (_lock)
            {
                var dead = new List<int>();
                foreach (int id in _owners.Keys)
                {
                    if (!aliveIds.Contains(id))
                        dead.Add(id);
                }
                foreach (int id in dead)
                    _owners.Remove(id);
                return dead.Count;
            }
        }

        internal static int TrackedCount
        {
            get { lock (_lock) { return _owners.Count + _bundleOwners.Count; } }
        }
    }
}
