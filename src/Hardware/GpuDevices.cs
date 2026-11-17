// Lists the display adapters (WMI) and enables / disables them (pnputil).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace GpuToggle
{
    static class GpuDevices
    {
        public static List<GpuInfo> Scan()
        {
            var result = new List<GpuInfo>();
            string query = "SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Display'";
            using (var searcher = new ManagementObjectSearcher(query))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    var g = new GpuInfo();
                    g.Name = Convert.ToString(mo["Name"]);
                    g.InstanceId = Convert.ToString(mo["PNPDeviceID"]);
                    g.ErrorCode = mo["ConfigManagerErrorCode"] == null ? 0 : Convert.ToInt32(mo["ConfigManagerErrorCode"]);
                    g.IsPhysical = g.InstanceId.StartsWith("PCI", StringComparison.OrdinalIgnoreCase);
                    var m = Regex.Match(g.InstanceId, "VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        g.VenId = Convert.ToUInt32(m.Groups[1].Value, 16);
                        g.DevId = Convert.ToUInt32(m.Groups[2].Value, 16);
                    }
                    g.Vendor = VendorName(g.VenId);
                    result.Add(g);
                }
            }
            result.Sort(delegate (GpuInfo a, GpuInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return result;
        }

        static string VendorName(uint ven)
        {
            switch (ven)
            {
                case 0: return "Virtual";
                case PciVendor.Nvidia: return "NVIDIA";
                case PciVendor.Amd: return "AMD";
                case PciVendor.Intel: return "Intel";
                default: return "VEN_" + ven.ToString("X4");
            }
        }

        // Enabled physical GPUs, NVIDIA first (the PCIe monitor and the OSD prefer them: NVML works there).
        public static List<GpuInfo> ActiveNvidiaFirst(List<GpuInfo> gpus)
        {
            var l = new List<GpuInfo>();
            foreach (var g in gpus) if (g.IsPhysical && g.Enabled && g.IsNvidia) l.Add(g);
            foreach (var g in gpus) if (g.IsPhysical && g.Enabled && !g.IsNvidia) l.Add(g);
            return l;
        }

        // Uses pnputil (Windows 10 2004+). Returns true on success.
        public static bool SetEnabled(GpuInfo g, bool enable, out string output)
        {
            var psi = new ProcessStartInfo("pnputil.exe",
                (enable ? "/enable-device" : "/disable-device") + " " + Txt.Q + g.InstanceId + Txt.Q);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            using (var p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0 || p.ExitCode == 3010; // 3010 = success, reboot required
            }
        }
    }
}
