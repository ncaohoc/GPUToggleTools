// GPU load and dedicated VRAM use from the Windows GPU performance counters (all vendors).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GpuToggle
{
    static class PerfGpu
    {
        static Dictionary<string, CounterSample> prev = new Dictionary<string, CounterSample>();
        static readonly Regex LuidRx = new Regex("luid_0x[0-9a-f]{8}_0x[0-9a-f]{8}", RegexOptions.IgnoreCase);
        static readonly Regex EngRx = new Regex("eng_([0-9]+)", RegexOptions.IgnoreCase);
        static readonly Regex PidRx = new Regex("pid_([0-9]+)_", RegexOptions.IgnoreCase);

        // pid -> (luid -> busiest-engine load %), filled by Load(). A process that has a context on a GPU
        // appears here even at 0 %, so we can tell which GPU an app is running on.
        public static Dictionary<int, Dictionary<string, float>> LastPidLoad = new Dictionary<int, Dictionary<string, float>>();

        // Returns luid -> load % (busiest engine, like Task Manager)
        public static Dictionary<string, float> Load()
        {
            Dictionary<int, Dictionary<string, float>> pidLoad;
            var result = Load(ref prev, out pidLoad);
            LastPidLoad = pidLoad;
            return result;
        }

        // Same, with the caller's own previous samples (the OSD reads the counters on its own thread and rate).
        public static Dictionary<string, float> Load(ref Dictionary<string, CounterSample> prev, out Dictionary<int, Dictionary<string, float>> pidLoad)
        {
            var result = new Dictionary<string, float>();
            var cat = new PerformanceCounterCategory("GPU Engine");
            InstanceDataCollection col = cat.ReadCategory()["Utilization Percentage"];
            var sums = new Dictionary<string, float>();
            var next = new Dictionary<string, CounterSample>();
            pidLoad = new Dictionary<int, Dictionary<string, float>>();
            foreach (InstanceData d in col.Values)
            {
                next[d.InstanceName] = d.Sample;
                var lm = LuidRx.Match(d.InstanceName);
                var pm = PidRx.Match(d.InstanceName);
                Dictionary<string, float> perLuid = null;
                if (lm.Success && pm.Success)
                {
                    int pid = int.Parse(pm.Groups[1].Value, Txt.Inv);
                    if (!pidLoad.TryGetValue(pid, out perLuid)) { perLuid = new Dictionary<string, float>(); pidLoad[pid] = perLuid; }
                    if (!perLuid.ContainsKey(lm.Value.ToLowerInvariant())) perLuid[lm.Value.ToLowerInvariant()] = 0f;
                }
                CounterSample old;
                if (!prev.TryGetValue(d.InstanceName, out old)) continue;
                float v = CounterSample.Calculate(old, d.Sample);
                var em = EngRx.Match(d.InstanceName);
                if (!lm.Success || !em.Success) continue;
                if (perLuid != null)
                {
                    string lk = lm.Value.ToLowerInvariant();
                    perLuid[lk] = Math.Min(100f, Math.Max(perLuid[lk], v));
                }
                string key = lm.Value.ToLowerInvariant() + "|" + em.Groups[1].Value;
                float s;
                sums.TryGetValue(key, out s);
                sums[key] = s + v;
            }
            prev = next;
            foreach (var kv in sums)
            {
                string luid = kv.Key.Substring(0, kv.Key.IndexOf('|'));
                float cur;
                result.TryGetValue(luid, out cur);
                result[luid] = Math.Min(100f, Math.Max(cur, kv.Value));
            }
            return result;
        }

        // Returns luid -> dedicated bytes in use
        public static Dictionary<string, long> DedicatedUsage()
        {
            var result = new Dictionary<string, long>();
            var cat = new PerformanceCounterCategory("GPU Adapter Memory");
            InstanceDataCollection col = cat.ReadCategory()["Dedicated Usage"];
            foreach (InstanceData d in col.Values)
            {
                var lm = LuidRx.Match(d.InstanceName);
                if (!lm.Success) continue;
                string luid = lm.Value.ToLowerInvariant();
                long cur;
                result.TryGetValue(luid, out cur);
                result[luid] = cur + d.RawValue;
            }
            return result;
        }
    }
}
