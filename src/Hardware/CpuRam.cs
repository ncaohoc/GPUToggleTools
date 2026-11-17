// CPU load (total and busiest logical processor) and RAM use.

using System;
using System.Runtime.InteropServices;

namespace GpuToggle
{
    static class CpuRam
    {
        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, byte[] buffer, int length, out int returned);

        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = 64, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);

        static long[] prevIdle, prevTotal;
        static readonly byte[] buf = new byte[48 * 256];

        // Total load and the busiest logical processor since the previous call (%).
        // A game held back by its main thread shows one busy core while the total stays low.
        public static bool Cpu(out float total, out float maxCore)
        {
            total = maxCore = -1;
            int ret;
            if (NtQuerySystemInformation(8, buf, buf.Length, out ret) != 0) return false;   // SystemProcessorPerformanceInformation
            int n = ret / 48;
            var idle = new long[n];
            var all = new long[n];
            for (int i = 0; i < n; i++)
            {
                idle[i] = BitConverter.ToInt64(buf, i * 48);
                all[i] = BitConverter.ToInt64(buf, i * 48 + 8) + BitConverter.ToInt64(buf, i * 48 + 16);   // kernel (includes idle) + user
            }
            bool ok = prevIdle != null && prevIdle.Length == n;
            if (ok)
            {
                long si = 0, st = 0;
                float mx = 0;
                for (int i = 0; i < n; i++)
                {
                    long di = idle[i] - prevIdle[i], dt = all[i] - prevTotal[i];
                    si += di; st += dt;
                    if (dt > 0) mx = Math.Max(mx, (float)(dt - di) * 100f / dt);
                }
                if (st > 0) { total = Math.Max(0, Math.Min(100, (float)(st - si) * 100f / st)); maxCore = Math.Max(0, Math.Min(100, mx)); }
                else ok = false;
            }
            prevIdle = idle;
            prevTotal = all;
            return ok;
        }

        public static void Ram(out double used, out double total)
        {
            var m = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(m)) { total = m.ullTotalPhys; used = m.ullTotalPhys - m.ullAvailPhys; }
            else used = total = -1;
        }
    }
}
