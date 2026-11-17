// Turns an OSD snapshot into the colored lines the overlay draws.

using System.Collections.Generic;
using System.Drawing;

namespace GpuToggle
{
    static class OsdLines
    {
        // OSD colors (fixed: the overlay sits on top of the game, not on the app theme)
        static readonly Color OsdGraph = Color.FromArgb(90, 200, 250), OsdGraph2 = Color.FromArgb(240, 120, 200);
        static readonly Color OsdLabel = Color.FromArgb(255, 170, 70), OsdValue = Color.FromArgb(245, 247, 250), OsdDim = Color.FromArgb(160, 170, 185),
            OsdGood = Color.FromArgb(110, 220, 130), OsdWarn = Color.FromArgb(255, 180, 60), OsdBad = Color.FromArgb(255, 90, 90);

        static string PadL(string s, int n) { return s.Length >= n ? s : new string(' ', n - s.Length) + s; }

        static Color TempColor(float t) { return t >= 90 ? OsdBad : (t >= 80 ? OsdWarn : OsdValue); }
        static Color PctColor(double p, double warn, double bad) { return p >= bad ? OsdBad : (p >= warn ? OsdWarn : OsdValue); }

        static void AddGraph(List<OsdSeg> l, float[] v, float[] v2, float fixedMax)
        {
            if (v == null) return;
            var g = new OsdSeg(v, 11, OsdGraph);
            g.Fixed = fixedMax;
            if (v2 != null) { g.Graph2 = v2; g.Color2 = OsdGraph2; }
            l.Add(g);
        }

