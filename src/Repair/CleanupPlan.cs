// What a driver cleanup will remove for one GPU vendor. Built by DriverCleaner.Plan, shown to the
// user in the log, then executed by DriverCleaner.Execute.

using System.Collections.Generic;

namespace GpuToggle
{
    class CleanupPlan
    {
        public string Brand;                                         // "NVIDIA" or "AMD"
        public uint VenId;
        public List<DriverPackage> Packages = new List<DriverPackage>();
        public List<string[]> Devices = new List<string[]>();        // { instance id, name }
        public List<string> Services = new List<string>();
        public List<string> Folders = new List<string>();
        public List<string[]> UninstallEntries = new List<string[]>(); // { HKLM sub key path, display name }
        public List<string> Markers = new List<string>();            // lower-case path parts that identify the vendor's files

        public bool Empty
        {
            get { return Packages.Count + Devices.Count + Services.Count + Folders.Count + UninstallEntries.Count == 0; }
        }

        public string Summary
        {
            get
            {
                return Packages.Count + " driver package(s), " + Devices.Count + " device(s), " + Services.Count + " service(s), " +
                    Folders.Count + " folder(s), " + UninstallEntries.Count + " uninstall entr" + (UninstallEntries.Count == 1 ? "y" : "ies");
            }
        }
    }
}
