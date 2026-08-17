using System;
using System.Runtime.InteropServices;

namespace ModMemoryProfiler.Profiling
{
    // システム全体のメモリ状況を読む。
    //
    // 「長時間プレイすると重くなる」の直接の引き金は、Beat Saber 単体の使用量ではなく
    // マシン全体の空きが尽きてページングが始まることだった。実測では
    // 空き 3.4GB → 1.3GB、コミットは開始1分目から上限の 97.9% に張り付き、
    // その間に Windows がページファイルを3回自動拡張していた。
    //
    // つまりプレイヤーが知りたいのは「あと何分プレイできるか」であり、
    // それを判断する材料はこのプロセスの中には無い。外部ツールを常時動かすのは
    // 現実的でないので、MOD 自身が読む。
    //
    // Process 側と同様、Unity の Mono では System 側の API が当てにならないため
    // Win32 を直接叩く。取れなければ 0 を返し、その事実を1度だけログに残す。
    internal static class SystemMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;   // コミット上限（物理＋ページファイル）
            public ulong ullAvailPageFile;   // 残りコミット可能量
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private static bool _warned;

        internal struct Snapshot
        {
            internal long TotalPhysBytes;
            internal long FreePhysBytes;
            internal long CommitLimitBytes;
            internal long CommitFreeBytes;

            internal bool Valid => TotalPhysBytes > 0;

            internal long CommitUsedBytes => CommitLimitBytes - CommitFreeBytes;

            internal double CommitUsedPercent
                => CommitLimitBytes > 0 ? 100.0 * CommitUsedBytes / CommitLimitBytes : 0.0;
        }

        internal static Snapshot Read()
        {
            var result = new Snapshot();

            try
            {
                var m = new MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));

                if (GlobalMemoryStatusEx(ref m))
                {
                    result.TotalPhysBytes = (long)m.ullTotalPhys;
                    result.FreePhysBytes = (long)m.ullAvailPhys;
                    result.CommitLimitBytes = (long)m.ullTotalPageFile;
                    result.CommitFreeBytes = (long)m.ullAvailPageFile;
                    return result;
                }

                WarnOnce($"GlobalMemoryStatusEx failed (win32 error {Marshal.GetLastWin32Error()})");
            }
            catch (Exception e)
            {
                WarnOnce($"GlobalMemoryStatusEx threw: {e.Message}");
            }

            return result;
        }

        private static void WarnOnce(string message)
        {
            if (_warned)
                return;
            _warned = true;
            Plugin.Log.Warn($"SystemMemory: {message}. Memory gauge will be hidden.");
        }
    }
}
