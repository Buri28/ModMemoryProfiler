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

        // 型別の個数。バイト数がほとんど増えないのに個数だけ増える種類のリーク
        // （アイコン等の小さなアセットの積み上がり）は、MB を見ていると見落とす。
        internal int TextureCount;
        internal int RenderTextureCount;
        internal int SpriteCount;
        internal int MeshCount;
        internal int AudioCount;

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
                {
                    s.RenderTextureBytes += bytes;
                    s.RenderTextureCount++;
                }
                else
                {
                    s.TextureBytes += bytes;
                    s.TextureCount++;
                }
                s.LiveAssetCount++;
            }

            // ── スプライト ──
            // バイト数は参照先のテクスチャに計上されるため、ここでは個数だけを見る。
            // アイコンの積み上がりは MB ではなく個数に現れるので、この列が主役になる。
            foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sprite == null) continue;
                int id = sprite.GetInstanceID();
                aliveIds.Add(id);

                ModStats s = Get(stats, OwnerOf(id, sprite.GetType()));
                s.SpriteCount++;
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
                s.MeshCount++;
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
                s.AudioCount++;
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

        // 生きている Sprite / Texture の名前を数え上げてテキストに落とす。
        //
        // 「何かが積み上がっている」までは個数で分かるが、その正体までは分からない。
        // Unity のアセットには名前が付いているので、それを見れば
        // カバー画像なのか UI アイコンなのかが判別できる。
        internal static string DumpAssetNames(string directory, int topN)
        {
            var counts = new Dictionary<string, int>(1024);

            void Tally(string kind, string owner, string name)
            {
                // 曲名などで名前が全部バラバラになると傾向が見えないので、
                // 長い名前は先頭だけ残して丸める（"cover_<曲名>" 等をまとめるため）
                if (name.Length > 40)
                    name = name.Substring(0, 40) + "...";
                string key = $"{kind}\t{owner}\t{name}";
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
            }

            foreach (Sprite sp in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sp == null) continue;
                Tally("Sprite", OwnerOf(sp.GetInstanceID(), typeof(Sprite)), sp.name ?? "(null)");
            }
            foreach (Texture tx in Resources.FindObjectsOfTypeAll<Texture>())
            {
                if (tx == null || tx is RenderTexture) continue;
                Tally("Texture", OwnerOf(tx.GetInstanceID(), tx.GetType()), tx.name ?? "(null)");
            }

            // ── マネージド側 ──
            // マネージドヒープの MB を MOD 別に割ることは原理的にできないので、
            // 「どのクラスのインスタンスが何個生き残っているか」で代用する。
            // 型からアセンブリを直接引けるため、この帰属はスタックトレースに依存せず正確。
            foreach (MonoBehaviour mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null) continue;
                Type mt = mb.GetType();
                Tally("MonoBehaviour", ModRegistry.ResolveType(mt), mt.FullName ?? mt.Name);
            }

            // GameObject は名前に用途が出ることが多く、増え続けているものの正体を掴みやすい。
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null) continue;
                Tally("GameObject", OwnerOf(go.GetInstanceID(), null), go.name ?? "(null)");
            }

            var list = new List<KeyValuePair<string, int>>(counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));

            System.IO.Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory,
                $"assets_{DateTime.Now:yyyyMMdd_HHmmss}.tsv");

            using (var w = new System.IO.StreamWriter(path, false, new System.Text.UTF8Encoding(true)))
            {
                w.WriteLine("count\ttype\towner\tname");
                int shown = 0;
                foreach (var kv in list)
                {
                    if (shown++ >= topN) break;
                    w.WriteLine($"{kv.Value}\t{kv.Key}");
                }
            }

            Plugin.Log.Info($"asset dump written: {path} ({list.Count} distinct names)");
            return path;
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

            // 概算側も native プロパティを触るので、破棄済みオブジェクト等で落ちないよう包む
            try { return Estimate(obj); }
            catch { return 0; }
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

            // AudioClip は GetRuntimeMemorySizeLong が 0 を返すため、実測では audioMB が
            // 常に 0 になっていた。PCM 16bit 換算で概算する。
            // 圧縮のままメモリに載っている場合は過大評価になるが、0 のまま見落とすより良い。
            if (obj is AudioClip clip)
                return (long)clip.samples * clip.channels * 2;

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
