// One display adapter as Windows lists it (PnP device), plus PCI vendor ids.

using System;

namespace GpuToggle
{
    static class PciVendor
    {
        public const uint Nvidia = 0x10DE, Amd = 0x1002, Intel = 0x8086;
    }

    class GpuInfo
    {
        public string Name;
        public string InstanceId;
        public string Vendor;
        public uint VenId, DevId;
        public int ErrorCode;          // ConfigManagerErrorCode: 0 = OK, 22 = disabled
        public bool IsPhysical;        // PCI device (not a virtual/remote display adapter)

        public bool Enabled { get { return ErrorCode == 0; } }
        public bool IsNvidia { get { return VenId == PciVendor.Nvidia; } }

        public string StatusText
        {
            get
            {
                if (ErrorCode == 0) return "Enabled";
                if (ErrorCode == 22) return "Disabled";
                return "Error (" + ErrorCode + ")";
            }
        }
    }
}
