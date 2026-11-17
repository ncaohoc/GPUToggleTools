// One third-party driver package in the Windows driver store (C:/Windows/INF/oemNN.inf).

namespace GpuToggle
{
    class DriverPackage
    {
        public string OemInf;         // published name, e.g. oem12.inf (what pnputil /delete-driver takes)
        public string OriginalName;   // e.g. nv_dispi.inf
        public string StoreFolder;    // e.g. nv_dispi.inf_amd64_0123abcd (FileRepository folder), may be null
        public string Provider = "";
        public string Class = "";
        public string Version = "";

        public override string ToString()
        {
            return OemInf + "  " + OriginalName + "  [" + Class + "]  " + Provider + (Version.Length > 0 ? "  " + Version : "");
        }
    }
}
