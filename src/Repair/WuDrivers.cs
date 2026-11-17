// Whether Windows Update may install drivers by itself. Blocked = it does not replace the driver you
// install by hand (or reinstall an old one right after a cleanup).
//   HKLM/SOFTWARE/Microsoft/Windows/CurrentVersion/DriverSearching  SearchOrderConfig = 0 (device installation settings)
//   HKLM/SOFTWARE/Policies/Microsoft/Windows/WindowsUpdate          ExcludeWUDriversInQualityUpdate = 1 (policy)

using System;
using Microsoft.Win32;

namespace GpuToggle
{
    static class WuDrivers
    {
        static readonly string SearchKey = Txt.RegPath("SOFTWARE", "Microsoft", "Windows", "CurrentVersion", "DriverSearching");
        static readonly string PolicyKey = Txt.RegPath("SOFTWARE", "Policies", "Microsoft", "Windows", "WindowsUpdate");

        public static bool Blocked
        {
            get
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(PolicyKey))
                    {
                        object v = k != null ? k.GetValue("ExcludeWUDriversInQualityUpdate") : null;
                        if (v is int && (int)v == 1) return true;
                    }
                    using (var k = Registry.LocalMachine.OpenSubKey(SearchKey))
                    {
                        object v = k != null ? k.GetValue("SearchOrderConfig") : null;
                        return v is int && (int)v == 0;
                    }
                }
                catch { return false; }
            }
        }

        public static void SetBlocked(bool blocked)
        {
            using (var k = Registry.LocalMachine.CreateSubKey(SearchKey))
                k.SetValue("SearchOrderConfig", blocked ? 0 : 1, RegistryValueKind.DWord);
            using (var k = Registry.LocalMachine.CreateSubKey(PolicyKey))
            {
                if (blocked) k.SetValue("ExcludeWUDriversInQualityUpdate", 1, RegistryValueKind.DWord);
                else k.DeleteValue("ExcludeWUDriversInQualityUpdate", false);
            }
        }
    }
}
