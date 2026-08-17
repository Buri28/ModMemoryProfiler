using System;
using System.Runtime.InteropServices;

namespace ModMemoryProfiler.Profiling
{
    // プロセス全体の使用量を取る。
    //
    // System.Diagnostics.Process.PrivateMemorySize64 は Unity の Mono では実装されておらず、
    // 例外も投げずに 0 を返す。実際それに気づかず、丸1回分の計測を無駄にした。
    // そのため Win32 の GetProcessMemoryInfo を直接叩く。
    //
    // 取れなかった場合は 0 を返すが、その事実を必ず1回ログに残す。
    // 黙って 0 のままだと「メモリが増えていない」と読めてしまい、前回はそれで判断を誤った。
    internal static class ProcessMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage; // EX にのみ存在。タスクマネージャの「コミット サイズ」
        }

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr hProcess,
            out PROCESS_MEMORY_COUNTERS_EX counters, uint size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private static bool _warned;

        // private / workingSet をバイトで返す。取れなければ両方 0。
        internal static void Read(out long privateBytes, out long workingSetBytes)
        {
            privateBytes = 0;
            workingSetBytes = 0;

            try
            {
                var c = new PROCESS_MEMORY_COUNTERS_EX();
                c.cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX));

                if (GetProcessMemoryInfo(GetCurrentProcess(), out c, c.cb))
                {
                    privateBytes = (long)c.PrivateUsage.ToUInt64();
                    workingSetBytes = (long)c.WorkingSetSize.ToUInt64();
                    return;
                }

                WarnOnce($"GetProcessMemoryInfo failed (win32 error {Marshal.GetLastWin32Error()})");
            }
            catch (Exception e)
            {
                WarnOnce($"GetProcessMemoryInfo threw: {e.Message}");
            }
        }

        private static void WarnOnce(string message)
        {
            if (_warned)
                return;
            _warned = true;
            // 0 が並ぶ CSV を後から見て原因が分からなくなるのを防ぐ
            Plugin.Log.Warn($"ProcessMemory: {message}. processPrivateMB will stay 0.");
        }
    }
}
