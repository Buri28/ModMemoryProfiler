using ModMemoryProfiler.Profiling;
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ModMemoryProfiler.Output
{
    // 計測結果を CSV に追記する。
    // 縦持ち（1行 = 1スナップショット × 1MOD）。集計・グラフ化は外部（Excel等）で行う前提。
    internal class CsvSink : IDisposable
    {
        // 列を増減させたら README の表も必ず合わせること。
        private const string Header =
            "timestamp,elapsedMin,phase,songsPlayed,mod," +
            "textureMB,renderTextureMB,meshMB,audioMB," +
            "textureCount,renderTextureCount,spriteCount,meshCount,audioCount," +
            "materialCount,gameObjectCount,monoBehaviourCount,liveAssetCount,unfreedBundles,msPerFrame," +
            "processPrivateMB,processWorkingSetMB,unityReservedMB,monoHeapMB," +
            "sysFreeMB,sysCommitPct,customLevelCount";

        private readonly StreamWriter _writer;

        internal string FilePath { get; }

        internal CsvSink(string directory)
        {
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            _writer = new StreamWriter(FilePath, append: false, encoding: new UTF8Encoding(true));
            _writer.WriteLine(Header);
            _writer.Flush();
        }

        // ModStats をそのまま渡す。以前は 15 個の引数を順番に並べており、
        // 列を足すたびに渡し間違いが起きやすかった。
        internal void WriteRow(DateTime timestamp, double elapsedMin, string phase,
                               int songsPlayed, string mod, ModStats s)
        {
            var ci = CultureInfo.InvariantCulture;
            _writer.WriteLine(string.Join(",", new[]
            {
                timestamp.ToString("yyyy-MM-dd HH:mm:ss", ci),
                elapsedMin.ToString("F2", ci),
                Escape(phase),
                songsPlayed.ToString(ci),
                Escape(mod),
                Mb(s.TextureBytes),
                Mb(s.RenderTextureBytes),
                Mb(s.MeshBytes),
                Mb(s.AudioBytes),
                s.TextureCount.ToString(ci),
                s.RenderTextureCount.ToString(ci),
                s.SpriteCount.ToString(ci),
                s.MeshCount.ToString(ci),
                s.AudioCount.ToString(ci),
                s.MaterialCount.ToString(ci),
                s.GameObjectCount.ToString(ci),
                s.MonoBehaviourCount.ToString(ci),
                s.LiveAssetCount.ToString(ci),
                s.UnfreedBundles.ToString(ci),
                s.MsPerFrame.ToString("F3", ci),
                // (TOTAL) 行以外は 0。プロセス全体の値なのでMOD別に分ける意味がない。
                Mb(s.ProcessPrivateBytes),
                Mb(s.ProcessWorkingSetBytes),
                Mb(s.UnityReservedBytes),
                Mb(s.MonoHeapBytes),
                Mb(s.SystemFreeBytes),
                s.SystemCommitPercent.ToString("F1", ci),
                s.CustomLevelCount.ToString(ci),
            }));
        }

        internal void Flush() => _writer.Flush();

        private static string Mb(long bytes)
            => (bytes / 1024.0 / 1024.0).ToString("F2", CultureInfo.InvariantCulture);

        // MOD名にカンマが含まれても壊れないようにする
        private static string Escape(string value)
            => value.IndexOf(',') >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

        public void Dispose()
        {
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch { }
        }
    }
}
