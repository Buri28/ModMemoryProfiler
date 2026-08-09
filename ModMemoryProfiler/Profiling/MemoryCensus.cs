using ModMemoryProfiler.Configuration;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace ModMemoryProfiler.Profiling
{
    // 1回のスナップショットにおける、1MOD分の集計値
    internal class ModStats
    {
        internal long TextureBytes;
        internal long RenderTextureBytes;
        internal long MeshBytes;
        internal long AudioBytes;
        internal int MaterialCount;
        internal int GameObjectCount;
        internal int MonoBehaviourCount;
        internal int LiveAssetCount;
        internal int UnfreedBundles;
        internal double MsPerFrame;
    }

    // ★中核その2: 生存している Unity アセットを走査し、実バイト数を MOD 別に合計する。
    //
    // 注意: Resources.FindObjectsOfTypeAll は数万オブジェクトを舐めるため重い。
    //       毎フレームではなく、スナップショットのタイミングでのみ呼ぶこと。
    internal static class MemoryCensus
    {
        internal static Dictionary<string, ModStats> TakeSnapshot()
        {
            var stats = new Dictionary<string, ModStats>();
            var aliveIds = new HashSet<int>();

            // ── テクスチャ（Texture2D / RenderTexture / Cubemap を含む） ──
            foreach (Texture tex in Resources.FindObjectsOfTypeAll<Texture>())
            {
                if (tex == null) continue;
                int id = tex.GetInstanceID();
                aliveIds.Add(id);

                ModStats s = Get(stats, OwnerOf(id, tex.GetType()));
                long bytes = SizeOf(tex);
                if (tex is RenderTexture)
                    s.RenderTextureBytes += bytes;
                else
                    s.TextureBytes += bytes;
                s.LiveAssetCount++;
            }

            // ── メッシュ ──
            foreach (Mesh mesh in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                if (mesh == null) continue;
                int id = mesh.GetInstanceID();
                aliveIds.Add(id);

                ModStats s = Get(stats, OwnerOf(id, mesh.GetType()));
                s.MeshBytes += SizeOf(mesh);
                s.LiveAssetCount++;
            }

            // ── 音声 ──
            foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                if (clip == null) continue;
                int id = clip.GetInstanceID();
                aliveIds.Add(id);

                ModStats s = Get(stats, OwnerOf(id, clip.GetType()));
                s.AudioBytes += SizeOf(clip);
                s.LiveAssetCount++;
            }

            // ── マテリアル（バイト数は取りにくいので個数で見る） ──
            foreach (Material mat in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (mat == null) continue;
                int id = mat.GetInstanceID();
                aliveIds.Add(id);

                ModStats s = Get(stats, OwnerOf(id, mat.GetType()));
                s.MaterialCount++;
                s.LiveAssetCount++;
            }

            // ── GameObject ──
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null) continue;
                int id = go.GetInstanceID();
                aliveIds.Add(id);

                ModStats s = Get(stats, OwnerOf(id, null));
                s.GameObjectCount++;
            }

            // ── MonoBehaviour（型からアセンブリが直接引けるので帰属が正確） ──
            if (PluginConfig.Instance.CountMonoBehaviours)
            {
                foreach (MonoBehaviour mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    // ここは所有者テーブルではなく型で引く。MOD製コンポーネントの解放漏れが直接見える。
                    ModStats s = Get(stats, ModRegistry.ResolveType(mb.GetType()));
                    s.MonoBehaviourCount++;
                }
            }

            // ── 未解放 AssetBundle ──
            foreach (var kv in OwnershipTracker.GetUnfreedBundles())
            {
                if (kv.Value == 0) continue;
                Get(stats, kv.Key).UnfreedBundles = kv.Value;
            }

            // 所有者テーブル自体が太らないように、死んだ instanceID を掃除する
            int pruned = OwnershipTracker.Prune(aliveIds);
            Plugin.DebugLog($"census: alive={aliveIds.Count} pruned={pruned} tracked={OwnershipTracker.TrackedCount}");

            return stats;
        }

        // 所有者テーブルを引く。無ければ型で引き、それも駄目なら Untracked。
        // Untracked = 起動時から存在するゲーム本体のアセット、または計測開始前に生成されたもの。
        private static string OwnerOf(int instanceId, Type? type)
        {
            string? owner = OwnershipTracker.Lookup(instanceId);
            if (owner != null)
                return owner;

            if (type != null)
            {
                string? byType = ModRegistry.Resolve(type.Assembly);
                if (byType != null)
                    return byType;
            }
            return ModRegistry.Untracked;
        }

        private static ModStats Get(Dictionary<string, ModStats> stats, string mod)
        {
            if (!stats.TryGetValue(mod, out ModStats s))
            {
                s = new ModStats();
                stats[mod] = s;
            }
            return s;
        }

        // 実バイト数。Unity の Profiler API が使えない場合は概算にフォールバックする。
        private static long SizeOf(Object obj)
        {
            if (PluginConfig.Instance.UseUnityProfilerApi)
            {
                try
                {
                    long n = Profiler.GetRuntimeMemorySizeLong(obj);
                    if (n > 0)
                        return n;
                }
                catch { /* フォールバックへ */ }
            }
            return Estimate(obj);
        }

        // フォールバック用の概算。テクスチャのみ意味のある値を返す。
        private static long Estimate(Object obj)
        {
            // ミップ付きは約 4/3 倍。整数除算にすると 1 になって係数が消えるので必ず double で書く。
            if (obj is Texture2D t2d)
                return (long)((double)t2d.width * t2d.height * BytesPerPixel(t2d.format)
                              * (t2d.mipmapCount > 1 ? 4.0 / 3.0 : 1.0));
            if (obj is RenderTexture rt)
                return (long)rt.width * rt.height * (rt.depth > 0 ? 8 : 4) * Math.Max(1, rt.antiAliasing);
            return 0;
        }

        private static int BytesPerPixel(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.Alpha8:
                case TextureFormat.R8:
                    return 1;
                case TextureFormat.RGB565:
                case TextureFormat.RGBA4444:
                case TextureFormat.ARGB4444:
                case TextureFormat.R16:
                case TextureFormat.RHalf:
                    return 2;
                case TextureFormat.RGB24:
                    return 3;
                case TextureFormat.RGBAHalf:
                    return 8;
                case TextureFormat.RGBAFloat:
                    return 16;
                default:
                    return 4;
            }
        }
    }
}
