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

        // プロセス全体のメモリ。(TOTAL) 行にだけ入り、MOD別の行では常に 0。
        //
        // Unity が把握しているのはプロセスの一部でしかない（実測で 2.9GB / 8.9GB）。
        // 残りはネイティブ・Mono ランタイム・各MODのDLL・ドライバのバッファ等で、
        // マネージドヒープにも Unity の確保総量にも現れない。
        // ここが無いせいで「起動時に何GB使っているか」を毎回外部ツールに頼っており、
        // ツールを回し忘れた計測が繰り返し無駄になったため、MOD自身が記録する。
        internal long ProcessPrivateBytes;
        internal long ProcessWorkingSetBytes;

        // Unity が OS から「予約」している総量と、実際に「使用」している総量の差。
        // ネイティブのアロケータは大きな塊単位で確保するため、オブジェクトを一度に大量生成すると
        // 使用量以上にコミットが跳ねる。プロセスのコミットだけが増えて
        // 確保総量もマネージドヒープも増えない現象の、有力な説明候補。
        internal long UnityReservedBytes;

        // Mono が OS から借りているヒープの総量（隙間を含む）。
        //
        // GC.GetTotalMemory が返すのは「生きているオブジェクトの合計」＝使用量であって、
        // GC が抱えている総量ではない。Unity の Mono は Boehm（非圧縮GC）なので
        // 回収後に詰め直しが行われず、ブロック内に1つでも生存オブジェクトがあると
        // そのブロックを OS に返せない。結果として
        // 「使用量は横ばいなのにヒープだけ増え続ける」が起こりうる。
        //
        // 実測で 使用量+235MB に対しプロセス+2,248MB という乖離が出ており、
        // その差の最有力候補がここ。使用量だけ見ていては原理的に見えない。
        //
        // ※ GetAllocatedMemoryForGraphicsDriver は全行 0 を返して機能しなかったため、
        //    その列と入れ替えている。
        internal long MonoHeapBytes;

        // システム全体の空き物理メモリと、コミット使用率。(TOTAL) 行にのみ入る。
        // 「重くなる」の直接の引き金はここが尽きることなので、Beat Saber 単体の
        // 使用量より優先して見るべき数字。外部ロガーなしで後から検証できるよう記録する。
        internal long SystemFreeBytes;
        internal double SystemCommitPercent;

        // SongCore が読み込み済みのカスタム曲数。(TOTAL) 行にのみ入る。-1 は取得不可。
        // 条件（曲あり/なし）を後から確実に判別するため、および
        // 非同期の読み込みが完了しているかを見るために記録する。
        internal int CustomLevelCount;
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

            // AudioClip は曲名がそのまま名前に入るので、積み上がっている正体が一目で分かる。
            // 実測で1譜面あたり約1.3個ずつ単調増加しており、解放漏れの最有力候補。
            // loadType も併記する（同じ個数でも常駐かストリーミングかで実害が2桁違うため）。
            foreach (AudioClip ac in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                if (ac == null) continue;
                string load;
                try { load = ac.loadType.ToString(); }
                catch { load = "?"; }
                Tally($"AudioClip[{load}]", OwnerOf(ac.GetInstanceID(), typeof(AudioClip)), ac.name ?? "(null)");
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

            // AudioClip は GetRuntimeMemorySizeLong が 0 を返すため概算する。
            //
            // 以前は一律 PCM16 換算（samples × channels × 2）にしていたが、
            // Beat Saber の曲は Ogg で、実際には圧縮のまま／ストリーミングで載っている。
            // その結果 audioMB が実態の10倍以上に膨らみ、「音声が300MB増えた」という
            // 誤った読みを生んでいた。loadType で分岐して桁を合わせる。
            if (obj is AudioClip clip)
            {
                long pcm = (long)clip.samples * clip.channels * 2;
                switch (clip.loadType)
                {
                    // 展開済みで載っている。PCM そのもの。
                    case AudioClipLoadType.DecompressOnLoad:
                        return pcm;
                    // 圧縮のまま常駐。Vorbis はおおむね 1/10 前後。
                    case AudioClipLoadType.CompressedInMemory:
                        return pcm / 10;
                    // 再生位置ぶんのバッファしか持たない。定数で置く。
                    case AudioClipLoadType.Streaming:
                        return 256 * 1024;
                    default:
                        return pcm / 10;
                }
            }

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
