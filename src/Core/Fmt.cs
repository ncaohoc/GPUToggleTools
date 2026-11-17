// Number formatting shared by the pages, the OSD and the stats (always invariant culture).

using System;

namespace GpuToggle
{
    static class Fmt
    {
        public static string F(double v, string format) { return v.ToString(format, Txt.Inv); }

        // Bytes as GiB with one decimal, e.g. "7.8" (VRAM, RAM).
        public static string GiB(double bytes) { return F(bytes / 1073741824.0, "0.0"); }

        // Bytes per second as GB/s with two decimals, e.g. "3.81" (PCIe traffic).
        public static string GBs(double bytesPerSec) { return F(bytesPerSec / 1e9, "0.00"); }

        // v as a whole percentage of cap, e.g. "48 %".
        public static string Pct(double v, double cap) { return F(cap > 0 ? v / cap * 100 : 0, "0") + " %"; }

        // mm:ss, or h:mm:ss from one hour on.
        public static string Dur(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.ToString("mm':'ss", Txt.Inv) : t.ToString("mm':'ss", Txt.Inv);
        }

        public static string Deg { get { return ((char)0xB0).ToString() + "C"; } }
    }
}
