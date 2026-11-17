// Tries to clear "Windows has stopped this device because it has reported problems (Code 43)" on a GPU.
// Goes from the gentlest fix to the strongest and stops as soon as the GPU works again:
//   1. restart the device (pnputil /restart-device, else disable + enable)
//   2. remove the device and rescan, so Windows installs it again from scratch
//   3. remove ghost (disconnected) display adapters, e.g. the same eGPU seen on another port, and rescan
//   4. optional, for eGPUs with an NVIDIA card: set RM1774520 = 1 in the driver key and restart the device
//   5. optional, for eGPUs: turn off Fast Startup and PCIe link power saving (take effect after a full restart)
// If the GPU still fails, the driver itself is broken: the next step is the driver cleanup.

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace GpuToggle
{
    static class Code43Fixer
    {
        public const int Code = 43;

        public static List<GpuInfo> Broken()
        {
            var l = new List<GpuInfo>();
            foreach (var g in GpuDevices.Scan()) if (g.IsPhysical && g.ErrorCode == Code) l.Add(g);
            return l;
        }

        // Returns true when no GPU reports code 43 any more.
        public static bool Run(bool egpuTweaks, Action<string> log)
        {
            var broken = Broken();
            if (broken.Count == 0) { log("No GPU reports code 43."); return true; }
            foreach (var g in broken) log("Code 43: " + g.Name + "  (" + g.InstanceId + ")");

            string output;
            int code;

            log("Step 1: restarting the device...");
            foreach (var g in broken)
            {
                code = Shell.Run("pnputil.exe", "/restart-device " + Q(g.InstanceId), out output);
                if (!Shell.Ok(code))
                {
                    log("  restart-device not available, disabling and enabling instead.");
                    Shell.Run("pnputil.exe", "/disable-device " + Q(g.InstanceId), out output);
                    Thread.Sleep(1500);
                    Shell.Run("pnputil.exe", "/enable-device " + Q(g.InstanceId), out output);
                }
            }
            if (Check(broken, 4000, log)) return true;

            log("Step 2: removing the device and scanning for hardware changes...");
            foreach (var g in StillBroken(broken))
            {
                code = Shell.Run("pnputil.exe", "/remove-device " + Q(g.InstanceId), out output);
                log("  " + (Shell.Ok(code) ? "removed " : "FAILED  ") + g.Name + (Shell.Ok(code) ? "" : ": " + Shell.LastLine(output)));
            }
            Shell.Run("pnputil.exe", "/scan-devices", out output);
            if (Check(broken, 8000, log)) return true;

            log("Step 3: removing ghost (disconnected) display adapters...");
            code = Shell.Run("pnputil.exe", "/remove-device /class Display /disconnected", out output);
            log("  " + (Shell.Ok(code) ? "done" : "skipped: " + Shell.LastLine(output)));
            Shell.Run("pnputil.exe", "/scan-devices", out output);
            if (Check(broken, 8000, log)) return true;

            if (egpuTweaks)
            {
                // After the reinstall steps: a reinstall rebuilds the class key and drops the value.
                var nv = new List<GpuInfo>();
                foreach (var g in StillBroken(broken)) if (g.IsNvidia) nv.Add(g);
                if (nv.Count > 0)
                {
                    log("Step 4: eGPU fix for NVIDIA (" + EgpuFixValue + " = 1 in the driver key, then restart the device)...");
                    foreach (var g in nv)
                    {
                        string key;
                        string err = ApplyEgpuFix(g.InstanceId, out key);
                        log("  " + (err == null ? "set " : "FAILED  ") + g.Name + (err == null ? ": HKLM" + Txt.BS + key : ": " + err));
                        if (err != null) continue;
                        code = Shell.Run("pnputil.exe", "/restart-device " + Q(g.InstanceId), out output);
                        if (!Shell.Ok(code))
                        {
                            Shell.Run("pnputil.exe", "/disable-device " + Q(g.InstanceId), out output);
                            Thread.Sleep(1500);
                            Shell.Run("pnputil.exe", "/enable-device " + Q(g.InstanceId), out output);
                        }
                    }
                    if (Check(broken, 4000, log))
                    {
                        log("  Reinstalling or updating the driver removes the value: run Fix code 43 again afterwards.");
                        return true;
                    }
                }

                log("Step 5: eGPU power settings (they take effect after a full restart)...");
                try
                {
                    using (var k = Registry.LocalMachine.CreateSubKey(Txt.RegPath("SYSTEM", "CurrentControlSet", "Control", "Session Manager", "Power")))
                        k.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
                    log("  Fast Startup turned off (the GPU is now set up from scratch at every boot).");
                }
                catch (Exception ex) { log("  Fast Startup: FAILED: " + ex.Message); }
                int a = Shell.Run("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0", out output);
                int d = Shell.Run("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0", out output);
                Shell.Run("powercfg.exe", "/setactive SCHEME_CURRENT", out output);
                log("  PCIe Link State Power Management: " + (a == 0 && d == 0 ? "off" : "FAILED: " + Shell.LastLine(output)));
            }

            log("Still code 43. What usually helps next:");
            log("  - Restart Windows (Start > Power > Restart, not Shut down).");
            log("  - Remove the driver with Driver cleanup on this page (best in Safe Mode), restart, then install the latest driver.");
            log("  - eGPU habit: before shutting down, Disable the eGPU on the GPUs page, then unplug it.");
            log("    After Windows has started and you are signed in, plug it in, then Enable it.");
            log("  - eGPU: try another USB4 / Thunderbolt port and cable; in the BIOS enable Thunderbolt PCIe tunneling");
            log("    and Above 4G decoding / Resizable BAR.");
            log("  - Still failing on every driver and machine: the card or the enclosure is faulty.");
            return false;
        }

        static string Q(string s) { return Txt.Q + s + Txt.Q; }

        // NVIDIA Resource Manager toggle that makes the driver accept a GPU in an external enclosure.
        // Not documented by NVIDIA; found by nando4 (egpu.io nvidia-error43-fixer), script by AleCyriaco
        // (github.com/AleCyriaco/nvidia-error43-fix, MIT). Lives in the adapter's driver key:
        // HKLM/SYSTEM/CurrentControlSet/Control/Class/{4d36e968-...}/NNNN, RM1774520 = 1 (DWORD).
        public const string EgpuFixValue = "RM1774520";

        // Returns null on success, otherwise the error. key = the driver key that was written.
        static string ApplyEgpuFix(string instanceId, out string key)
        {
            key = null;
            try
            {
                string driver;
                using (var e = Registry.LocalMachine.OpenSubKey(Txt.RegPath("SYSTEM", "CurrentControlSet", "Enum") + Txt.BS + instanceId))
                    driver = e != null ? e.GetValue("Driver") as string : null;
                if (string.IsNullOrEmpty(driver)) return "no driver installed for this device";
                key = Txt.RegPath("SYSTEM", "CurrentControlSet", "Control", "Class") + Txt.BS + driver;
                using (var k = Registry.LocalMachine.OpenSubKey(key, true))
                {
                    if (k == null) return "driver key not found";
                    k.SetValue(EgpuFixValue, 1, RegistryValueKind.DWord);
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        static List<GpuInfo> StillBroken(List<GpuInfo> before)
        {
            var now = new Dictionary<string, GpuInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in GpuDevices.Scan()) now[g.InstanceId] = g;
            var l = new List<GpuInfo>();
            foreach (var g in before)
            {
                GpuInfo n;
                // A GPU that is not listed (yet) after a rescan counts as still broken.
                if (!now.TryGetValue(g.InstanceId, out n) || n.ErrorCode != 0) l.Add(g);
            }
            return l;
        }

        static bool Check(List<GpuInfo> before, int waitMs, Action<string> log)
        {
            Thread.Sleep(waitMs);
            var left = StillBroken(before);
            if (left.Count == 0) { log("Fixed: the GPU works again."); return true; }
            foreach (var g in left) log("  still failing: " + g.Name);
            return false;
        }
    }
}
