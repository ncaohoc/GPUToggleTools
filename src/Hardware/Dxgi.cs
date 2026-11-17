// DXGI: adapter list with LUIDs (used to match perf counters to GPUs) and the GPU Windows
// picks for each GPU preference.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GpuToggle
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
        void _EnumAdapters(); void _MakeWindowAssociation(); void _GetWindowAssociation(); void _CreateSwapChain(); void _CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter1
    {
        void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
        void _EnumOutputs(); void _GetDesc(); void _CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    // IDXGIFactory6 adds EnumAdapterByGpuPreference (vtable slot 29). The placeholders keep the slot order.
    [ComImport, Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory6
    {
        void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
        void _EnumAdapters(); void _MakeWindowAssociation(); void _GetWindowAssociation(); void _CreateSwapChain(); void _CreateSoftwareAdapter();
        void _EnumAdapters1(); void _IsCurrent();
        void _IsWindowedStereoEnabled(); void _CreateSwapChainForHwnd(); void _CreateSwapChainForCoreWindow(); void _GetSharedResourceAdapterLuid();
        void _RegisterStereoStatusWindow(); void _RegisterStereoStatusEvent(); void _UnregisterStereoStatus();
        void _RegisterOcclusionStatusWindow(); void _RegisterOcclusionStatusEvent(); void _UnregisterOcclusionStatus(); void _CreateSwapChainForComposition();
        void _GetCreationFlags();
        void _EnumAdapterByLuid(); void _EnumWarpAdapter();
        void _CheckFeatureSupport();
        [PreserveSig] int EnumAdapterByGpuPreference(uint index, int preference, ref Guid riid, out IntPtr adapter);
    }

    class DxgiAdapter { public uint Ven, Dev; public string Luid; public ulong DedicatedTotal; public string Name; }

    static class Dxgi
    {
        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);

        public static List<DxgiAdapter> List()
        {
            var list = new List<DxgiAdapter>();
            IDXGIFactory1 f;
            Guid iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out f) != 0 || f == null) return list;
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    IDXGIAdapter1 a;
                    if (f.EnumAdapters1(i, out a) != 0 || a == null) break;
                    DXGI_ADAPTER_DESC1 d;
                    if (a.GetDesc1(out d) == 0)
                    {
                        var x = new DxgiAdapter();
                        x.Ven = d.VendorId;
                        x.Dev = d.DeviceId;
                        x.Luid = string.Format(Txt.Inv, "luid_0x{0:x8}_0x{1:x8}", (uint)d.LuidHigh, d.LuidLow);
                        x.DedicatedTotal = d.DedicatedVideoMemory.ToUInt64();
                        x.Name = d.Description;
                        list.Add(x);
                    }
                    Marshal.ReleaseComObject(a);
                }
            }
            finally { Marshal.ReleaseComObject(f); }
            return list;
        }

        // Which GPU Windows hands out for a preference: 1 = power saving, 2 = high performance.
        public static string PreferredAdapterName(int preference)
        {
            IDXGIFactory1 f;
            Guid iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out f) != 0 || f == null) return null;
            try
            {
                var f6 = f as IDXGIFactory6;
                if (f6 == null) return null;
                Guid aid = typeof(IDXGIAdapter1).GUID;
                IntPtr p;
                if (f6.EnumAdapterByGpuPreference(0, preference, ref aid, out p) != 0 || p == IntPtr.Zero) return null;
                try
                {
                    var a = (IDXGIAdapter1)Marshal.GetObjectForIUnknown(p);
                    DXGI_ADAPTER_DESC1 d;
                    string name = a.GetDesc1(out d) == 0 ? d.Description : null;
                    Marshal.ReleaseComObject(a);
                    return name;
                }
                finally { Marshal.Release(p); }
            }
            finally { Marshal.ReleaseComObject(f); }
        }

        // COM pointer (IDXGIAdapter1) of the adapter with this PCI vendor / device id, or IntPtr.Zero.
        // The caller releases it.
        public static IntPtr FindAdapter(uint ven, uint dev)
        {
            IDXGIFactory1 f;
            Guid iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out f) != 0 || f == null) return IntPtr.Zero;
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    IDXGIAdapter1 a;
                    if (f.EnumAdapters1(i, out a) != 0 || a == null) break;
                    DXGI_ADAPTER_DESC1 d;
                    bool match = a.GetDesc1(out d) == 0 && d.VendorId == ven && d.DeviceId == dev;
                    IntPtr p = match ? Marshal.GetComInterfaceForObject(a, typeof(IDXGIAdapter1)) : IntPtr.Zero;
                    Marshal.ReleaseComObject(a);
                    if (match) return p;
                }
            }
            finally { Marshal.ReleaseComObject(f); }
            return IntPtr.Zero;
        }
    }
}
