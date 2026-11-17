// Guesses what limits the frame rate from the last 3 s of OSD snapshots.

using System;
using System.Collections.Generic;

namespace GpuToggle
{
    class BottleneckJudge
    {
        readonly Queue<OsdSnapshot> hist = new Queue<OsdSnapshot>();

        public void Clear() { hist.Clear(); }

        // Averages the last 3 s so the verdict does not flicker between frames.
        double Avg(Func<OsdSnapshot, double> f)
        {
            double sum = 0;
            int n = 0;
            foreach (var x in hist) { double v = f(x); if (v >= 0) { sum += v; n++; } }
            return n > 0 ? sum / n : -1;
        }

        public OsdVerdict Judge(OsdSnapshot s)
        {
            hist.Enqueue(s);
            while (hist.Count > 6) hist.Dequeue();
            double gpu = Avg(x => x.GpuLoad), cpu = Avg(x => x.CpuLoad), core = Avg(x => x.CpuMaxCore);
            double fps = Avg(x => x.Frames != null ? x.Frames.Fps : -1);
            double pcie = Avg(x => x.PciePct), vram = Avg(x => x.VramPct), ram = Avg(x => x.RamPct);
            var v = new OsdVerdict();

            if (pcie >= (s.PcieMeasured ? 90 : 85))
            {
                v.Text = "PCIe link"; v.Level = 2;
                v.Detail = "Traffic at " + pcie.ToString("0") + " % of the " + (s.PcieMeasured ? "measured" : "theoretical") + " link limit. Typical for eGPUs:" + Txt.NL +
                    "lower texture quality / resolution, connect the monitor to the eGPU.";
            }
            else if (vram >= 95)
            {
                v.Text = "VRAM full"; v.Level = 2;
                v.Detail = "VRAM " + vram.ToString("0") + " % used: textures spill into system RAM over PCIe (stutter)." + Txt.NL + "Lower texture quality.";
            }
            else if (fps < 0 && (gpu < 0 || gpu < 50))
            {
                v.Text = "No game"; v.Level = -1;
                v.Detail = "Bring a game to the front: the OSD measures the app in the foreground.";
            }
            else if (gpu >= 95)
            {
                v.Text = "GPU-bound"; v.Level = 0;
                v.Detail = "GPU " + gpu.ToString("0") + " % - the normal case. For more FPS lower resolution /" + Txt.NL + "settings or use DLSS / FSR.";
            }
            else if (core >= 90 || cpu >= 90)
            {
                v.Text = "CPU-bound"; v.Level = 1;
                v.Detail = "Busiest CPU thread " + core.ToString("0") + " % while the GPU waits (" + Math.Max(0, gpu).ToString("0") + " %)." + Txt.NL +
                    "Lower CPU-heavy settings (view distance, crowds, physics).";
            }
            else if (fps > 0 && s.RefreshHz > 0 && fps >= s.RefreshHz * 0.95 && fps <= s.RefreshHz * 1.03)
            {
                v.Text = "V-Sync / refresh cap"; v.Level = 0;
                v.Detail = "FPS sits at the " + s.RefreshHz + " Hz refresh rate: the game is capped, nothing is maxed.";
            }
            else if (ram >= 92)
            {
                v.Text = "RAM full"; v.Level = 1;
                v.Detail = "System RAM " + ram.ToString("0") + " % used: Windows pages to disk. Close background apps.";
            }
            else if (fps > 0 && gpu >= 0 && gpu < 85)
            {
                v.Text = "Limited elsewhere"; v.Level = 1;
                v.Detail = "Neither GPU (" + gpu.ToString("0") + " %) nor CPU is maxed: FPS limiter, game engine, or one" + Txt.NL +
                    "game thread hopping between cores.";
            }
            else
            {
                v.Text = "No bottleneck"; v.Level = 0;
                v.Detail = "GPU " + Math.Max(0, gpu).ToString("0") + " %, busiest CPU thread " + Math.Max(0, core).ToString("0") + " %.";
            }
            return v;
        }
    }
}