        public static List<List<OsdSeg>> Build(OsdSnapshot s)
        {
            var lines = new List<List<OsdSeg>>();
            int items = OsdSettings.Items, graphs = OsdSettings.Graphs;
            if (s == null)
            {
                lines.Add(new List<OsdSeg> { new OsdSeg("OSD  starting...", OsdDim) });
                return lines;
            }

            if ((items & OsdSettings.ItemFps) != 0)
            {
                var l = new List<OsdSeg> { new OsdSeg("FPS  ", OsdLabel) };
                if (s.Frames != null)
                {
                    l.Add(new OsdSeg(PadL(Fmt.F(s.Frames.Fps, "0"), 4), OsdValue));
                    if ((graphs & OsdSettings.ItemFps) != 0 && s.Frames.Graph != null) l.Add(new OsdSeg(s.Frames.Graph, 11, OsdGraph));
                    l.Add(new OsdSeg(PadL(Fmt.F(s.Frames.FrameMs, "0.0"), 6) + " ms", OsdDim));
                    if (s.Frames.Low1 > 0) l.Add(new OsdSeg("  1% " + Fmt.F(s.Frames.Low1, "0"), s.Frames.Low1 < s.Frames.Fps * 0.5 ? OsdWarn : OsdDim));
                }
                else l.Add(new OsdSeg(s.FpsError != null ? "n/a" : "   -", OsdDim));
                lines.Add(l);
            }
            if ((items & OsdSettings.ItemGpu) != 0)
            {
                var l = new List<OsdSeg> { new OsdSeg("GPU  ", OsdLabel) };
                l.Add(new OsdSeg(s.GpuLoad >= 0 ? PadL(Fmt.F(s.GpuLoad, "0"), 3) + " %" : "  - %", OsdValue));
                if ((graphs & OsdSettings.ItemGpu) != 0) AddGraph(l, s.GpuGraph, null, 100);
                if (s.GpuTemp > 0) l.Add(new OsdSeg(PadL(Fmt.F(s.GpuTemp, "0"), 5) + Fmt.Deg, TempColor(s.GpuTemp)));
                if (s.GpuPower >= 0) l.Add(new OsdSeg(PadL(Fmt.F(s.GpuPower, "0"), 5) + " W", s.GpuPowerLimit > 0 && s.GpuPower >= s.GpuPowerLimit * 0.97 ? OsdWarn : OsdDim));
                lines.Add(l);
            }
            if ((items & OsdSettings.ItemVram) != 0)
            {
                var l = new List<OsdSeg> { new OsdSeg("VRAM ", OsdLabel) };
                if (s.VramTotal > 0)
                {
                    l.Add(new OsdSeg(Fmt.GiB(s.VramUsed) + " / " + Fmt.GiB(s.VramTotal) + " GB", PctColor(s.VramPct, 85, 95)));
                    if ((graphs & OsdSettings.ItemVram) != 0) AddGraph(l, s.VramGraph, null, 100);
                }
                else l.Add(new OsdSeg("-", OsdDim));
                lines.Add(l);
            }
            if ((items & OsdSettings.ItemCpu) != 0)
            {
                var l = new List<OsdSeg> { new OsdSeg("CPU  ", OsdLabel) };
                l.Add(new OsdSeg(s.CpuLoad >= 0 ? PadL(Fmt.F(s.CpuLoad, "0"), 3) + " %" : "  - %", OsdValue));
                if ((graphs & OsdSettings.ItemCpu) != 0) AddGraph(l, s.CpuGraph, s.CpuMaxGraph, 100);   // total + busiest core
                if (s.CpuMaxCore >= 0) l.Add(new OsdSeg("  max " + PadL(Fmt.F(s.CpuMaxCore, "0"), 3) + " %", PctColor(s.CpuMaxCore, 85, 95)));
                if (s.CpuTemp > 0) l.Add(new OsdSeg(PadL(Fmt.F(s.CpuTemp, "0"), 5) + Fmt.Deg, TempColor(s.CpuTemp)));
                lines.Add(l);
            }
            if ((items & OsdSettings.ItemRam) != 0)
            {
                var l = new List<OsdSeg> { new OsdSeg("RAM  ", OsdLabel) };
                if (s.RamTotal > 0)
                {
                    l.Add(new OsdSeg(Fmt.GiB(s.RamUsed) + " / " + Fmt.GiB(s.RamTotal) + " GB", PctColor(s.RamPct, 85, 92)));
                    if ((graphs & OsdSettings.ItemRam) != 0) AddGraph(l, s.RamGraph, null, 100);
                }
                else l.Add(new OsdSeg("-", OsdDim));
                lines.Add(l);
            }
            if ((items & OsdSettings.ItemPcie) != 0)
            {
                var l = new List<OsdSeg> { new OsdSeg("PCIe ", OsdLabel) };
                if (s.PcieRx >= 0)
                {
                    l.Add(new OsdSeg(((char)0x2191).ToString(), OsdGraph));
                    l.Add(new OsdSeg(Fmt.F(s.PcieRx / 1e9, "0.00") + " ", OsdValue));
                    l.Add(new OsdSeg(((char)0x2193).ToString(), OsdGraph2));
                    l.Add(new OsdSeg(Fmt.F(s.PcieTx / 1e9, "0.00") + " GB/s", OsdValue));
                    if ((graphs & OsdSettings.ItemPcie) != 0 && s.PcieRxGraph != null)
                    {
                        var gs = new OsdSeg(s.PcieRxGraph, 11, OsdGraph);
                        gs.Graph2 = s.PcieTxGraph; gs.Color2 = OsdGraph2; gs.Floor = 0.1e9f;
                        l.Add(gs);
                    }
                    double p = s.PciePct;
                    if (p >= 0) l.Add(new OsdSeg(PadL(Fmt.F(p, "0"), 5) + " %", PctColor(p, 70, s.PcieMeasured ? 90 : 85)));
                }
                else l.Add(new OsdSeg(s.GpuNvidia ? "-" : "n/a (NVIDIA only)", OsdDim));
                lines.Add(l);
            }
            if ((items & OsdSettings.ItemLimit) != 0 && s.Verdict.Text.Length > 0)
            {
                Color c = s.Verdict.Level == 2 ? OsdBad : (s.Verdict.Level == 1 ? OsdWarn : (s.Verdict.Level == 0 ? OsdGood : OsdDim));
                lines.Add(new List<OsdSeg> { new OsdSeg("LIM  ", OsdLabel), new OsdSeg(s.Verdict.Text, c) });
            }
            if (lines.Count == 0) lines.Add(new List<OsdSeg> { new OsdSeg("OSD", OsdLabel) });
            return lines;
        }
    }
}
