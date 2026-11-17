// Gathers the live numbers of the GPUs page (load, temperature, power, fan, VRAM, displays)
// and which GPU each listed app runs on.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace GpuToggle
{
    class GpuStats
    {
        public string Load = Txt.NA, Temp = Txt.NA, Power = Txt.NA, Fan = Txt.NA, Vram = Txt.NA;
        public float TempValue = -1;
    }

    class StatsResult
    {
        public Dictionary<string, GpuStats> ByInstance = new Dictionary<string, GpuStats>();
        public List<DisplayInfo> Displays = new List<DisplayInfo>();
        public Dictionary<string, string> LuidNames = new Dictionary<string, string>();
        public Dictionary<string, string> AppRunning = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    static class Stats
    {
        // Which GPU a running app is using right now (from the per-process GPU Engine counters).
        static string RunningText(string path, StatsResult r, Dictionary<int, Dictionary<string, float>> pidLoad)
        {
            var pids = new List<int>();
            try
            {
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
                {
                    pids.Add(p.Id);
                    p.Dispose();
                }
            }
            catch { }
            if (pids.Count == 0) return "Not running";

            var per = new Dictionary<string, float>();
            foreach (int pid in pids)
            {
                Dictionary<string, float> d;
                if (!pidLoad.TryGetValue(pid, out d)) continue;
                foreach (var kv in d)
                {
                    float cur;
                    per.TryGetValue(kv.Key, out cur);
                    per[kv.Key] = Math.Min(100f, cur + kv.Value);
                }
            }
            if (per.Count == 0) return "Running - no GPU activity yet";

            string best = null;
            float bestValue = -1;
            foreach (var kv in per)
                if (kv.Value > bestValue) { best = kv.Key; bestValue = kv.Value; }
            string name;
            if (!r.LuidNames.TryGetValue(best, out name)) name = "Unknown GPU";
            return name + "   " + bestValue.ToString("0", Txt.Inv) + " %";
        }

        public static StatsResult Collect(List<GpuInfo> gpus, List<string> appPaths)
        {
            var r = new StatsResult();
            try { r.Displays = Displays.List(); } catch { }

            List<DxgiAdapter> adapters = new List<DxgiAdapter>();
            Dictionary<string, float> load = new Dictionary<string, float>();
            Dictionary<string, long> mem = new Dictionary<string, long>();
            try { adapters = Dxgi.List(); } catch { }
            try { load = PerfGpu.Load(); } catch { }
            try { mem = PerfGpu.DedicatedUsage(); } catch { }

            bool anyNvidia = false;
            foreach (var g in gpus) if (g.Enabled && g.IsNvidia) anyNvidia = true;
            NvSmi nv = anyNvidia ? Nvidia.Query() : null;
            int rpm = anyNvidia ? Nvidia.FanRpm() : -1;

            foreach (var g in gpus)
            {
                if (!g.Enabled || !g.IsPhysical) continue;
                var s = new GpuStats();

                DxgiAdapter ad = null;
                foreach (var a in adapters) if (a.Ven == g.VenId && a.Dev == g.DevId) { ad = a; break; }
                if (ad != null)
                {
                    float l;
                    if (load.TryGetValue(ad.Luid, out l)) s.Load = l.ToString("0", Txt.Inv) + " %";
                    long used;
                    if (mem.TryGetValue(ad.Luid, out used) && ad.DedicatedTotal > 0)
                        s.Vram = Fmt.GiB(used) + " / " + Fmt.GiB(ad.DedicatedTotal) + " GB";
                }

                if (g.IsNvidia && nv != null)
                {
                    if (nv.Util >= 0) s.Load = nv.Util.ToString("0", Txt.Inv) + " %";
                    if (nv.Temp >= 0) { s.Temp = nv.Temp.ToString("0", Txt.Inv) + " C"; s.TempValue = nv.Temp; }
                    if (nv.Power >= 0)
                        s.Power = nv.Power.ToString("0", Txt.Inv) + (nv.Limit > 0 ? " / " + nv.Limit.ToString("0", Txt.Inv) : "") + " W";
                    if (nv.MemUsed >= 0 && nv.MemTotal > 0)
                        s.Vram = (nv.MemUsed / 1024).ToString("0.0", Txt.Inv) + " / " + (nv.MemTotal / 1024).ToString("0.0", Txt.Inv) + " GB";
                    if (rpm >= 0)
                        s.Fan = rpm == 0 ? "0 RPM (stop)" : rpm + " RPM";
                    else if (nv.FanPct >= 0)
                        s.Fan = nv.FanPct.ToString("0", Txt.Inv) + " %";
                }
                r.ByInstance[g.InstanceId] = s;
            }

            foreach (var a in adapters)
            {
                string name = a.Name;
                foreach (var g in gpus) if (g.VenId == a.Ven && g.DevId == a.Dev) { name = g.Name; break; }
                r.LuidNames[a.Luid] = name;
            }
            var pidLoad = PerfGpu.LastPidLoad;
            foreach (var path in appPaths) r.AppRunning[path] = RunningText(path, r, pidLoad);
            return r;
        }
    }
}
