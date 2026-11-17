// Removes an NVIDIA or AMD graphics driver completely, the way Display Driver Uninstaller does:
// stops the vendor's processes and services, deletes its driver packages from the driver store,
// removes its devices, deletes the leftover folders and uninstall entries, then rescans the devices
// so Windows falls back to the Microsoft Basic Display Adapter.
//
// AMD CPUs share the vendor name with Radeon GPUs, so for AMD only the display driver, its HDMI audio
// and the Radeon software parts are matched (never the chipset drivers, PCI id 1022).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GpuToggle
{
    static class DriverCleaner
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string existing, string newName, int flags);
        const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;

        static readonly string[] AmdSoftwarePrefixes = { "u0", "amdfendr", "amdlog", "amdxe", "atihd", "atiumd" };

        // ---------------------------------------------------------------- plan

        public static CleanupPlan Plan(string brand)
        {
            var plan = new CleanupPlan();
            plan.Brand = brand;
            bool nv = brand == "NVIDIA";
            plan.VenId = nv ? PciVendor.Nvidia : PciVendor.Amd;
            if (nv) plan.Markers.Add("nvidia corporation");
            else { plan.Markers.Add("cnext"); plan.Markers.Add("radeon"); plan.Markers.Add("amdfendrsr"); plan.Markers.Add("atiesrxx"); plan.Markers.Add("amdrsserv"); }

            foreach (var p in DriverStore.List())
                if (nv ? IsNvidiaPackage(p) : IsAmdPackage(p))
                {
                    plan.Packages.Add(p);
                    if (!string.IsNullOrEmpty(p.StoreFolder)) plan.Markers.Add(p.StoreFolder.ToLowerInvariant());
                }

            plan.Devices = Devices(plan.VenId);
            plan.Services = Services(plan.Markers);
            plan.Folders = ExistingFolders(nv);
            plan.UninstallEntries = UninstallEntries(nv);
            return plan;
        }

        static bool IsNvidiaPackage(DriverPackage p)
        {
            if (p.Provider.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0) return false;
            // NVIDIA (Mellanox) network adapters are not part of the graphics driver.
            return !p.Class.Equals("Net", StringComparison.OrdinalIgnoreCase) && !p.Class.Equals("NetService", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsAmdPackage(DriverPackage p)
        {
            bool amd = p.Provider.IndexOf("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) >= 0
                || p.Provider.Equals("AMD", StringComparison.OrdinalIgnoreCase)
                || p.Provider.IndexOf("ATI Technologies", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!amd) return false;
            if (p.Class.Equals("Display", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string pre in AmdSoftwarePrefixes)
                if (p.OriginalName.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The GPU, its HDMI / DP audio and USB-C functions (PCI) and the audio endpoints (HDAUDIO).
        static List<string[]> Devices(uint ven)
        {
            var l = new List<string[]>();
            var rx = new Regex("^(PCI|HDAUDIO).*VEN_" + ven.ToString("X4") + "(&|$)", RegexOptions.IgnoreCase);
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity"))
                    foreach (ManagementObject mo in s.Get())
                    {
                        string id = Convert.ToString(mo["PNPDeviceID"]);
                        if (rx.IsMatch(id)) l.Add(new string[] { id, Convert.ToString(mo["Name"]) });
                    }
            }
            catch { }
            // Audio endpoints first: removing the PCI function also removes its children.
            l.Sort(delegate (string[] a, string[] b)
            {
                int pa = a[0].StartsWith("HDAUDIO", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                int pb = b[0].StartsWith("HDAUDIO", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                return pa != pb ? pa - pb : string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase);
            });
            return l;
        }

        // Services whose program lives in one of the vendor's folders or driver store packages.
        static List<string> Services(List<string> markers)
        {
            var l = new List<string>();
            try
            {
                using (var root = Registry.LocalMachine.OpenSubKey(Txt.RegPath("SYSTEM", "CurrentControlSet", "Services")))
                    foreach (string name in root.GetSubKeyNames())
                    {
                        try
                        {
                            using (var k = root.OpenSubKey(name))
                            {
                                string img = k == null ? null : k.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                                if (!string.IsNullOrEmpty(img) && MatchesAny(img, markers)) l.Add(name);
                            }
                        }
                        catch { }
                    }
            }
            catch { }
            return l;
        }

        static bool MatchesAny(string path, List<string> markers)
        {
            string low = path.ToLowerInvariant();
            foreach (string m in markers) if (m.Length > 0 && low.Contains(m)) return true;
            return false;
        }

        static List<string> CandidateFolders(bool nv)
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var l = new List<string>();
            if (nv)
            {
                l.Add(Path.Combine(pf, "NVIDIA Corporation"));
                l.Add(Path.Combine(pf86, "NVIDIA Corporation"));
                l.Add(Path.Combine(pd, "NVIDIA Corporation"));
                l.Add(Path.Combine(pd, "NVIDIA"));
                l.Add(Path.Combine(local, "NVIDIA"));
                l.Add(Path.Combine(local, "NVIDIA Corporation"));
                l.Add(Path.Combine(roaming, "NVIDIA"));
                l.Add(Path.Combine(sys, "NVIDIA"));          // the installer's extract folder
            }
            else
            {
                // Program Files/AMD and ProgramData/AMD also hold the chipset software: only the Radeon parts go.
                l.Add(Path.Combine(Path.Combine(pf, "AMD"), "CNext"));
                l.Add(Path.Combine(local, "AMD"));            // shader caches and Radeon settings
                l.Add(Path.Combine(local, "RadeonInstaller"));
                l.Add(Path.Combine(sys, "AMD"));             // the installer's extract folder
            }
            return l;
        }

        static List<string> ExistingFolders(bool nv)
        {
            var l = new List<string>();
            foreach (string f in CandidateFolders(nv))
                if (!string.IsNullOrEmpty(f) && Directory.Exists(f) && !l.Contains(f)) l.Add(f);
            return l;
        }

        static string UninstallRoot(bool wow)
        {
            return wow ? Txt.RegPath("SOFTWARE", "WOW6432Node", "Microsoft", "Windows", "CurrentVersion", "Uninstall")
                       : Txt.RegPath("SOFTWARE", "Microsoft", "Windows", "CurrentVersion", "Uninstall");
        }

        static List<string[]> UninstallEntries(bool nv)
        {
            var l = new List<string[]>();
            foreach (bool wow in new bool[] { false, true })
            {
                string rootPath = UninstallRoot(wow);
                try
                {
                    using (var root = Registry.LocalMachine.OpenSubKey(rootPath))
                    {
                        if (root == null) continue;
                        foreach (string name in root.GetSubKeyNames())
                        {
                            try
                            {
                                using (var k = root.OpenSubKey(name))
                                {
                                    if (k == null) continue;
                                    string pub = (k.GetValue("Publisher") as string) ?? "";
                                    string disp = (k.GetValue("DisplayName") as string) ?? "";
                                    string loc = ((k.GetValue("InstallLocation") as string) ?? "").ToLowerInvariant();
                                    bool hit;
                                    if (nv) hit = pub.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
                                    else hit = pub.IndexOf("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) >= 0
                                        && (disp.IndexOf("AMD Software", StringComparison.OrdinalIgnoreCase) >= 0
                                            || disp.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0
                                            || loc.Contains("cnext"));
                                    if (hit) l.Add(new string[] { rootPath + Txt.BS + name, disp.Length > 0 ? disp : name });
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return l;
        }

        public static void LogPlan(CleanupPlan plan, Action<string> log)
        {
            log("Found for " + plan.Brand + ": " + plan.Summary + ".");
            foreach (var p in plan.Packages) log("  driver   " + p);
            foreach (var d in plan.Devices) log("  device   " + d[1] + "  (" + d[0] + ")");
            foreach (var s in plan.Services) log("  service  " + s);
            foreach (var f in plan.Folders) log("  folder   " + f);
            foreach (var u in plan.UninstallEntries) log("  entry    " + u[1]);
        }

        // ---------------------------------------------------------------- execute

        public static void Execute(CleanupPlan plan, bool restorePoint, bool removeFolders, Action<string> log)
        {
            string output;
            int code;

            if (restorePoint)
            {
                log("Creating a restore point (can take a minute)...");
                string err = CreateRestorePoint("GPU Toggle: before removing the " + plan.Brand + " driver");
                log(err == null ? "  Restore point created (Windows makes at most one every 24 hours)." : "  Restore point failed: " + err);
            }

            log("Stopping " + plan.Brand + " programs...");
            int pid = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == pid) continue;
                    string file = p.MainModule.FileName;
                    if (file != null && MatchesAny(file, plan.Markers))
                    {
                        p.Kill();
                        log("  ended " + p.ProcessName);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }

            foreach (string s in plan.Services)
            {
                Shell.Run("sc.exe", "stop " + Txt.Q + s + Txt.Q, 30000, out output);
            }

            log("Deleting driver packages...");
            foreach (var p in plan.Packages)
            {
                code = Shell.Run("pnputil.exe", "/delete-driver " + p.OemInf + " /uninstall /force", out output);
                log("  " + (Shell.Ok(code) ? "deleted " : "FAILED  ") + p.OemInf + " (" + p.OriginalName + ")" + (Shell.Ok(code) ? "" : ": " + Shell.LastLine(output)));
            }

            log("Deleting services...");
            foreach (string s in plan.Services)
            {
                code = Shell.Run("sc.exe", "delete " + Txt.Q + s + Txt.Q, 30000, out output);
                log("  " + (code == 0 || code == 1072 ? "deleted " : "FAILED  ") + s + (code == 0 || code == 1072 ? "" : ": " + Shell.LastLine(output)));
            }

            log("Removing devices...");
            foreach (var d in plan.Devices)
            {
                code = Shell.Run("pnputil.exe", "/remove-device " + Txt.Q + d[0] + Txt.Q, out output);
                log("  " + (Shell.Ok(code) ? "removed " : "FAILED  ") + d[1] + (Shell.Ok(code) ? "" : ": " + Shell.LastLine(output)));
            }

            if (removeFolders)
            {
                log("Deleting leftover folders...");
                foreach (string f in plan.Folders)
                {
                    int left = DeleteTree(f);
                    log("  " + (left == 0 ? "deleted " : "partly   ") + f + (left == 0 ? "" : " (" + left + " file(s) in use, deleted at the next restart)"));
                }
                foreach (var u in plan.UninstallEntries)
                {
                    try { Registry.LocalMachine.DeleteSubKeyTree(u[0], false); log("  entry deleted: " + u[1]); }
                    catch (Exception ex) { log("  entry FAILED: " + u[1] + ": " + ex.Message); }
                }
            }

            log("Scanning for hardware changes (the GPU comes back without the " + plan.Brand + " driver)...");
            Shell.Run("pnputil.exe", "/scan-devices", out output);
            log("Done. Restart Windows, then install the new driver.");
        }

        // null on success, otherwise the error.
        static string CreateRestorePoint(string description)
        {
            try
            {
                using (var mc = new ManagementClass(new ManagementScope("root/default"), new ManagementPath("SystemRestore"), null))
                {
                    var inParams = mc.GetMethodParameters("CreateRestorePoint");
                    inParams["Description"] = description;
                    inParams["RestorePointType"] = 12;   // MODIFY_SETTINGS
                    inParams["EventType"] = 100;         // BEGIN_SYSTEM_CHANGE
                    var outParams = mc.InvokeMethod("CreateRestorePoint", inParams, null);
                    uint rv = Convert.ToUInt32(outParams["ReturnValue"]);
                    if (rv == 0) return null;
                    if (rv == 1058) return "System Protection is turned off for the system drive";
                    return "error " + rv;
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        // Deletes a folder; files that are in use are scheduled for deletion at the next restart.
        // Returns how many entries could not be deleted now.
        static int DeleteTree(string dir)
        {
            int left = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
                    catch { left++; MoveFileEx(f, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
                }
                foreach (string d in Directory.GetDirectories(dir))
                {
                    // Do not follow junctions / symlinks out of the folder.
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                    {
                        try { Directory.Delete(d); } catch { left++; }
                        continue;
                    }
                    left += DeleteTree(d);
                }
                try { Directory.Delete(dir); }
                catch { MoveFileEx(dir, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
            }
            catch { left++; }
            return left;
        }

    }
}
