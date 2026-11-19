// eGPU power helpers: disable the marked eGPUs before Windows shuts down / restarts, then shut down.

using System;
using System.Collections.Generic;
using System.Text;

namespace GpuToggle
{
    static class Egpu
    {
        // Marked eGPUs that are plugged in right now.
        public static List<GpuInfo> Marked(List<GpuInfo> gpus)
        {
            var l = new List<GpuInfo>();
            foreach (var g in gpus) if (g.IsPhysical && EgpuSettings.IsEgpu(g)) l.Add(g);
            return l;
        }

        // Disables every enabled marked eGPU. Returns false if one failed; log says what happened.
        public static bool DisableAll(List<GpuInfo> gpus, out string log)
        {
            var sb = new StringBuilder();
            bool ok = true;
            foreach (var g in Marked(gpus))
            {
                if (!g.Enabled) continue;
                string output;
                bool done;
                try { done = GpuDevices.SetEnabled(g, false, out output); }
                catch (Exception ex) { done = false; output = ex.Message; }
                if (done) sb.AppendLine(g.Name + ": disabled");
                else { ok = false; sb.AppendLine(g.Name + ": FAILED - " + Shell.LastLine(output)); }
            }
            log = sb.ToString();
            return ok;
        }

        // Full shut down / restart (not the Fast Startup hybrid one), so the eGPU starts clean next time.
        public static bool PowerOff(bool restart, out string error)
        {
            string output;
            int code = Shell.Run("shutdown.exe", (restart ? "/r" : "/s") + " /t 0", 15000, out output);
            error = code == 0 ? null : Shell.LastLine(output);
            return code == 0;
        }
    }
}
