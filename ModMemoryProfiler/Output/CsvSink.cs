using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ModMemoryProfiler.Output
{
    // 計測結果を CSV に追記する。
    // 縦持ち（1行 = 1スナップショット × 1MOD）。集計・グラフ化は外部（Excel等）で行う前提。
    internal class CsvSink : IDisposable
    {
        private const string Header =
            "timestamp,elapsedMin,phase,songsPlayed,mod," +
            "textureMB,renderTextureMB,meshMB,audioMB," +
            "materialCount,gameObjectCount,monoBehaviourCount,liveAssetCount,unfreedBundles,msPerFrame";

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

        internal void WriteRow(
            DateTime timestamp, double elapsedMin, string phase, int songsPlayed, string mod,
            long textureBytes, long renderTextureBytes, long meshBytes, long audioBytes,
            int materialCount, int gameObjectCount, int monoBehaviourCount,
            int liveAssetCount, int unfreedBundles, double msPerFrame)
        {
            var ci = CultureInfo.InvariantCulture;
            _writer.WriteLine(string.Join(",", new[]
            {
                timestamp.ToString("yyyy-MM-dd HH:mm:ss", ci),
                elapsedMin.ToString("F2", ci),
                phase,
                songsPlayed.ToString(ci),
                Escape(mod),
                Mb(textureBytes),
                Mb(renderTextureBytes),
                Mb(meshBytes),
                Mb(audioBytes),
                materialCount.ToString(ci),
                gameObjectCount.ToString(ci),
                monoBehaviourCount.ToString(ci),
                liveAssetCount.ToString(ci),
                unfreedBundles.ToString(ci),
                msPerFrame.ToString("F3", ci),
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
