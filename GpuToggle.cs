// GpuToggle - WinForms app to enable/disable GPUs (eGPU / iGPU) and monitor them.
// Build with build.bat (uses the C# compiler that ships with Windows).
// Written in C# 5 syntax so the built-in csc.exe can compile it.
// NOTE: no backslash characters anywhere in this file (they can get stripped when downloading).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GpuToggle
{
    static class Txt
    {
        public static readonly string Q = ((char)34).ToString();
        public static readonly string NL = Environment.NewLine;
        public static readonly char BS = (char)92;
        public const string NA = "-";
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    }

    // ------------------------------------------------------------------ GPU list / toggle

    class GpuInfo
    {
        public string Name;
        public string InstanceId;
        public string Vendor;
        public uint VenId, DevId;
        public int ErrorCode;          // ConfigManagerErrorCode: 0 = OK, 22 = disabled
        public bool IsPhysical;        // PCI device (not a virtual/remote display adapter)

        public bool Enabled { get { return ErrorCode == 0; } }

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

    static class Gpu
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
                case 0x10DE: return "NVIDIA";
                case 0x1002: return "AMD";
                case 0x8086: return "Intel";
                default: return "VEN_" + ven.ToString("X4");
            }
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

    // ------------------------------------------------------------------ Stats: DXGI (adapter LUIDs)

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
    }

    // ------------------------------------------------------------------ Stats: Windows GPU perf counters (all vendors)

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
            var result = new Dictionary<string, float>();
            var cat = new PerformanceCounterCategory("GPU Engine");
            InstanceDataCollection col = cat.ReadCategory()["Utilization Percentage"];
            var sums = new Dictionary<string, float>();
            var next = new Dictionary<string, CounterSample>();
            var pidLoad = new Dictionary<int, Dictionary<string, float>>();
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
            LastPidLoad = pidLoad;
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

    // ------------------------------------------------------------------ Stats: NVIDIA (nvidia-smi + NVAPI fan RPM)

    class NvSmi { public float Temp = -1, Power = -1, Limit = -1, FanPct = -1, MemUsed = -1, MemTotal = -1, Util = -1; }

    static class Nvidia
    {
        public static NvSmi Query()
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi.exe",
                    "--query-gpu=temperature.gpu,power.draw,power.limit,fan.speed,memory.used,memory.total,utilization.gpu --format=csv,noheader,nounits");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    string line = p.StandardOutput.ReadLine();
                    p.WaitForExit(3000);
                    if (string.IsNullOrEmpty(line)) return null;
                    string[] f = line.Split(',');
                    if (f.Length < 7) return null;
                    var r = new NvSmi();
                    r.Temp = Num(f[0]); r.Power = Num(f[1]); r.Limit = Num(f[2]); r.FanPct = Num(f[3]);
                    r.MemUsed = Num(f[4]); r.MemTotal = Num(f[5]); r.Util = Num(f[6]);
                    return r;
                }
            }
            catch { return null; }
        }

        static float Num(string s)
        {
            float v;
            return float.TryParse(s.Trim(), NumberStyles.Float, Txt.Inv, out v) ? v : -1;
        }

        // ---- NVAPI fan tachometer (RPM)
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr NvQuery(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumFn([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] IntPtr[] handles, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TachFn(IntPtr handle, out uint rpm);

        static bool nvapiTried, nvapiOk;
        static EnumFn enumGpus;
        static TachFn tach;

        static T Fn<T>(uint id) where T : class
        {
            IntPtr p = NvQuery(id);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        // Returns RPM, or -1 if unavailable
        public static int FanRpm()
        {
            try
            {
                if (!nvapiTried)
                {
                    nvapiTried = true;
                    var init = Fn<InitFn>(0x0150E828);
                    enumGpus = Fn<EnumFn>(0xE5AC921F);
                    tach = Fn<TachFn>(0x5F608315);
                    nvapiOk = init != null && enumGpus != null && tach != null && init() == 0;
                }
                if (!nvapiOk) return -1;
                var h = new IntPtr[64];
                int n;
                if (enumGpus(h, out n) != 0 || n < 1) return -1;
                uint rpm;
                return tach(h[0], out rpm) == 0 ? (int)rpm : -1;
            }
            catch { nvapiOk = false; return -1; }
        }
    }

    // ------------------------------------------------------------------ Displays: which monitor is driven by which GPU

    class DisplayInfo
    {
        public string Output, Monitor, Mode, GpuName, GpuDeviceId;
        public bool Primary;
    }

    static class Displays
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

        const int ATTACHED = 0x1, PRIMARY = 0x4;

        public static List<DisplayInfo> List()
        {
            var names = MonitorNames();
            var list = new List<DisplayInfo>();
            for (uint i = 0; i < 32; i++)
            {
                var ad = new DISPLAY_DEVICE();
                ad.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(null, i, ref ad, 0)) break;
                if ((ad.StateFlags & ATTACHED) == 0) continue;

                var di = new DisplayInfo();
                int k = ad.DeviceName.IndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
                di.Output = k >= 0 ? ad.DeviceName.Substring(k) : ad.DeviceName;
                di.Primary = (ad.StateFlags & PRIMARY) != 0;
                di.GpuName = ad.DeviceString;
                di.GpuDeviceId = ad.DeviceID;

                var mon = new DISPLAY_DEVICE();
                mon.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                di.Monitor = "Unknown monitor";
                if (EnumDisplayDevices(ad.DeviceName, 0, ref mon, 1)) // EDD_GET_DEVICE_INTERFACE_NAME
                {
                    di.Monitor = mon.DeviceString;
                    string key = MonitorKey(mon.DeviceID);
                    if (key != null)
                        foreach (var kv in names)
                            if (kv.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase)) { di.Monitor = kv.Value; break; }
                }

                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(ad.DeviceName, -1, ref dm)) // ENUM_CURRENT_SETTINGS
                    di.Mode = dm.dmPelsWidth + " x " + dm.dmPelsHeight + " @ " + dm.dmDisplayFrequency + " Hz";
                else
                    di.Mode = Txt.NA;
                list.Add(di);
            }
            return list;
        }

        // Interface path like  ??DISPLAY#GSM5B7F#5&abc&0&UID4352#{guid}  ->  DISPLAY<bs>GSM5B7F<bs>5&abc&0&UID4352
        static string MonitorKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int i = path.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase);
            int j = path.IndexOf("#{", StringComparison.Ordinal);
            if (i < 0 || j <= i) return null;
            return path.Substring(i, j - i).Replace('#', Txt.BS);
        }

        // WmiMonitorID: InstanceName -> friendly name from EDID
        static Dictionary<string, string> MonitorNames()
        {
            var d = new Dictionary<string, string>();
            try
            {
                using (var s = new ManagementObjectSearcher("root/wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        var raw = mo["UserFriendlyName"] as ushort[];
                        if (raw == null) continue;
                        var chars = new List<char>();
                        foreach (var c in raw) if (c != 0) chars.Add((char)c);
                        string name = new string(chars.ToArray()).Trim();
                        if (name.Length > 0) d[Convert.ToString(mo["InstanceName"])] = name;
                    }
                }
            }
            catch { }
            return d;
        }
    }

    // ------------------------------------------------------------------ PCIe link info (cfgmgr32 device properties)

    class PcieLink
    {
        public string Device;          // device name of this end of the link
        public int CurSpeed, CurWidth, MaxSpeed, MaxWidth;   // speed: 1 = Gen1 (2.5 GT/s) ... 5 = Gen5 (32 GT/s)

        public static string Gen(int speed) { return speed > 0 ? "Gen" + speed : "?"; }

        // Usable bandwidth per lane and direction in GB/s, after line encoding.
        public static double LaneGBs(int speed)
        {
            switch (speed)
            {
                case 1: return 0.25;
                case 2: return 0.5;
                case 3: return 0.985;
                case 4: return 1.969;
                case 5: return 3.938;
                case 6: return 7.563;
                default: return 0;
            }
        }

        public double CurGBs { get { return LaneGBs(CurSpeed) * CurWidth; } }
        public double MaxGBs { get { return LaneGBs(MaxSpeed) * MaxWidth; } }
        public string CurText { get { return Gen(CurSpeed) + " x" + CurWidth; } }
        public string MaxText { get { return Gen(MaxSpeed) + " x" + MaxWidth; } }
    }

    static class Pcie
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DEVPROPKEY { public Guid fmtid; public uint pid; }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[] buffer, ref uint size, uint flags);

        static readonly Guid PciKeys = new Guid("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
        static readonly Guid DevKeys = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");   // DEVPKEY_Device_*

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int len, uint flags);

        static byte[] Prop(uint dev, Guid fmt, uint pid)
        {
            var k = new DEVPROPKEY();
            k.fmtid = fmt;
            k.pid = pid;
            uint type, size = 0;
            CM_Get_DevNode_PropertyW(dev, ref k, out type, null, ref size, 0);
            if (size == 0) return null;
            var buf = new byte[size];
            if (CM_Get_DevNode_PropertyW(dev, ref k, out type, buf, ref size, 0) != 0) return null;
            return buf;
        }

        static int U32(uint dev, uint pid)
        {
            byte[] b = Prop(dev, PciKeys, pid);
            return b != null && b.Length >= 4 ? BitConverter.ToInt32(b, 0) : 0;
        }

        static string Name(uint dev)
        {
            byte[] b = Prop(dev, DevKeys, 14);                 // FriendlyName
            if (b == null) b = Prop(dev, DevKeys, 2);          // DeviceDesc
            return b == null ? "PCI device" : Encoding.Unicode.GetString(b).TrimEnd((char)0);
        }

        static PcieLink Read(uint dev)
        {
            var l = new PcieLink();
            l.CurSpeed = U32(dev, 9);
            l.CurWidth = U32(dev, 10);
            l.MaxSpeed = U32(dev, 11);
            l.MaxWidth = U32(dev, 12);
            if (l.CurWidth <= 0 && l.MaxWidth <= 0) return null;
            l.Device = Name(dev);
            return l;
        }

        // Links from the GPU up to the root port. [0] is the GPU's own link (null if it has none, e.g. an iGPU).
        public static List<PcieLink> Chain(string instanceId)
        {
            var chain = new List<PcieLink>();
            uint dev;
            if (CM_Locate_DevNodeW(out dev, instanceId, 0) != 0) return chain;
            chain.Add(Read(dev));
            for (int i = 0; i < 12; i++)
            {
                uint parent;
                if (CM_Get_Parent(out parent, dev, 0) != 0) break;
                dev = parent;
                var l = Read(dev);
                if (l != null) chain.Add(l);
            }
            return chain;
        }

        // Windows only reports link width/speed on endpoints, not on switch / root ports, so instead we look for
        // a USB4 / Thunderbolt eGPU controller above the GPU. Its PCIe tunnel is usually the real limit.
        static readonly string[] TunnelIds = {
            "VEN_1B21&DEV_2461", "VEN_1B21&DEV_2463", "VEN_1B21&DEV_2464",      // ASMedia ASM2464 (USB4)
            "VEN_8086&DEV_15E7", "VEN_8086&DEV_15EA", "VEN_8086&DEV_15EF",      // Intel Titan Ridge (TB3)
            "VEN_8086&DEV_1133", "VEN_8086&DEV_1136", "VEN_8086&DEV_0B26",      // Intel Maple / Goshen Ridge (TB4)
            "VEN_8086&DEV_1578", "VEN_8086&DEV_1576", "VEN_8086&DEV_15D3", "VEN_8086&DEV_15DA" };   // Alpine Ridge (TB3)

        public static string Tunnel(string instanceId)
        {
            uint dev;
            if (CM_Locate_DevNodeW(out dev, instanceId, 0) != 0) return null;
            for (int i = 0; i < 12; i++)
            {
                uint parent;
                if (CM_Get_Parent(out parent, dev, 0) != 0) break;
                dev = parent;
                var sb = new StringBuilder(512);
                if (CM_Get_Device_IDW(dev, sb, sb.Capacity, 0) != 0) continue;
                string id = sb.ToString().ToUpperInvariant();
                string name = Name(dev);
                foreach (var t in TunnelIds)
                    if (id.Contains(t)) return (t.StartsWith("VEN_1B21", StringComparison.Ordinal) ? "USB4 (ASMedia ASM2464)" : "Thunderbolt (Intel)");
                if (name.IndexOf("Thunderbolt", StringComparison.OrdinalIgnoreCase) >= 0) return "Thunderbolt";
                if (name.IndexOf("USB4", StringComparison.OrdinalIgnoreCase) >= 0) return "USB4";
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ NVML: live PCIe traffic counters (NVIDIA only)

    static class Nvml
    {
        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPciInfo_v3(IntPtr device, IntPtr pci);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPcieThroughput(IntPtr device, int counter, out uint kbPerSec);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCurrPcieLinkGeneration(IntPtr device, out uint gen);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr device, out uint width);

        static bool tried, ok;

        static bool Init()
        {
            if (!tried)
            {
                tried = true;
                try { ok = nvmlInit_v2() == 0; } catch { ok = false; }
            }
            return ok;
        }

        // Handle of the NVIDIA GPU with this PCI vendor/device id, or IntPtr.Zero.
        public static IntPtr Find(uint ven, uint dev)
        {
            if (!Init()) return IntPtr.Zero;
            uint n;
            if (nvmlDeviceGetCount_v2(out n) != 0) return IntPtr.Zero;
            IntPtr buf = Marshal.AllocHGlobal(256);
            try
            {
                for (uint i = 0; i < n; i++)
                {
                    IntPtr h;
                    if (nvmlDeviceGetHandleByIndex_v2(i, out h) != 0) continue;
                    for (int k = 0; k < 256; k++) Marshal.WriteByte(buf, k, 0);
                    if (nvmlDeviceGetPciInfo_v3(h, buf) != 0) continue;
                    uint id = (uint)Marshal.ReadInt32(buf, 28);   // pciDeviceId = device << 16 | vendor
                    if (id == ((dev << 16) | ven)) return h;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return IntPtr.Zero;
        }

        // Bytes per second over the last ~20 ms. rx = host -> GPU, tx = GPU -> host. False if unsupported.
        public static bool Throughput(IntPtr h, out double rx, out double tx)
        {
            uint r, t;
            rx = tx = 0;
            if (nvmlDeviceGetPcieThroughput(h, 1, out r) != 0 || nvmlDeviceGetPcieThroughput(h, 0, out t) != 0) return false;
            rx = r * 1024.0;
            tx = t * 1024.0;
            return true;
        }

        public static string Link(IntPtr h, out int gen, out int width)
        {
            uint g, w;
            gen = width = 0;
            if (nvmlDeviceGetCurrPcieLinkGeneration(h, out g) != 0 || nvmlDeviceGetCurrPcieLinkWidth(h, out w) != 0) return Txt.NA;
            gen = (int)g; width = (int)w;
            return "Gen" + g + " x" + w;
        }
    }

    // ------------------------------------------------------------------ PCIe bandwidth test (D3D11 copies between system memory and VRAM)

    class BandwidthResult
    {
        public string Error;
        public double UploadGBs, DownloadGBs;     // host -> GPU, GPU -> host (best of several runs)
        public List<PcieLink> Chain = new List<PcieLink>();       // links at idle
        public List<PcieLink> ChainLoad;                           // links sampled while the test was running
        public string Tunnel;                                      // USB4 / Thunderbolt controller above the GPU, if any
        public double NvmlRxPeak, NvmlTxPeak;                      // NVML counter peaks (bytes/s) during the test (informational)
        public bool Integrated;
    }

    // Raw vtable calls, so we only need the handful of D3D11 methods used here.
    static class D3D11Bandwidth
    {
        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);
        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels,
            uint sdkVersion, out IntPtr device, out int level, out IntPtr context);

        [StructLayout(LayoutKind.Sequential)]
        struct BUFFER_DESC { public uint ByteWidth, Usage, BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride; }
        [StructLayout(LayoutKind.Sequential)]
        struct QUERY_DESC { public uint Query, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)]
        struct MAPPED { public IntPtr pData; public uint RowPitch, DepthPitch; }
        [StructLayout(LayoutKind.Sequential)]
        struct DISJOINT { public ulong Frequency; public int Disjoint; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBufferFn(IntPtr self, ref BUFFER_DESC d, IntPtr init, out IntPtr buf);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateQueryFn(IntPtr self, ref QUERY_DESC d, out IntPtr q);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr res, uint sub, int type, uint flags, out MAPPED m);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr res, uint sub);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void AsyncFn(IntPtr self, IntPtr async);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDataFn(IntPtr self, IntPtr async, IntPtr data, uint size, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyFn(IntPtr self, IntPtr dst, IntPtr src);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FlushFn(IntPtr self);

        // ID3D11Device / ID3D11DeviceContext vtable slots
        const int DEV_CreateBuffer = 3, DEV_CreateQuery = 24;
        const int CTX_Map = 14, CTX_Unmap = 15, CTX_Begin = 27, CTX_End = 28, CTX_GetData = 29, CTX_CopyResource = 47, CTX_Flush = 111;

        const uint BufferBytes = 64u << 20;     // 64 MB per copy

        // Latest successful result per GPU instance id (the live monitor uses it as the link capacity).
        public static Dictionary<string, BandwidthResult> LastResults = new Dictionary<string, BandwidthResult>();

        static T Vt<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size), typeof(T)) as T;
        }

        class Ctx
        {
            public IntPtr Dev, Ctx_;
            public CreateBufferFn CreateBuffer;
            public CreateQueryFn CreateQuery;
            public MapFn Map;
            public UnmapFn Unmap;
            public AsyncFn Begin, End;
            public GetDataFn GetData;
            public CopyFn Copy;
            public FlushFn Flush;
            public List<IntPtr> Owned = new List<IntPtr>();

            public IntPtr Buffer(uint usage, uint bind, uint cpu)
            {
                var d = new BUFFER_DESC();
                d.ByteWidth = BufferBytes; d.Usage = usage; d.BindFlags = bind; d.CPUAccessFlags = cpu;
                IntPtr b;
                int hr = CreateBuffer(Dev, ref d, IntPtr.Zero, out b);
                if (hr != 0 || b == IntPtr.Zero) throw new Exception("CreateBuffer failed (0x" + hr.ToString("X8") + ")");
                Owned.Add(b);
                return b;
            }

            public IntPtr Query(uint type)
            {
                var d = new QUERY_DESC();
                d.Query = type;
                IntPtr q;
                int hr = CreateQuery(Dev, ref d, out q);
                if (hr != 0 || q == IntPtr.Zero) throw new Exception("CreateQuery failed (0x" + hr.ToString("X8") + ")");
                Owned.Add(q);
                return q;
            }

            // Spins until the query result is ready (S_OK). Returns false on timeout.
            public bool Wait(IntPtr q, IntPtr data, uint size)
            {
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    int hr = GetData(Ctx_, q, data, size, 0);
                    if (hr == 0) return true;
                    if (hr < 0 || sw.ElapsedMilliseconds > 60000) return false;
                    Thread.Sleep(0);
                }
            }

            public void Idle(IntPtr evt)
            {
                End(Ctx_, evt);
                Flush(Ctx_);
                if (!Wait(evt, IntPtr.Zero, 0)) throw new Exception("GPU did not respond");
            }
        }

        static IntPtr FindAdapter(uint ven, uint dev)
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

        // Times reps copies (alternating between two destinations) with GPU timestamps. Returns GB/s.
        static double Run(Ctx c, IntPtr[] dst, IntPtr[] src, int reps, IntPtr qDisjoint, IntPtr qStart, IntPtr qEnd, IntPtr evt)
        {
            c.Idle(evt);
            var sw = Stopwatch.StartNew();
            c.Begin(c.Ctx_, qDisjoint);
            c.End(c.Ctx_, qStart);
            for (int i = 0; i < reps; i++) c.Copy(c.Ctx_, dst[i % dst.Length], src[i % src.Length]);
            c.End(c.Ctx_, qEnd);
            c.End(c.Ctx_, qDisjoint);
            c.Flush(c.Ctx_);

            IntPtr buf = Marshal.AllocHGlobal(32);
            try
            {
                if (!c.Wait(qDisjoint, buf, (uint)Marshal.SizeOf(typeof(DISJOINT)))) throw new Exception("GPU did not respond");
                double wall = sw.Elapsed.TotalSeconds;
                var dj = (DISJOINT)Marshal.PtrToStructure(buf, typeof(DISJOINT));
                double seconds = wall;
                if (dj.Disjoint == 0 && dj.Frequency > 0 && c.Wait(qStart, buf, 8))
                {
                    ulong t0 = (ulong)Marshal.ReadInt64(buf);
                    if (c.Wait(qEnd, buf, 8))
                    {
                        ulong t1 = (ulong)Marshal.ReadInt64(buf);
                        if (t1 > t0) seconds = (t1 - t0) / (double)dj.Frequency;
                    }
                }
                return (double)BufferBytes * reps / seconds / 1e9;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // Calibrates the number of copies so one run takes about half a second, then keeps the best of 3 runs.
        static double Measure(Ctx c, IntPtr[] dst, IntPtr[] src, IntPtr qd, IntPtr qs, IntPtr qe, IntPtr evt)
        {
            double rate = Run(c, dst, src, 2, qd, qs, qe, evt);
            int reps = (int)Math.Ceiling(0.5 * rate * 1e9 / BufferBytes);
            reps = Math.Max(4, Math.Min(256, reps));
            double best = 0;
            for (int i = 0; i < 3; i++) best = Math.Max(best, Run(c, dst, src, reps, qd, qs, qe, evt));
            return best;
        }

        public static BandwidthResult Test(GpuInfo g, Action<string> progress)
        {
            var r = new BandwidthResult();
            try { r.Chain = Pcie.Chain(g.InstanceId); } catch { }
            r.Integrated = r.Chain.Count == 0 || r.Chain[0] == null;
            try { r.Tunnel = Pcie.Tunnel(g.InstanceId); } catch { }

            IntPtr adapter = FindAdapter(g.VenId, g.DevId);
            if (adapter == IntPtr.Zero) { r.Error = "This GPU is not available to DirectX (is it enabled and is the driver installed?)."; return r; }

            var c = new Ctx();
            try
            {
                int level;
                int hr = D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out c.Dev, out level, out c.Ctx_);
                if (hr != 0 || c.Dev == IntPtr.Zero) { r.Error = "Could not create a Direct3D 11 device (0x" + hr.ToString("X8") + ")."; return r; }

                c.CreateBuffer = Vt<CreateBufferFn>(c.Dev, DEV_CreateBuffer);
                c.CreateQuery = Vt<CreateQueryFn>(c.Dev, DEV_CreateQuery);
                c.Map = Vt<MapFn>(c.Ctx_, CTX_Map);
                c.Unmap = Vt<UnmapFn>(c.Ctx_, CTX_Unmap);
                c.Begin = Vt<AsyncFn>(c.Ctx_, CTX_Begin);
                c.End = Vt<AsyncFn>(c.Ctx_, CTX_End);
                c.GetData = Vt<GetDataFn>(c.Ctx_, CTX_GetData);
                c.Copy = Vt<CopyFn>(c.Ctx_, CTX_CopyResource);
                c.Flush = Vt<FlushFn>(c.Ctx_, CTX_Flush);

                progress("Allocating buffers...");
                const uint DEFAULT = 0, STAGING = 3, BIND_VB = 0x1, CPU_WRITE = 0x10000, CPU_READ = 0x20000;
                IntPtr up = c.Buffer(STAGING, 0, CPU_WRITE);                 // system memory, CPU writes
                IntPtr[] vram = { c.Buffer(DEFAULT, BIND_VB, 0), c.Buffer(DEFAULT, BIND_VB, 0) };
                IntPtr[] down = { c.Buffer(STAGING, 0, CPU_READ), c.Buffer(STAGING, 0, CPU_READ) };
                IntPtr qd = c.Query(3), qs = c.Query(2), qe = c.Query(2), evt = c.Query(0);   // disjoint, timestamp x2, event

                // Fill the upload buffer with non-trivial data.
                MAPPED m;
                hr = c.Map(c.Ctx_, up, 0, 2, 0, out m);   // D3D11_MAP_WRITE
                if (hr != 0) throw new Exception("Map failed (0x" + hr.ToString("X8") + ")");
                var chunk = new byte[1 << 20];
                new Random(1).NextBytes(chunk);
                for (long off = 0; off < BufferBytes; off += chunk.Length) Marshal.Copy(chunk, 0, new IntPtr(m.pData.ToInt64() + off), chunk.Length);
                c.Unmap(c.Ctx_, up, 0);

                // While traffic flows: re-read the link (GPUs lower its speed when idle) and, on NVIDIA,
                // record the NVML counter peaks.
                int phase = 1;   // 1 = upload, 2 = download, 0 = done
                IntPtr nv = IntPtr.Zero;
                try { nv = Nvml.Find(g.VenId, g.DevId); } catch { }
                var sampler = new Thread(delegate ()
                {
                    Thread.Sleep(150);
                    try { r.ChainLoad = Pcie.Chain(g.InstanceId); } catch { }
                    while (nv != IntPtr.Zero && phase != 0)
                    {
                        double rx, tx;
                        try
                        {
                            if (!Nvml.Throughput(nv, out rx, out tx)) break;
                            if (phase == 1) r.NvmlRxPeak = Math.Max(r.NvmlRxPeak, rx);
                            else r.NvmlTxPeak = Math.Max(r.NvmlTxPeak, tx);
                        }
                        catch { break; }
                        Thread.Sleep(30);
                    }
                });
                sampler.IsBackground = true;
                sampler.Start();
                try
                {
                    progress("Measuring host -> GPU...");
                    r.UploadGBs = Measure(c, vram, new IntPtr[] { up }, qd, qs, qe, evt);
                    phase = 2;
                    progress("Measuring GPU -> host...");
                    r.DownloadGBs = Measure(c, down, new IntPtr[] { vram[0] }, qd, qs, qe, evt);
                }
                finally { phase = 0; sampler.Join(2000); }
            }
            catch (Exception ex) { r.Error = ex.Message; }
            finally
            {
                foreach (var p in c.Owned) Marshal.Release(p);
                if (c.Ctx_ != IntPtr.Zero) Marshal.Release(c.Ctx_);
                if (c.Dev != IntPtr.Zero) Marshal.Release(c.Dev);
                Marshal.Release(adapter);
            }
            return r;
        }
    }

    // ------------------------------------------------------------------ Live PCIe monitor (NVML samples while apps / games run)

    class PcieSample
    {
        public DateTime Time;
        public double Rx, Tx;          // bytes/s: rx = host -> GPU, tx = GPU -> host (NVML counters)
        public double Dt;              // seconds since the previous sample
        public string Link, App;
        public bool Saturated;
    }

    class PcieEpisode
    {
        public DateTime Start, End;
        public double PeakPct;
        public string Dir;             // "Host -> GPU", "GPU -> Host" or "Both"
        public string App;
        public double Seconds { get { return (End - Start).TotalSeconds; } }
    }

    class PcieMonitor
    {
        public const int IntervalMs = 250;
        public const int MaxSamples = 4 * 3600 * 24;   // 24 h at 4 samples/s
        public const double MinEpisodeSeconds = 0.5;

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        public readonly object Lock = new object();
        public readonly List<PcieSample> Samples = new List<PcieSample>();
        public readonly List<PcieEpisode> Episodes = new List<PcieEpisode>();
        public double CapRx, CapTx;            // bytes/s that count as 100 %
        public string CapSource = "";
        public double ThresholdPct = 80;
        public double MeasRx, MeasTx;          // bytes/s the bandwidth test reached (0 = not measured)
        public int CapGen, CapLanes;           // link the theoretical bandwidth comes from (current gen x lanes in use)
        public GpuInfo Gpu;
        public string Error;

        // Running totals (under Lock)
        public double Seconds, SumRx, SumTx, SatSeconds, MaxRx, MaxTx;
        public DateTime MaxRxTime, MaxTxTime, Started;
        PcieEpisode open;
        int belowCount;

        IntPtr handle;
        Thread thread;
        volatile bool running;
        DateTime lastTime;
        uint lastPid;
        string lastApp = "";

        public bool Running { get { return running; } }

        public bool Attach(GpuInfo g)
        {
            Stop();
            Gpu = g;
            Error = null;
            handle = IntPtr.Zero;
            if (g == null) { Error = "No GPU selected."; return false; }
            if (g.VenId != 0x10DE)
            {
                Error = "Live PCIe traffic needs an NVIDIA GPU. AMD and Intel drivers do not expose PCIe traffic counters to apps. " +
                    "You can still use PCIe bandwidth... on the GPUs page to measure the link limit.";
                return false;
            }
            try { handle = Nvml.Find(g.VenId, g.DevId); } catch { handle = IntPtr.Zero; }
            if (handle == IntPtr.Zero) { Error = "NVML (nvml.dll from the NVIDIA driver) could not open this GPU."; return false; }
            double rx, tx;
            if (!Nvml.Throughput(handle, out rx, out tx)) { Error = "This GPU / driver does not report PCIe throughput through NVML."; handle = IntPtr.Zero; return false; }
            UpdateCapacity();
            return true;
        }

        // 100 % = theoretical bandwidth of the link. The bottleneck threshold is not a fixed % of it:
        // after a bandwidth test it is lowered to what the link really delivered (e.g. a Gen4 x4 eGPU
        // behind a USB4 tunnel tops out near 49 %), because traffic can never get above that.
        public void UpdateCapacity()
        {
            CapRx = CapTx = MeasRx = MeasTx = 0;
            CapGen = CapLanes = 0;
            CapSource = "theoretical link";
            if (Gpu == null) return;
            BandwidthResult r;
            bool have = D3D11Bandwidth.LastResults.TryGetValue(Gpu.InstanceId, out r) && r.UploadGBs > 0 && r.DownloadGBs > 0;
            List<PcieLink> chain = null;
            if (have) chain = r.ChainLoad != null && r.ChainLoad.Count > 0 && r.ChainLoad[0] != null ? r.ChainLoad : r.Chain;
            try { if (chain == null || chain.Count == 0 || chain[0] == null) chain = Pcie.Chain(Gpu.InstanceId); } catch { }
            if (chain != null && chain.Count > 0 && chain[0] != null) { CapRx = CapTx = chain[0].CurGBs * 1e9; CapGen = chain[0].CurSpeed; CapLanes = chain[0].CurWidth; }
            if (have)
            {
                MeasRx = r.UploadGBs * 1e9; MeasTx = r.DownloadGBs * 1e9;
                // Integrated GPU / no link info: the measured speed itself is the only reference.
                if (CapRx <= 0) CapRx = CapTx = Math.Max(MeasRx, MeasTx);
                CapRx = Math.Max(CapRx, MeasRx); CapTx = Math.Max(CapTx, MeasTx);
                ThresholdPct = Math.Floor(Math.Min(MeasRx / CapRx, MeasTx / CapTx) * 100);
            }
            else if (CapRx <= 0) CapRx = CapTx = 1e9;
        }

        public bool Measured { get { return MeasRx > 0 && MeasTx > 0; } }

        public void Start()
        {
            if (running || handle == IntPtr.Zero || !Measured) return;
            running = true;
            lastTime = DateTime.MinValue;
            lock (Lock) { if (Samples.Count == 0) Started = DateTime.Now; }
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            if (thread != null) { thread.Join(1000); thread = null; }
            lock (Lock) CloseEpisode();
        }

        public void Reset()
        {
            lock (Lock)
            {
                Samples.Clear(); Episodes.Clear();
                Seconds = SumRx = SumTx = SatSeconds = MaxRx = MaxTx = 0;
                open = null; belowCount = 0;
                Started = DateTime.Now;
                lastTime = DateTime.MinValue;
            }
        }

        string ForegroundApp()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                if (pid == lastPid) return lastApp;
                lastPid = pid;
                using (var p = Process.GetProcessById((int)pid)) lastApp = p.ProcessName;
            }
            catch { lastApp = "?"; }
            return lastApp;
        }

        void Loop()
        {
            int linkTick = 0;
            string link = Txt.NA;
            while (running)
            {
                var t0 = DateTime.Now;
                double rx, tx;
                bool ok;
                try { ok = Nvml.Throughput(handle, out rx, out tx); } catch { ok = false; rx = tx = 0; }
                if (!ok) { Error = "Lost the GPU (was it disabled or unplugged?)."; running = false; break; }
                if (linkTick-- <= 0)
                {
                    int gen = 0, width = 0;
                    try { link = Nvml.Link(handle, out gen, out width); } catch { }
                    double theo = PcieLink.LaneGBs(gen) * width * 1e9;
                    if (!Measured && theo > CapRx) { CapRx = CapTx = theo; CapGen = gen; CapLanes = width; }   // idle links run slower; measured caps are fixed
                    linkTick = 4;
                }
                Add(rx, tx, link, ForegroundApp());
                int wait = IntervalMs - (int)(DateTime.Now - t0).TotalMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
            }
        }

        void Add(double rx, double tx, string link, string app)
        {
            var now = DateTime.Now;
            var s = new PcieSample();
            s.Time = now; s.Rx = rx; s.Tx = tx; s.Link = link; s.App = app;
            s.Dt = lastTime == DateTime.MinValue ? IntervalMs / 1000.0 : Math.Min(2.0, (now - lastTime).TotalSeconds);
            lastTime = now;
            double pr = CapRx > 0 ? rx / CapRx * 100 : 0, pt = CapTx > 0 ? tx / CapTx * 100 : 0;
            s.Saturated = pr >= ThresholdPct || pt >= ThresholdPct;

            lock (Lock)
            {
                if (Samples.Count >= MaxSamples) Samples.RemoveRange(0, MaxSamples / 10);
                Samples.Add(s);
                Seconds += s.Dt; SumRx += rx * s.Dt; SumTx += tx * s.Dt;
                if (rx > MaxRx) { MaxRx = rx; MaxRxTime = now; }
                if (tx > MaxTx) { MaxTx = tx; MaxTxTime = now; }
                if (s.Saturated)
                {
                    SatSeconds += s.Dt;
                    belowCount = 0;
                    string dir = pr >= ThresholdPct && pt >= ThresholdPct ? "Both" : (pr >= ThresholdPct ? "Host -> GPU" : "GPU -> Host");
                    if (open == null)
                    {
                        open = new PcieEpisode();
                        open.Start = now.AddSeconds(-s.Dt);
                        open.Dir = dir;
                        open.App = app;
                    }
                    else if (open.Dir != dir) open.Dir = "Both";
                    open.End = now;
                    open.PeakPct = Math.Max(open.PeakPct, Math.Max(pr, pt));
                }
                else if (open != null && ++belowCount >= 2) CloseEpisode();   // 2 quiet samples end an episode
            }
        }

        void CloseEpisode()
        {
            if (open != null && open.Seconds >= MinEpisodeSeconds) Episodes.Add(open);
            open = null;
            belowCount = 0;
        }

        public PcieEpisode OpenEpisode { get { return open; } }

        public void ExportCsv(string path)
        {
            var sb = new StringBuilder();
            lock (Lock)
            {
                sb.Append("# GPU," + Csv(Gpu != null ? Gpu.Name : "") + Txt.NL);
                sb.Append("# 100 % = host->GPU " + (CapRx / 1e9).ToString("0.00", Txt.Inv) + " GB/s; GPU->host " + (CapTx / 1e9).ToString("0.00", Txt.Inv) + " GB/s (" + CapSource + ")" + Txt.NL);
                sb.Append("# Threshold," + ThresholdPct.ToString("0", Txt.Inv) + " %" + Txt.NL);
                if (Seconds > 0)
                    sb.Append("# Average GB/s,host->GPU " + (SumRx / Seconds / 1e9).ToString("0.000", Txt.Inv) + ",GPU->host " + (SumTx / Seconds / 1e9).ToString("0.000", Txt.Inv) + Txt.NL);
                sb.Append("# Peak GB/s,host->GPU " + (MaxRx / 1e9).ToString("0.000", Txt.Inv) + ",GPU->host " + (MaxTx / 1e9).ToString("0.000", Txt.Inv) + Txt.NL);
                sb.Append("# Bottleneck seconds," + SatSeconds.ToString("0.0", Txt.Inv) + Txt.NL + Txt.NL);

                sb.Append("episode_start,episode_end,seconds,direction,peak_pct,app" + Txt.NL);
                foreach (var e in Episodes)
                    sb.Append(e.Start.ToString("yyyy-MM-dd HH:mm:ss.fff", Txt.Inv) + "," + e.End.ToString("yyyy-MM-dd HH:mm:ss.fff", Txt.Inv) + "," +
                        e.Seconds.ToString("0.00", Txt.Inv) + "," + Csv(e.Dir) + "," + e.PeakPct.ToString("0", Txt.Inv) + "," + Csv(e.App) + Txt.NL);
                sb.Append(Txt.NL);

                sb.Append("time,host_to_gpu_GBps,gpu_to_host_GBps,host_to_gpu_pct,gpu_to_host_pct,link,foreground_app,bottleneck" + Txt.NL);
                foreach (var s in Samples)
                    sb.Append(s.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", Txt.Inv) + "," +
                        (s.Rx / 1e9).ToString("0.000", Txt.Inv) + "," + (s.Tx / 1e9).ToString("0.000", Txt.Inv) + "," +
                        (CapRx > 0 ? s.Rx / CapRx * 100 : 0).ToString("0.0", Txt.Inv) + "," + (CapTx > 0 ? s.Tx / CapTx * 100 : 0).ToString("0.0", Txt.Inv) + "," +
                        Csv(s.Link) + "," + Csv(s.App) + "," + (s.Saturated ? "1" : "0") + Txt.NL);
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        static string Csv(string v)
        {
            if (v == null) return "";
            if (v.IndexOf(',') < 0 && v.IndexOf('"') < 0) return v;
            return Txt.Q + v.Replace(Txt.Q, Txt.Q + Txt.Q) + Txt.Q;
        }
    }

    // ------------------------------------------------------------------ Stats collection

    class GpuStats
    {
        public string Load = Txt.NA, Temp = Txt.NA, Power = Txt.NA, Fan = Txt.NA, Vram = Txt.NA;
        public float TempValue = -1;
    }

    class StatsResult
    {
        public Dictionary<string, GpuStats> ByInstance = new Dictionary<string, GpuStats>();
        public List<DisplayInfo> Displays = new List<DisplayInfo>();
        public Dictionary<string, string> LuidNames = new Dictionary<string, string>();
        public Dictionary<string, string> AppRunning = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    static class Stats
    {
        static string Gb(double bytes) { return (bytes / 1073741824.0).ToString("0.0", Txt.Inv); }

        // Which GPU a running app is using right now (from the per-process GPU Engine counters).
        static string RunningText(string path, StatsResult r, Dictionary<int, Dictionary<string, float>> pidLoad)
        {
            var pids = new List<int>();
            try
            {
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
                {
                    pids.Add(p.Id);
                    p.Dispose();
                }
            }
            catch { }
            if (pids.Count == 0) return "Not running";

            var per = new Dictionary<string, float>();
            foreach (int pid in pids)
            {
                Dictionary<string, float> d;
                if (!pidLoad.TryGetValue(pid, out d)) continue;
                foreach (var kv in d)
                {
                    float cur;
                    per.TryGetValue(kv.Key, out cur);
                    per[kv.Key] = Math.Min(100f, cur + kv.Value);
                }
            }
            if (per.Count == 0) return "Running - no GPU activity yet";

            string best = null;
            float bestValue = -1;
            foreach (var kv in per)
                if (kv.Value > bestValue) { best = kv.Key; bestValue = kv.Value; }
            string name;
            if (!r.LuidNames.TryGetValue(best, out name)) name = "Unknown GPU";
            return name + "   " + bestValue.ToString("0", Txt.Inv) + " %";
        }

        public static StatsResult Collect(List<GpuInfo> gpus)
        {
            return Collect(gpus, new List<string>());
        }

        public static StatsResult Collect(List<GpuInfo> gpus, List<string> appPaths)
        {
            var r = new StatsResult();
            try { r.Displays = Displays.List(); } catch { }

            List<DxgiAdapter> adapters = new List<DxgiAdapter>();
            Dictionary<string, float> load = new Dictionary<string, float>();
            Dictionary<string, long> mem = new Dictionary<string, long>();
            try { adapters = Dxgi.List(); } catch { }
            try { load = PerfGpu.Load(); } catch { }
            try { mem = PerfGpu.DedicatedUsage(); } catch { }

            bool anyNvidia = false;
            foreach (var g in gpus) if (g.Enabled && g.VenId == 0x10DE) anyNvidia = true;
            NvSmi nv = anyNvidia ? Nvidia.Query() : null;
            int rpm = anyNvidia ? Nvidia.FanRpm() : -1;

            foreach (var g in gpus)
            {
                if (!g.Enabled || !g.IsPhysical) continue;
                var s = new GpuStats();

                DxgiAdapter ad = null;
                foreach (var a in adapters) if (a.Ven == g.VenId && a.Dev == g.DevId) { ad = a; break; }
                if (ad != null)
                {
                    float l;
                    if (load.TryGetValue(ad.Luid, out l)) s.Load = l.ToString("0", Txt.Inv) + " %";
                    long used;
                    if (mem.TryGetValue(ad.Luid, out used) && ad.DedicatedTotal > 0)
                        s.Vram = Gb(used) + " / " + Gb(ad.DedicatedTotal) + " GB";
                }

                if (g.VenId == 0x10DE && nv != null)
                {
                    if (nv.Util >= 0) s.Load = nv.Util.ToString("0", Txt.Inv) + " %";
                    if (nv.Temp >= 0) { s.Temp = nv.Temp.ToString("0", Txt.Inv) + " C"; s.TempValue = nv.Temp; }
                    if (nv.Power >= 0)
                        s.Power = nv.Power.ToString("0", Txt.Inv) + (nv.Limit > 0 ? " / " + nv.Limit.ToString("0", Txt.Inv) : "") + " W";
                    if (nv.MemUsed >= 0 && nv.MemTotal > 0)
                        s.Vram = (nv.MemUsed / 1024).ToString("0.0", Txt.Inv) + " / " + (nv.MemTotal / 1024).ToString("0.0", Txt.Inv) + " GB";
                    if (rpm >= 0)
                        s.Fan = rpm == 0 ? "0 RPM (stop)" : rpm + " RPM";
                    else if (nv.FanPct >= 0)
                        s.Fan = nv.FanPct.ToString("0", Txt.Inv) + " %";
                }
                r.ByInstance[g.InstanceId] = s;
            }

            foreach (var a in adapters)
            {
                string name = a.Name;
                foreach (var g in gpus) if (g.VenId == a.Ven && g.DevId == a.Dev) { name = g.Name; break; }
                r.LuidNames[a.Luid] = name;
            }
            var pidLoad = PerfGpu.LastPidLoad;
            foreach (var path in appPaths) r.AppRunning[path] = RunningText(path, r, pidLoad);
            return r;
        }
    }

    // ------------------------------------------------------------------ Per-app GPU preference (same store as Windows Graphics settings)

    class AppEntry
    {
        public string Path;
        public string Name;
        public int Pref;
    }

    static class GpuPrefs
    {
        static string KeyPath
        {
            get { return "Software" + Txt.BS + "Microsoft" + Txt.BS + "DirectX" + Txt.BS + "UserGpuPreferences"; }
        }

        public static List<AppEntry> Load()
        {
            var list = new List<AppEntry>();
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (k == null) return list;
                foreach (string name in k.GetValueNames())
                {
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var e = new AppEntry();
                    e.Path = name;
                    e.Name = AppName(name);
                    e.Pref = ParsePref(Convert.ToString(k.GetValue(name)));
                    list.Add(e);
                }
            }
            list.Sort(delegate (AppEntry a, AppEntry b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        public static string AppName(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrEmpty(vi.FileDescription) && vi.FileDescription.Trim().Length > 0)
                        return vi.FileDescription.Trim();
                }
            }
            catch { }
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        public static int ParsePref(string data)
        {
            if (string.IsNullOrEmpty(data)) return 0;
            foreach (var part in data.Split(';'))
            {
                var t = part.Trim();
                if (!t.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)) continue;
                int v;
                if (int.TryParse(t.Substring("GpuPreference=".Length), NumberStyles.Integer, Txt.Inv, out v)) return v;
            }
            return 0;
        }

        // Adds the app with "Let Windows decide" if it is not in the list yet.
        public static void Add(string path)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (k.GetValue(path) == null) k.SetValue(path, "GpuPreference=0;", RegistryValueKind.String);
            }
        }

        // Rewrites GpuPreference=N; and keeps any other key=value; parts Windows stored.
        public static void Set(string path, int pref)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                string old = Convert.ToString(k.GetValue(path));
                var sb = new StringBuilder("GpuPreference=" + pref.ToString(Txt.Inv) + ";");
                foreach (var part in old.Split(';'))
                {
                    var t = part.Trim();
                    if (t.Length == 0 || t.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(t).Append(';');
                }
                k.SetValue(path, sb.ToString(), RegistryValueKind.String);
            }
        }

        public static void Remove(string path)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
            {
                if (k != null) k.DeleteValue(path, false);
            }
        }

        public static string PrefName(int pref)
        {
            if (pref == 1) return "Power saving";
            if (pref == 2) return "High performance";
            return "Let Windows decide";
        }
    }

    // ------------------------------------------------------------------ UI

    static class Ui
    {
        public static float Scale = 1f;
        public static int S(int v) { return (int)Math.Round(v * Scale); }
    }

    static class Theme
    {
        // Palette. Not readonly: SetDark swaps it and Retheme recolors the open windows.
        public static Color Header, HeaderSel, HeaderHover, HeaderMuted, Surface, Card, Border, Text, Muted,
            Accent, Green, Red, Orange, RowAlt, RowSel;
        public static bool Dark;
        public static string Mode = "system";   // "system", "light" or "dark" (saved in HKCU\Software\GPUToggle)
        const string SettingsKey = @"Software\GPUToggle";

        static Color[] Palette()
        {
            return new Color[] { Header, HeaderSel, HeaderHover, HeaderMuted, Surface, Card, Border, Text, Muted, Accent, Green, Red, Orange, RowAlt, RowSel };
        }

        public static void SetDark(bool dark)
        {
            Dark = dark;
            Green = Color.FromArgb(22, 163, 74);
            Red = Color.FromArgb(220, 38, 38);
            Orange = Color.FromArgb(234, 88, 12);
            HeaderMuted = Color.FromArgb(148, 163, 184);
            if (!dark)
            {
                Header = Color.FromArgb(15, 27, 51);
                HeaderSel = Color.FromArgb(34, 52, 92);
                HeaderHover = Color.FromArgb(26, 41, 74);
                Surface = Color.FromArgb(243, 246, 250);
                Card = Color.White;
                Border = Color.FromArgb(226, 232, 240);
                Text = Color.FromArgb(30, 41, 59);
                Muted = Color.FromArgb(100, 116, 139);
                Accent = Color.FromArgb(37, 99, 235);
                RowAlt = Color.FromArgb(248, 250, 252);
                RowSel = Color.FromArgb(219, 234, 254);
            }
            else
            {
                Header = Color.FromArgb(8, 13, 28);
                HeaderSel = Color.FromArgb(37, 52, 82);
                HeaderHover = Color.FromArgb(24, 34, 56);
                Surface = Color.FromArgb(17, 24, 39);
                Card = Color.FromArgb(30, 39, 56);
                Border = Color.FromArgb(55, 67, 89);
                Text = Color.FromArgb(226, 232, 240);
                Muted = Color.FromArgb(140, 154, 176);   // must differ from HeaderMuted (palette swap maps by value)
                Accent = Color.FromArgb(77, 140, 255);
                RowAlt = Color.FromArgb(36, 46, 65);
                RowSel = Color.FromArgb(32, 62, 116);
            }
        }

        public static bool SystemDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static bool WantDark { get { return Mode == "dark" || (Mode == "system" && SystemDark()); } }

        public static void LoadMode()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    string v = k != null ? k.GetValue("Theme") as string : null;
                    if (v == "light" || v == "dark" || v == "system") Mode = v;
                }
            }
            catch { }
            SetDark(WantDark);
        }

        public static void SaveMode()
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue("Theme", Mode, RegistryValueKind.String); } catch { }
        }

        // ---- applying the palette to windows that are already open

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Dark title bar (Windows 10 20H1+ / 11). Lists keep the default theme: the Explorer list theme
        // draws column divider lines through the owner-drawn rows.
        public static void Native(Control c)
        {
            if (!c.IsHandleCreated) return;
            try
            {
                if (c is Form)
                {
                    int on = Dark ? 1 : 0;
                    if (DwmSetWindowAttribute(c.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(c.Handle, 19, ref on, 4);
                    // Windows 11: in dark mode paint the caption like the header (overrides "accent color on
                    // title bars"); in light mode give it back to Windows (0xFFFFFFFF = DWMWA_COLOR_DEFAULT).
                    int cap = Dark ? ColorTranslator.ToWin32(Header) : -1, txt = Dark ? ColorTranslator.ToWin32(Color.White) : -1;
                    DwmSetWindowAttribute(c.Handle, 35, ref cap, 4);
                    DwmSetWindowAttribute(c.Handle, 36, ref txt, 4);
                }
            }
            catch { }
        }

        // Call once in every window's constructor: themes the title bar and the native controls.
        public static void Hook(Form f)
        {
            f.HandleCreated += delegate { Native(f); };
            f.Load += delegate { Walk(f, null); };
        }

        static void Walk(Control c, Dictionary<Color, Color> map)
        {
            var b = c as Button;
            Color nc;
            if (b != null && styles.ContainsKey(b)) StyleButton(b, map);
            else if (b == null && map != null)
            {
                if (map.TryGetValue(c.BackColor, out nc)) c.BackColor = nc;
                if (map.TryGetValue(c.ForeColor, out nc)) c.ForeColor = nc;
            }
            var cb = c as ComboBox;
            if (cb != null) { cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Card; cb.ForeColor = Text; }
            var nu = c as NumericUpDown;
            if (nu != null) { nu.BackColor = Card; nu.ForeColor = Text; nu.BorderStyle = BorderStyle.FixedSingle; }
            var tb = c as TextBox;
            if (tb != null) { tb.BackColor = Card; tb.ForeColor = Text; }
            if (c is ListView) { c.BackColor = Card; c.ForeColor = Text; }
            Native(c);
            foreach (Control ch in c.Controls) Walk(ch, map);
            c.Invalidate();
        }

        // Switches the palette and recolors every open window in place (no restart, recordings keep running).
        public static void Retheme(bool dark)
        {
            if (dark == Dark) return;
            Color[] old = Palette();
            SetDark(dark);
            Color[] now = Palette();
            var map = new Dictionary<Color, Color>();
            for (int i = 0; i < old.Length; i++) if (!map.ContainsKey(old[i])) map[old[i]] = now[i];
            if (Changed != null) Changed();
            foreach (Form f in Application.OpenForms) { Walk(f, map); Native(f); f.Refresh(); }
        }

        public static event Action Changed;

        public static Font Regular, Semibold, SmallBold, Small, Title;

        public static void Init()
        {
            Regular = new Font("Segoe UI", 9.5f);
            Semibold = new Font("Segoe UI Semibold", 9.5f);
            SmallBold = new Font("Segoe UI Semibold", 8.5f);
            Small = new Font("Segoe UI", 8.5f);
            Title = new Font("Segoe UI Semibold", 15f);
        }

        public static Color Darken(Color c, int amount)
        {
            return Color.FromArgb(c.A, Math.Max(0, c.R - amount), Math.Max(0, c.G - amount), Math.Max(0, c.B - amount));
        }

        public static GraphicsPath Round(Rectangle r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d < 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Dictionary<Button, object[]> styles = new Dictionary<Button, object[]>();   // button -> { color, primary }

        // Colors a MakeButton button from the current palette; map translates its stored color
        // (e.g. Accent) when the palette changes.
        static void StyleButton(Button b, Dictionary<Color, Color> map)
        {
            object[] st = styles[b];
            Color color = (Color)st[0], nc;
            if (map != null && map.TryGetValue(color, out nc)) { color = nc; st[0] = nc; }
            bool primary = (bool)st[1];
            Color bg = primary ? color : Card;
            Color fg = primary ? Color.White : Text;
            b.BackColor = b.Enabled ? bg : Border;
            b.ForeColor = b.Enabled ? fg : Muted;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = primary ? Darken(bg, 18) : (Dark ? RowAlt : Surface);
            b.FlatAppearance.MouseDownBackColor = primary ? Darken(bg, 34) : Border;
        }

        // Changes a MakeButton button's fill color (e.g. Start recording green <-> red).
        public static void SetButtonColor(Button b, Color color)
        {
            object[] st;
            if (!styles.TryGetValue(b, out st)) { b.BackColor = color; return; }
            st[0] = color;
            StyleButton(b, null);
        }

        // primary = filled colored button, otherwise white button with a border
        public static Button MakeButton(string text, Color color, bool primary)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Font = Semibold;
            b.Cursor = Cursors.Hand;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(Ui.S(92), Ui.S(34));
            b.Padding = new Padding(Ui.S(10), 0, Ui.S(10), 0);
            b.Margin = new Padding(0, 0, Ui.S(8), Ui.S(8));
            styles[b] = new object[] { color, primary };
            StyleButton(b, null);
            b.EnabledChanged += delegate { StyleButton(b, null); };
            b.Disposed += delegate { styles.Remove(b); };
            return b;
        }

        // 1px bordered white card around a control
        public static Panel CardAround(Control inner)
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = Border;
            p.Padding = new Padding(1);
            inner.Dock = DockStyle.Fill;
            p.Controls.Add(inner);
            return p;
        }

        // Section with a bold title + muted subtitle above a body control
        public static Panel Section(string title, string subtitle, Control body)
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = Surface;
            var head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = Ui.S(30);
            head.BackColor = Surface;
            head.Paint += delegate (object s, PaintEventArgs e)
            {
                var flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(e.Graphics, title, Semibold, new Point(0, Ui.S(5)), Text, flags);
                int w = TextRenderer.MeasureText(e.Graphics, title, Semibold, Size.Empty, flags).Width;
                TextRenderer.DrawText(e.Graphics, subtitle, Small, new Point(w + Ui.S(10), Ui.S(7)), Muted, flags);
            };
            body.Dock = DockStyle.Fill;
            p.Controls.Add(body);
            p.Controls.Add(head);
            return p;
        }

        public static FlowLayoutPanel Toolbar()
        {
            var f = new FlowLayoutPanel();
            f.Dock = DockStyle.Top;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = true;
            f.BackColor = Surface;
            f.Padding = new Padding(0, 0, 0, Ui.S(4));
            return f;
        }
    }

    // ListView with flat header, tall rows, alternating rows, status pills and load bars.
    class StyledList : ListView
    {
        public Dictionary<int, string> ColumnKinds = new Dictionary<int, string>();   // "pill" or "bar"
        public Func<string, Color[]> PillStyle;                                       // text -> { background, foreground }

        public StyledList()
        {
            DoubleBuffered = true;
            OwnerDraw = true;
            View = View.Details;
            FullRowSelect = true;
            MultiSelect = false;
            HideSelection = false;
            BorderStyle = BorderStyle.None;
            Font = Theme.Regular;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            var il = new ImageList();
            il.ImageSize = new Size(1, Ui.S(30));
            SmallImageList = il;
            DrawColumnHeader += OnHeader;
            DrawItem += delegate (object s, DrawListViewItemEventArgs e) { };
            DrawSubItem += OnSubItem;
            SelectedIndexChanged += delegate { Invalidate(); };
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            StretchLast();
        }

        public void StretchLast()
        {
            if (Columns.Count == 0) return;
            int used = 0;
            for (int i = 0; i < Columns.Count - 1; i++) used += Columns[i].Width;
            int w = ClientSize.Width - used;
            if (w > Ui.S(80)) Columns[Columns.Count - 1].Width = w;
        }

        void OnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var bg = new SolidBrush(Theme.RowAlt)) e.Graphics.FillRectangle(bg, e.Bounds);
            using (var pen = new Pen(Theme.Border))
            {
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + Ui.S(6), e.Bounds.Right - 1, e.Bounds.Bottom - Ui.S(6));
            }
            var r = new Rectangle(e.Bounds.X + Ui.S(10), e.Bounds.Y, Math.Max(0, e.Bounds.Width - Ui.S(14)), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), Theme.SmallBold, r, Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        void OnSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            var g = e.Graphics;
            var b = e.Bounds;
            if (e.ColumnIndex == 0 && Columns.Count > 0) b.Width = Columns[0].Width;
            Color bg = e.Item.Selected ? Theme.RowSel : (e.ItemIndex % 2 == 1 ? Theme.RowAlt : Theme.Card);
            using (var br = new SolidBrush(bg)) g.FillRectangle(br, b);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);

            string text = e.SubItem.Text ?? "";
            var inner = new Rectangle(b.X + Ui.S(10), b.Y, Math.Max(0, b.Width - Ui.S(14)), b.Height);
            string kind;
            ColumnKinds.TryGetValue(e.ColumnIndex, out kind);

            if (kind == "pill" && text.Length > 0 && text != Txt.NA) { DrawPill(g, inner, text); return; }
            float v;
            if (kind == "bar" && LeadingNumber(text, out v)) { DrawBar(g, inner, text, v); return; }

            Color fg = e.SubItem.ForeColor;
            if (fg.IsEmpty) fg = Theme.Text;
            Font f = e.ColumnIndex == 0 ? Theme.Semibold : Theme.Regular;
            TextRenderer.DrawText(g, text, f, inner, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        void DrawPill(Graphics g, Rectangle inner, string text)
        {
            Color[] c = PillStyle != null ? PillStyle(text) : new Color[] { Theme.Border, Theme.Text };
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            Size ts = TextRenderer.MeasureText(g, text, Theme.SmallBold, Size.Empty, flags);
            int h = Ui.S(22);
            int w = Math.Min(inner.Width, ts.Width + Ui.S(22));
            if (w <= 0) return;
            var pr = new Rectangle(inner.X, inner.Y + (inner.Height - h) / 2, w, h);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(pr, h / 2f))
            using (var br = new SolidBrush(c[0]))
                g.FillPath(br, path);
            g.SmoothingMode = old;
            TextRenderer.DrawText(g, text, Theme.SmallBold, pr, c[1],
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        void DrawBar(Graphics g, Rectangle inner, string text, float value)
        {
            int tw = Ui.S(46);
            TextRenderer.DrawText(g, text, Theme.Regular, new Rectangle(inner.X, inner.Y, tw, inner.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            int bx = inner.X + tw;
            int bw = inner.Right - bx - Ui.S(4);
            if (bw < Ui.S(12)) return;
            int bh = Ui.S(6);
            var track = new Rectangle(bx, inner.Y + (inner.Height - bh) / 2, bw, bh);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(track, bh / 2f))
            using (var br = new SolidBrush(Theme.Border))
                g.FillPath(br, path);
            int fw = (int)Math.Round(bw * Math.Max(0f, Math.Min(100f, value)) / 100f);
            if (fw > 0)
            {
                var fill = new Rectangle(track.X, track.Y, Math.Max(fw, bh), bh);
                using (var path = Theme.Round(fill, bh / 2f))
                using (var br = new SolidBrush(value >= 90 ? Theme.Orange : Theme.Accent))
                    g.FillPath(br, path);
            }
            g.SmoothingMode = old;
        }

        static bool LeadingNumber(string s, out float v)
        {
            v = 0;
            int i = 0;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            return i > 0 && float.TryParse(s.Substring(0, i), NumberStyles.Float, Txt.Inv, out v);
        }

        public static Color[] GpuStatusPill(string t)
        {
            if (Theme.Dark)
            {
                if (t == "Enabled") return new Color[] { Color.FromArgb(20, 83, 45), Color.FromArgb(134, 239, 172) };
                if (t == "Disabled") return new Color[] { Color.FromArgb(51, 65, 85), Color.FromArgb(148, 163, 184) };
                return new Color[] { Color.FromArgb(127, 29, 29), Color.FromArgb(252, 165, 165) };
            }
            if (t == "Enabled") return new Color[] { Color.FromArgb(220, 252, 231), Color.FromArgb(21, 128, 61) };
            if (t == "Disabled") return new Color[] { Color.FromArgb(241, 245, 249), Color.FromArgb(100, 116, 139) };
            return new Color[] { Color.FromArgb(254, 226, 226), Color.FromArgb(185, 28, 28) };
        }

        public static Color[] PrefPill(string t)
        {
            if (Theme.Dark)
            {
                if (t.StartsWith("High", StringComparison.Ordinal)) return new Color[] { Color.FromArgb(30, 58, 138), Color.FromArgb(147, 197, 253) };
                if (t.StartsWith("Power", StringComparison.Ordinal)) return new Color[] { Color.FromArgb(120, 53, 15), Color.FromArgb(253, 230, 138) };
                return new Color[] { Color.FromArgb(51, 65, 85), Color.FromArgb(203, 213, 225) };
            }
            if (t.StartsWith("High", StringComparison.Ordinal)) return new Color[] { Color.FromArgb(219, 234, 254), Color.FromArgb(29, 78, 216) };
            if (t.StartsWith("Power", StringComparison.Ordinal)) return new Color[] { Color.FromArgb(254, 243, 199), Color.FromArgb(180, 83, 9) };
            return new Color[] { Color.FromArgb(241, 245, 249), Color.FromArgb(71, 85, 105) };
        }
    }

    // Real-time line graph of the last N seconds of PCIe traffic, as % of the link capacity.
    class PcieGraph : Control
    {
        public PcieMonitor Mon;
        public int WindowSeconds = 120;
        public string Message;
        static Color RxColor { get { return Theme.Accent; } }
        static readonly Color TxColor = Color.FromArgb(234, 88, 12);

        public PcieGraph()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Theme.Card;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Card);
            var tf = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            if (Message != null || Mon == null)
            {
                TextRenderer.DrawText(g, Message ?? "", Theme.Regular, new Rectangle(Ui.S(20), 0, Width - Ui.S(40), Height), Theme.Muted,
                    TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            var plot = new Rectangle(Ui.S(48), Ui.S(30), Width - Ui.S(64), Height - Ui.S(56));
            if (plot.Width < 40 || plot.Height < 30) return;

            List<PcieSample> pts = new List<PcieSample>();
            double capRx, capTx, thr;
            lock (Mon.Lock)
            {
                capRx = Mon.CapRx; capTx = Mon.CapTx; thr = Mon.ThresholdPct;
                DateTime from = DateTime.Now.AddSeconds(-WindowSeconds - 1);
                for (int i = Mon.Samples.Count - 1; i >= 0 && Mon.Samples[i].Time >= from; i--) pts.Add(Mon.Samples[i]);
            }
            pts.Reverse();

            double top = 100;
            foreach (var p in pts) top = Math.Max(top, Math.Max(Pct(p.Rx, capRx), Pct(p.Tx, capTx)));
            top = Math.Ceiling(top / 25) * 25;

            // Bottleneck shading
            DateTime now = DateTime.Now;
            using (var sat = new SolidBrush(Color.FromArgb(40, 220, 38, 38)))
                foreach (var p in pts)
                {
                    if (!p.Saturated) continue;
                    float x1 = X(plot, now, p.Time.AddSeconds(-p.Dt)), x2 = X(plot, now, p.Time);
                    g.FillRectangle(sat, x1, plot.Top, Math.Max(1f, x2 - x1), plot.Height);
                }

            // Grid + axis labels
            using (var grid = new Pen(Theme.Border))
                for (int v = 0; v <= top; v += 25)
                {
                    float y = Y(plot, v, top);
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    TextRenderer.DrawText(g, v + " %", Theme.Small, new Rectangle(0, (int)y - Ui.S(8), plot.Left - Ui.S(6), Ui.S(16)), Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
            for (int sec = 0; sec <= WindowSeconds; sec += 30)
            {
                float x = plot.Right - (float)sec / WindowSeconds * plot.Width;
                TextRenderer.DrawText(g, sec == 0 ? "now" : "-" + sec + " s", Theme.Small, new Point((int)x - Ui.S(14), plot.Bottom + Ui.S(6)), Theme.Muted, tf);
            }
            using (var th = new Pen(Theme.Red, 1f))
            {
                th.DashStyle = DashStyle.Dash;
                float y = Y(plot, thr, top);
                g.DrawLine(th, plot.Left, y, plot.Right, y);
                TextRenderer.DrawText(g, (Mon.Measured ? "measured limit / bottleneck " : "bottleneck ") + thr.ToString("0", Txt.Inv) + " %", Theme.Small, new Point(plot.Left + Ui.S(4), (int)y - Ui.S(16)), Theme.Red, tf);
            }

            // Lines
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawLine(g, plot, now, top, pts, capRx, true, RxColor);
            DrawLine(g, plot, now, top, pts, capTx, false, TxColor);
            g.SmoothingMode = SmoothingMode.Default;

            // Legend
            int lx = plot.Left;
            lx = Legend(g, lx, "Host -> GPU (upload)", RxColor, tf);
            Legend(g, lx + Ui.S(18), "GPU -> Host (download)", TxColor, tf);
        }

        int Legend(Graphics g, int x, string text, Color c, TextFormatFlags tf)
        {
            using (var b = new SolidBrush(c)) g.FillRectangle(b, x, Ui.S(10), Ui.S(14), Ui.S(4));
            TextRenderer.DrawText(g, text, Theme.Small, new Point(x + Ui.S(20), Ui.S(4)), Theme.Text, tf);
            return x + Ui.S(20) + TextRenderer.MeasureText(text, Theme.Small).Width;
        }

        void DrawLine(Graphics g, Rectangle plot, DateTime now, double top, List<PcieSample> pts, double cap, bool rx, Color c)
        {
            if (pts.Count < 2) return;
            var arr = new PointF[pts.Count];
            for (int i = 0; i < pts.Count; i++)
                arr[i] = new PointF(X(plot, now, pts[i].Time), Y(plot, Pct(rx ? pts[i].Rx : pts[i].Tx, cap), top));
            using (var pen = new Pen(c, Ui.S(2))) g.DrawLines(pen, arr);
        }

        static double Pct(double v, double cap) { return cap > 0 ? v / cap * 100 : 0; }
        float X(Rectangle plot, DateTime now, DateTime t)
        {
            double age = (now - t).TotalSeconds;
            return (float)Math.Max(plot.Left, plot.Right - age / WindowSeconds * plot.Width);
        }
        static float Y(Rectangle plot, double pct, double top) { return (float)(plot.Bottom - Math.Min(pct, top) / top * plot.Height); }
    }

    // Small white card with a caption, a big value and a muted detail line.
    class StatCard : Control
    {
        public string Caption = "", Value = Txt.NA, Detail = "";
        public Color ValueColor = Theme.Text;

        public StatCard()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            Margin = new Padding(0, 0, Ui.S(10), 0);
        }

        public void Set(string value, string detail, Color color)
        {
            if (value == Value && detail == Detail && color == ValueColor) return;
            Value = value; Detail = detail; ValueColor = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Card);
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            var tf = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int x = Ui.S(12), w = Width - Ui.S(20);
            TextRenderer.DrawText(g, Caption.ToUpperInvariant(), Theme.SmallBold, new Rectangle(x, Ui.S(8), w, Ui.S(18)), Theme.Muted, tf);
            TextRenderer.DrawText(g, Value, Theme.Title, new Rectangle(x, Ui.S(26), w, Ui.S(30)), ValueColor, tf);
            int y = Ui.S(58), lh = TextRenderer.MeasureText("Ag", Theme.Small).Height;
            foreach (var line in (Detail ?? "").Split('\n'))
            {
                if (y + lh > Height - Ui.S(2)) break;
                TextRenderer.DrawText(g, line.TrimEnd('\r'), Theme.Small, new Rectangle(x, y, w, lh), Theme.Muted, tf);
                y += lh;
            }
        }
    }

    class MainForm : Form
    {
        const int RevertSeconds = 15;
        const int WM_DEVICECHANGE = 0x0219;
        // GPU list columns
        const int C_STATUS = 2, C_LOAD = 3, C_TEMP = 4, C_POWER = 5, C_FAN = 6, C_VRAM = 7, C_DISP = 8;
        // Apps list columns
        const int A_PREF = 1, A_RUN = 2;

        StyledList list, dispList, appsList;
        Button btnEnable, btnDisable, btnRefresh, btnPcie;
        Button btnAddApp, btnAddRunning, btnRemoveApp, btnLaunch, btnGfxSettings, btnPrefAuto, btnPrefSave, btnPrefPerf;
        Button navGpus, navApps, navMon, navTheme;
        int curPage;
        Panel gpuPage, appsPage, monitorPage, header;
        // PCIe monitor page
        PcieMonitor mon = new PcieMonitor();
        PcieGraph graph;
        StatCard cardLimit;
        StatCard cardNow, cardAvg, cardPeak, cardSat;
        StyledList epList;
        ComboBox cmbMonGpu;
        NumericUpDown numThr;
        Label lblThrPct;
        Button btnMonStart, btnMonReset, btnMonMeasure, btnMonExport;
        Label lblMonInfo;
        List<GpuInfo> monGpus = new List<GpuInfo>();
        int shownEpisodes;
        System.Windows.Forms.Timer monTimer;
        CheckBox chkAll;
        Label lblStatus, lblPrefInfo;
        System.Windows.Forms.Timer debounce, statsTimer;
        List<GpuInfo> gpus = new List<GpuInfo>();
        bool busy, statsBusy;
        string lastDispSig = "";
        string savingGpu, perfGpu;
        Bitmap logo;

        public MainForm()
        {
            Text = "GPU Toggle";
            Font = Theme.Regular;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Theme.Hook(this);
            ClientSize = new Size(Ui.S(1120), Ui.S(680));
            MinimumSize = new Size(Ui.S(960), Ui.S(560));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            logo = LoadLogo(Ui.S(40));

            BuildHeader();
            BuildGpuPage();
            BuildAppsPage();
            BuildMonitorPage();

            var content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Theme.Surface;
            content.Padding = new Padding(Ui.S(16), Ui.S(14), Ui.S(16), Ui.S(10));
            content.Controls.Add(gpuPage);
            content.Controls.Add(appsPage);
            content.Controls.Add(monitorPage);

            var statusBar = new Panel();
            statusBar.Dock = DockStyle.Bottom;
            statusBar.Height = Ui.S(30);
            statusBar.BackColor = Theme.Card;
            statusBar.Paint += delegate (object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, statusBar.Width, 0);
            };
            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.Padding = new Padding(Ui.S(14), 0, 0, 0);
            lblStatus.ForeColor = Theme.Muted;
            lblStatus.Font = Theme.Small;
            statusBar.Controls.Add(lblStatus);

            // Docking: the last control added is docked first (outermost).
            Controls.Add(content);
            Controls.Add(statusBar);
            Controls.Add(header);

            debounce = new System.Windows.Forms.Timer();
            debounce.Interval = 800;
            debounce.Tick += delegate
            {
                debounce.Stop();
                if (busy) return;
                RefreshList();
                if (appsPage.Visible) RefreshApps();
            };

            statsTimer = new System.Windows.Forms.Timer();
            statsTimer.Interval = 2000;
            statsTimer.Tick += delegate { UpdateStats(); };

            Shown += delegate
            {
                ShowPage(0);
                RefreshList();
                RefreshApps();
                UpdateStats();
                statsTimer.Start();
            };
            monTimer = new System.Windows.Forms.Timer();
            monTimer.Interval = 500;
            monTimer.Tick += delegate { UpdateMonitorUi(); };
            monTimer.Start();

            FormClosing += delegate { statsTimer.Stop(); monTimer.Stop(); mon.Stop(); };
        }

        // Reads the largest frame of the embedded app.ico. PNG frames are decoded directly,
        // because Icon.ToBitmap does not decode them reliably.
        static Bitmap LoadLogo(int size)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                {
                    if (s != null)
                    {
                        var ms = new MemoryStream();
                        s.CopyTo(ms);
                        byte[] ico = ms.ToArray();
                        int count = BitConverter.ToUInt16(ico, 4);
                        int bestSize = -1, bestOffset = 0, bestLength = 0;
                        for (int i = 0; i < count; i++)
                        {
                            int e = 6 + i * 16;
                            int w = ico[e] == 0 ? 256 : ico[e];
                            if (w > bestSize)
                            {
                                bestSize = w;
                                bestLength = BitConverter.ToInt32(ico, e + 8);
                                bestOffset = BitConverter.ToInt32(ico, e + 12);
                            }
                        }
                        if (bestSize > 0 && ico[bestOffset] == 0x89)
                        {
                            using (var img = Image.FromStream(new MemoryStream(ico, bestOffset, bestLength)))
                                return new Bitmap(img);
                        }
                        ms.Position = 0;
                        using (var ic = new Icon(ms, size, size)) return ic.ToBitmap();
                    }
                }
            }
            catch { }
            try
            {
                using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath)) return ic.ToBitmap();
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- layout

        void BuildHeader()
        {
            header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = Ui.S(72);
            header.BackColor = Theme.Header;
            header.Paint += delegate (object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                int x = Ui.S(18);
                if (logo != null)
                {
                    int sz = Ui.S(40);
                    g.DrawImage(logo, x, (header.Height - sz) / 2, sz, sz);
                    x += sz + Ui.S(12);
                }
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                TextRenderer.DrawText(g, "GPU Toggle", Theme.Title, new Point(x, Ui.S(12)), Color.White, flags);
                TextRenderer.DrawText(g, "Enable / disable GPUs for eGPU setups and choose which GPU each game uses",
                    Theme.Small, new Point(x + Ui.S(1), Ui.S(44)), Theme.HeaderMuted, flags);
            };

            var nav = new FlowLayoutPanel();
            nav.Dock = DockStyle.Right;
            nav.AutoSize = true;
            nav.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            nav.WrapContents = false;
            nav.BackColor = Theme.Header;
            nav.Padding = new Padding(0, Ui.S(18), Ui.S(14), 0);
            navGpus = MakeNav("GPUs");
            navApps = MakeNav("Apps / Games");
            navMon = MakeNav("PCIe monitor");
            navGpus.Click += delegate { ShowPage(0); };
            navApps.Click += delegate { ShowPage(1); };
            navMon.Click += delegate { ShowPage(2); };
            navTheme = MakeNav("");
            navTheme.Width = Ui.S(104);
            navTheme.Margin = new Padding(Ui.S(14), 0, 0, 0);
            navTheme.Click += delegate
            {
                // Auto (follow Windows) -> Light -> Dark -> Auto
                Theme.Mode = Theme.Mode == "system" ? "light" : (Theme.Mode == "light" ? "dark" : "system");
                Theme.SaveMode();
                Theme.Retheme(Theme.WantDark);
                StyleNav();
            };
            nav.Controls.Add(navGpus);
            nav.Controls.Add(navApps);
            nav.Controls.Add(navMon);
            nav.Controls.Add(navTheme);
            header.Controls.Add(nav);

            Theme.Changed += delegate { StyleNav(); };
            UserPreferenceChangedEventHandler upc = delegate (object s, UserPreferenceChangedEventArgs e)
            {
                if (Theme.Mode == "system" && !IsDisposed)
                    BeginInvoke((MethodInvoker)delegate { Theme.Retheme(Theme.WantDark); StyleNav(); });
            };
            SystemEvents.UserPreferenceChanged += upc;
            FormClosed += delegate { SystemEvents.UserPreferenceChanged -= upc; };
        }

        Button MakeNav(string text)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.HeaderHover;
            b.FlatAppearance.MouseDownBackColor = Theme.HeaderSel;
            b.BackColor = Theme.Header;
            b.ForeColor = Theme.HeaderMuted;
            b.Font = Theme.Semibold;
            b.Cursor = Cursors.Hand;
            b.Size = new Size(Ui.S(124), Ui.S(36));
            b.Margin = new Padding(Ui.S(4), 0, 0, 0);
            return b;
        }

        // Header buttons: selected page highlighted; theme button shows the current mode.
        void StyleNav()
        {
            Button[] navs = { navGpus, navApps, navMon, navTheme };
            for (int i = 0; i < navs.Length; i++)
            {
                bool sel = i == curPage;
                navs[i].BackColor = sel ? Theme.HeaderSel : Theme.Header;
                navs[i].ForeColor = sel ? Color.White : Theme.HeaderMuted;
                navs[i].FlatAppearance.MouseOverBackColor = Theme.HeaderHover;
                navs[i].FlatAppearance.MouseDownBackColor = Theme.HeaderSel;
            }
            navTheme.Text = Theme.Mode == "system" ? "Theme: Auto" : (Theme.Mode == "dark" ? "Theme: Dark" : "Theme: Light");
            if (navTheme.Parent != null) navTheme.Parent.BackColor = Theme.Header;
            header.BackColor = Theme.Header;
            header.Invalidate();
        }

        void ShowPage(int page)
        {
            curPage = page;
            gpuPage.Visible = page == 0;
            appsPage.Visible = page == 1;
            monitorPage.Visible = page == 2;
            StyleNav();
            if (page == 1) RefreshApps();
            else if (page == 2) SetStatus("Start recording, then play your game. NVIDIA GPUs only (NVML). Shaded areas = PCIe bottleneck.");
            else SetStatus(list.Items.Count + " GPU(s) found. Double-click a row to toggle. Stats refresh every 2 s.");
        }

        void BuildGpuPage()
        {
            gpuPage = new Panel();
            gpuPage.Dock = DockStyle.Fill;
            gpuPage.BackColor = Theme.Surface;

            var bar = Theme.Toolbar();
            btnEnable = Theme.MakeButton("Enable", Theme.Green, true);
            btnDisable = Theme.MakeButton("Disable", Theme.Red, true);
            btnRefresh = Theme.MakeButton("Refresh", Theme.Card, false);
            btnRefresh.Click += delegate { RefreshList(); };
            btnDisable.Click += delegate { DisableSelected(); };
            btnEnable.Click += delegate { EnableSelected(); };
            btnPcie = Theme.MakeButton("PCIe bandwidth...", Theme.Card, false);
            btnPcie.Click += delegate { TestBandwidth(); };

            chkAll = new CheckBox();
            chkAll.Text = "Show virtual adapters";
            chkAll.AutoSize = true;
            chkAll.ForeColor = Theme.Muted;
            chkAll.Margin = new Padding(Ui.S(8), Ui.S(8), 0, 0);
            chkAll.CheckedChanged += delegate { RefreshList(); };

            bar.Controls.Add(btnEnable);
            bar.Controls.Add(btnDisable);
            bar.Controls.Add(btnRefresh);
            bar.Controls.Add(btnPcie);
            bar.Controls.Add(chkAll);

            list = new StyledList();
            list.Columns.Add("GPU", Ui.S(220));
            list.Columns.Add("Vendor", Ui.S(72));
            list.Columns.Add("Status", Ui.S(96));
            list.Columns.Add("Load", Ui.S(120));
            list.Columns.Add("Temp", Ui.S(62));
            list.Columns.Add("Power / TDP", Ui.S(110));
            list.Columns.Add("Fan", Ui.S(96));
            list.Columns.Add("VRAM", Ui.S(112));
            list.Columns.Add("Displays", Ui.S(78));
            list.Columns.Add("Instance ID", Ui.S(300));
            list.ColumnKinds[C_STATUS] = "pill";
            list.ColumnKinds[C_LOAD] = "bar";
            list.PillStyle = StyledList.GpuStatusPill;
            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            list.DoubleClick += delegate { ToggleSelected(); };

            dispList = new StyledList();
            dispList.Columns.Add("Output", Ui.S(160));
            dispList.Columns.Add("Monitor", Ui.S(240));
            dispList.Columns.Add("Mode", Ui.S(200));
            dispList.Columns.Add("Driven by GPU", Ui.S(300));

            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.BackColor = Theme.Surface;
            split.SplitterWidth = Ui.S(14);
            split.Panel1.Controls.Add(Theme.Section("Graphics adapters", "Double-click a row to enable / disable it", Theme.CardAround(list)));
            split.Panel2.Controls.Add(Theme.Section("Displays", "Which monitor is driven by which GPU", Theme.CardAround(dispList)));
            gpuPage.Resize += delegate
            {
                if (split.Height > Ui.S(200)) split.SplitterDistance = (int)(split.Height * 0.55);
            };

            gpuPage.Controls.Add(split);
            gpuPage.Controls.Add(bar);
        }

        void BuildAppsPage()
        {
            appsPage = new Panel();
            appsPage.Dock = DockStyle.Fill;
            appsPage.BackColor = Theme.Surface;
            appsPage.Visible = false;

            // Info card: which GPU Windows uses for each preference
            var info = new Panel();
            info.Dock = DockStyle.Top;
            info.Height = Ui.S(84);
            info.BackColor = Theme.Card;
            info.Padding = new Padding(Ui.S(16), Ui.S(10), Ui.S(16), Ui.S(10));
            info.Paint += delegate (object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, info.Width - 1, info.Height - 1);
            };
            lblPrefInfo = new Label();
            lblPrefInfo.Dock = DockStyle.Fill;
            lblPrefInfo.ForeColor = Theme.Text;
            lblPrefInfo.BackColor = Theme.Card;
            info.Controls.Add(lblPrefInfo);

            var gap = new Panel();
            gap.Dock = DockStyle.Top;
            gap.Height = Ui.S(12);
            gap.BackColor = Theme.Surface;

            var row1 = Theme.Toolbar();
            btnAddApp = Theme.MakeButton("Add app...", Theme.Accent, true);
            btnAddRunning = Theme.MakeButton("Add running app...", Theme.Card, false);
            btnRemoveApp = Theme.MakeButton("Remove", Theme.Card, false);
            btnLaunch = Theme.MakeButton("Launch to test", Theme.Green, true);
            btnGfxSettings = Theme.MakeButton("Windows Graphics settings", Theme.Card, false);
            btnAddApp.Click += delegate { AddAppFromFile(); };
            btnAddRunning.Click += delegate { AddRunningApp(); };
            btnRemoveApp.Click += delegate { RemoveSelectedApps(); };
            btnLaunch.Click += delegate { LaunchSelectedApp(); };
            btnGfxSettings.Click += delegate
            {
                try { Process.Start("ms-settings:display-advancedgraphics"); }
                catch (Exception ex) { SetStatus("Could not open Windows settings: " + ex.Message); }
            };
            row1.Controls.Add(btnAddApp);
            row1.Controls.Add(btnAddRunning);
            row1.Controls.Add(btnRemoveApp);
            row1.Controls.Add(btnLaunch);
            row1.Controls.Add(btnGfxSettings);

            var row2 = Theme.Toolbar();
            var lblRun = new Label();
            lblRun.Text = "Run selected app on:";
            lblRun.AutoSize = true;
            lblRun.ForeColor = Theme.Muted;
            lblRun.Font = Theme.Semibold;
            lblRun.Margin = new Padding(0, Ui.S(8), Ui.S(10), 0);
            btnPrefAuto = Theme.MakeButton("Let Windows decide", Theme.Card, false);
            btnPrefSave = Theme.MakeButton("Power saving", Theme.Card, false);
            btnPrefPerf = Theme.MakeButton("High performance", Theme.Card, false);
            btnPrefAuto.Click += delegate { SetSelectedPref(0); };
            btnPrefSave.Click += delegate { SetSelectedPref(1); };
            btnPrefPerf.Click += delegate { SetSelectedPref(2); };
            row2.Controls.Add(lblRun);
            row2.Controls.Add(btnPrefAuto);
            row2.Controls.Add(btnPrefSave);
            row2.Controls.Add(btnPrefPerf);

            appsList = new StyledList();
            appsList.MultiSelect = true;
            appsList.Columns.Add("App", Ui.S(240));
            appsList.Columns.Add("GPU preference", Ui.S(300));
            appsList.Columns.Add("Running on", Ui.S(280));
            appsList.Columns.Add("Path", Ui.S(400));
            appsList.ColumnKinds[A_PREF] = "pill";
            appsList.PillStyle = StyledList.PrefPill;
            appsList.SelectedIndexChanged += delegate { UpdateAppButtons(); };
            appsList.DoubleClick += delegate { LaunchSelectedApp(); };
            appsList.AllowDrop = true;
            appsList.DragEnter += delegate (object s, DragEventArgs e)
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            appsList.DragDrop += delegate (object s, DragEventArgs e)
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) AddApps(files);
            };

            var section = Theme.Section("Apps & games", "Drop .exe files here. Double-click to launch. Stored in Windows Graphics settings.",
                Theme.CardAround(appsList));

            appsPage.Controls.Add(section);
            appsPage.Controls.Add(row2);
            appsPage.Controls.Add(row1);
            appsPage.Controls.Add(gap);
            appsPage.Controls.Add(info);
        }

        void BuildMonitorPage()
        {
            monitorPage = new Panel();
            monitorPage.Dock = DockStyle.Fill;
            monitorPage.BackColor = Theme.Surface;
            monitorPage.Visible = false;

            var bar = Theme.Toolbar();
            var lblGpu = new Label();
            lblGpu.Text = "GPU:";
            lblGpu.AutoSize = true;
            lblGpu.ForeColor = Theme.Muted;
            lblGpu.Font = Theme.Semibold;
            lblGpu.Margin = new Padding(0, Ui.S(8), Ui.S(6), 0);
            cmbMonGpu = new ComboBox();
            cmbMonGpu.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMonGpu.Width = Ui.S(250);
            cmbMonGpu.Margin = new Padding(0, Ui.S(5), Ui.S(10), 0);
            cmbMonGpu.SelectedIndexChanged += delegate { AttachMonitor(); };

            btnMonStart = Theme.MakeButton("Start recording", Theme.Green, true);
            btnMonReset = Theme.MakeButton("Reset", Theme.Card, false);
            btnMonMeasure = Theme.MakeButton("Measure limit...", Theme.Card, false);
            btnMonExport = Theme.MakeButton("Export CSV...", Theme.Card, false);
            btnMonStart.Click += delegate { ToggleMonitor(); };
            btnMonReset.Click += delegate
            {
                mon.Reset();
                shownEpisodes = 0;
                epList.Items.Clear();
                UpdateMonitorUi();
            };
            btnMonMeasure.Click += delegate { if (mon.Gpu != null) RunBandwidthTest(mon.Gpu); };
            btnMonExport.Click += delegate { ExportMonitor(); };

            var lblThr = new Label();
            lblThr.Text = "Bottleneck at";
            lblThr.AutoSize = true;
            lblThr.ForeColor = Theme.Muted;
            lblThr.Margin = new Padding(Ui.S(8), Ui.S(8), Ui.S(6), 0);
            numThr = new NumericUpDown();
            numThr.Minimum = 5; numThr.Maximum = 100; numThr.Value = 80; numThr.Increment = 5;
            numThr.Width = Ui.S(56);
            numThr.Margin = new Padding(0, Ui.S(5), Ui.S(4), 0);
            numThr.ValueChanged += delegate { mon.ThresholdPct = (double)numThr.Value; graph.Invalidate(); };
            var lblPct = lblThrPct = new Label();
            lblPct.Text = "% of link";
            lblPct.AutoSize = true;
            lblPct.ForeColor = Theme.Muted;
            lblPct.Margin = new Padding(0, Ui.S(8), 0, 0);

            bar.Controls.Add(lblGpu);
            bar.Controls.Add(cmbMonGpu);
            bar.Controls.Add(btnMonStart);
            bar.Controls.Add(btnMonReset);
            bar.Controls.Add(btnMonMeasure);
            bar.Controls.Add(btnMonExport);
            bar.Controls.Add(lblThr);
            bar.Controls.Add(numThr);
            bar.Controls.Add(lblPct);

            lblMonInfo = new Label();
            lblMonInfo.Dock = DockStyle.Top;
            lblMonInfo.Height = Ui.S(24);
            lblMonInfo.ForeColor = Theme.Muted;
            lblMonInfo.Font = Theme.Small;
            lblMonInfo.AutoEllipsis = true;

            var cards = new TableLayoutPanel();
            cards.Dock = DockStyle.Top;
            cards.Height = Ui.S(110);
            cards.ColumnCount = 5;
            cards.RowCount = 1;
            cards.BackColor = Theme.Surface;
            for (int i = 0; i < 5; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, i == 0 ? 28f : 18f));
            cardLimit = new StatCard(); cardLimit.Caption = "Link limit";
            cardNow = new StatCard(); cardNow.Caption = "Now";
            cardAvg = new StatCard(); cardAvg.Caption = "Average";
            cardPeak = new StatCard(); cardPeak.Caption = "Peak";
            cardSat = new StatCard(); cardSat.Caption = "Bottleneck time";
            StatCard[] all = { cardLimit, cardNow, cardAvg, cardPeak, cardSat };
            for (int i = 0; i < all.Length; i++)
            {
                all[i].Dock = DockStyle.Fill;
                all[i].Margin = new Padding(i == 0 ? 0 : Ui.S(5), 0, i == all.Length - 1 ? 0 : Ui.S(5), 0);
                cards.Controls.Add(all[i], i, 0);
            }

            var gap = new Panel();
            gap.Dock = DockStyle.Top;
            gap.Height = Ui.S(10);
            gap.BackColor = Theme.Surface;

            graph = new PcieGraph();
            graph.Mon = mon;

            epList = new StyledList();
            epList.Columns.Add("Start", Ui.S(110));
            epList.Columns.Add("Duration", Ui.S(90));
            epList.Columns.Add("Direction", Ui.S(130));
            epList.Columns.Add("Peak", Ui.S(80));
            epList.Columns.Add("App in foreground", Ui.S(240));

            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.BackColor = Theme.Surface;
            split.SplitterWidth = Ui.S(14);
            split.Panel1.Controls.Add(Theme.Section("Live PCIe traffic", "Last 2 minutes, % of the theoretical link. Dashed line = measured limit, red areas = bottleneck.", Theme.CardAround(graph)));
            split.Panel2.Controls.Add(Theme.Section("Bottleneck log", "Every stretch of at least " +
                PcieMonitor.MinEpisodeSeconds.ToString("0.0", Txt.Inv) + " s above the threshold, newest first", Theme.CardAround(epList)));
            monitorPage.Resize += delegate
            {
                if (split.Height > Ui.S(200)) split.SplitterDistance = (int)(split.Height * 0.62);
            };

            monitorPage.Controls.Add(split);
            monitorPage.Controls.Add(gap);
            monitorPage.Controls.Add(cards);
            monitorPage.Controls.Add(lblMonInfo);
            monitorPage.Controls.Add(bar);
        }

        // Keeps the GPU combo in sync with the GPU list; NVIDIA GPUs first.
        void RefreshMonGpus()
        {
            if (cmbMonGpu == null) return;
            string keep = mon.Gpu != null ? mon.Gpu.InstanceId : null;
            var l = new List<GpuInfo>();
            foreach (var g in gpus) if (g.IsPhysical && g.Enabled && g.VenId == 0x10DE) l.Add(g);
            foreach (var g in gpus) if (g.IsPhysical && g.Enabled && g.VenId != 0x10DE) l.Add(g);
            var ids = new StringBuilder();
            foreach (var g in l) ids.Append(g.InstanceId).Append('|');
            var old = new StringBuilder();
            foreach (var g in monGpus) old.Append(g.InstanceId).Append('|');
            if (ids.ToString() == old.ToString()) return;

            monGpus = l;
            cmbMonGpu.Items.Clear();
            int sel = -1;
            for (int i = 0; i < l.Count; i++)
            {
                cmbMonGpu.Items.Add(l[i].Name);
                if (l[i].InstanceId == keep) sel = i;
            }
            if (sel < 0 && l.Count > 0) sel = 0;
            if (sel >= 0) cmbMonGpu.SelectedIndex = sel;   // fires AttachMonitor
            else AttachMonitor();
        }

        void AttachMonitor()
        {
            int i = cmbMonGpu.SelectedIndex;
            GpuInfo g = i >= 0 && i < monGpus.Count ? monGpus[i] : null;
            if (g != null && mon.Gpu != null && g.InstanceId == mon.Gpu.InstanceId && mon.Error == null) return;
            bool wasRunning = mon.Running;
            mon.Reset();
            shownEpisodes = 0;
            epList.Items.Clear();
            bool ok = mon.Attach(g);
            SyncThreshold();
            if (ok && wasRunning) mon.Start();
            UpdateMonitorUi();
        }

        void ToggleMonitor()
        {
            if (mon.Running) mon.Stop();
            else
            {
                if (mon.Gpu == null || mon.Error != null) AttachMonitor();
                if (mon.Error != null) { UpdateMonitorUi(); return; }
                // Bottlenecks are judged against what the link really delivers, so a measurement is required.
                if (!mon.Measured)
                {
                    if (MessageBox.Show(this,
                        "The real PCIe bandwidth of " + mon.Gpu.Name + " has not been measured yet. Real transfers never reach the theoretical link " +
                        "(an eGPU behind a USB4 / Thunderbolt tunnel may only get half of it), so the bottleneck threshold has to come from a measurement." + Txt.NL + Txt.NL +
                        "The bandwidth test runs now (a few seconds), the threshold is lowered to the measured % of the link, and recording starts right after it.",
                        "Measure PCIe bandwidth first", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                        return;
                    RunBandwidthTest(mon.Gpu, true);
                    if (!mon.Measured) { SetStatus("Recording not started: the bandwidth was not measured."); return; }
                }
                mon.Start();
            }
            UpdateMonitorUi();
        }

        // Shows the threshold the monitor picked for its current 100 % reference.
        void SyncThreshold()
        {
            decimal v = (decimal)Math.Max((double)numThr.Minimum, Math.Min((double)numThr.Maximum, mon.ThresholdPct));
            if (numThr.Value != v) numThr.Value = v;
            lblThrPct.Text = "% of link";
            graph.Invalidate();
        }

        // Pauses recording while the bandwidth test runs (it would show up as a bottleneck).
        void RunBandwidthTest(GpuInfo g, bool autoRun = false)
        {
            bool resume = mon.Running;
            if (resume) mon.Stop();
            using (var dlg = new BandwidthDialog(g, autoRun)) dlg.ShowDialog(this);
            if (mon.Gpu != null && mon.Gpu.InstanceId == g.InstanceId) { mon.UpdateCapacity(); SyncThreshold(); }
            if (resume) mon.Start();
            UpdateMonitorUi();
        }

        // "Theoretical Gen4 x4 = 1.97 x 4 = 7.88 GB/s": per-lane speed of the current PCIe gen x lanes in use.
        string TheoText(double cap)
        {
            double lane = PcieLink.LaneGBs(mon.CapGen);
            if (lane > 0 && mon.CapLanes > 0)
                return "Theoretical " + PcieLink.Gen(mon.CapGen) + " x" + mon.CapLanes + ": " + lane.ToString("0.00", Txt.Inv) + " x " + mon.CapLanes + " = " + Gbs(cap) + " GB/s";
            return "Theoretical: " + Gbs(cap) + " GB/s";
        }

        static string Gbs(double bytesPerSec) { return (bytesPerSec / 1e9).ToString("0.00", Txt.Inv); }
        static string Pct(double v, double cap) { return (cap > 0 ? v / cap * 100 : 0).ToString("0", Txt.Inv) + " %"; }
        static string Dur(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.ToString("mm':'ss", Txt.Inv) : t.ToString("mm':'ss", Txt.Inv);
        }

        void UpdateMonitorUi()
        {
            if (monitorPage == null || IsDisposed) return;
            bool ok = mon.Error == null && mon.Gpu != null;
            graph.Message = mon.Gpu == null ? "No enabled GPU found." : mon.Error;
            btnMonStart.Text = mon.Running ? "Stop recording" : "Start recording";
            Theme.SetButtonColor(btnMonStart, mon.Running ? Theme.Red : Theme.Green);
            btnMonStart.Enabled = ok;
            btnMonMeasure.Enabled = mon.Gpu != null;

            PcieSample last = null;
            double secs, sumRx, sumTx, maxRx, maxTx, sat, capRx, capTx;
            DateTime maxRxT, maxTxT, started;
            int epCount;
            var newEps = new List<PcieEpisode>();
            bool inEpisode;
            lock (mon.Lock)
            {
                if (mon.Samples.Count > 0) last = mon.Samples[mon.Samples.Count - 1];
                secs = mon.Seconds; sumRx = mon.SumRx; sumTx = mon.SumTx; maxRx = mon.MaxRx; maxTx = mon.MaxTx; sat = mon.SatSeconds;
                capRx = mon.CapRx; capTx = mon.CapTx; maxRxT = mon.MaxRxTime; maxTxT = mon.MaxTxTime; started = mon.Started;
                epCount = mon.Episodes.Count;
                for (int i = shownEpisodes; i < mon.Episodes.Count; i++) newEps.Add(mon.Episodes[i]);
                shownEpisodes = mon.Episodes.Count;
                inEpisode = mon.OpenEpisode != null;
            }
            btnMonExport.Enabled = secs > 0;
            btnMonReset.Enabled = secs > 0;

            if (ok)
            {
                lblMonInfo.Text = (mon.Running ? "Recording" : (secs > 0 ? "Paused" : "Not recording")) +
                    (secs > 0 ? "  " + Dur(secs) + " (since " + started.ToString("HH:mm:ss", Txt.Inv) + ")" : "") +
                    "   |   Link " + (last != null && mon.Running ? last.Link : (mon.CapLanes > 0 ? PcieLink.Gen(mon.CapGen) + " x" + mon.CapLanes : Txt.NA)) +
                    "   |   100 % = theoretical link, bottleneck line = measured limit";
            }
            else lblMonInfo.Text = "";

            if (!ok) cardLimit.Set(Txt.NA, "", Theme.Text);
            else if (mon.Measured)
                cardLimit.Set(Gbs(mon.MeasRx) + " / " + Gbs(mon.MeasTx),
                    "GB/s measured up / down = " + Pct(mon.MeasRx, capRx) + " / " + Pct(mon.MeasTx, capTx) + Txt.NL +
                    TheoText(capRx), Theme.Accent);
            else
                cardLimit.Set("Not measured",
                    "Measured when you start recording" + Txt.NL +
                    TheoText(capRx), Theme.Text);

            if (last != null && mon.Running)
                cardNow.Set(Gbs(last.Rx) + " / " + Gbs(last.Tx), "GB/s up / down" + Txt.NL + Pct(last.Rx, capRx) + " / " + Pct(last.Tx, capTx) + " of link" +
                    Txt.NL + "App: " + last.App, last.Saturated ? Theme.Red : Theme.Text);
            else cardNow.Set(Txt.NA, mon.Running ? "Waiting for data..." : "Not recording", Theme.Text);

            if (secs > 0)
            {
                cardAvg.Set(Gbs(sumRx / secs) + " / " + Gbs(sumTx / secs), "GB/s up / down" + Txt.NL + Pct(sumRx / secs, capRx) + " / " + Pct(sumTx / secs, capTx) + " of link", Theme.Text);
                cardPeak.Set(Gbs(maxRx) + " / " + Gbs(maxTx), "Up at " + maxRxT.ToString("HH:mm:ss", Txt.Inv) + " (" + Pct(maxRx, capRx) + ")" + Txt.NL +
                    "Down at " + maxTxT.ToString("HH:mm:ss", Txt.Inv) + " (" + Pct(maxTx, capTx) + ")", Theme.Text);
                cardSat.Set(sat.ToString(sat < 60 ? "0.0" : "0", Txt.Inv) + " s",
                    (sat / secs * 100).ToString("0.0", Txt.Inv) + " % of " + Dur(secs) + Txt.NL + epCount + " episode(s)" + (inEpisode ? Txt.NL + "Bottleneck right now" : ""),
                    sat > 0 ? Theme.Red : Theme.Green);
            }
            else
            {
                cardAvg.Set(Txt.NA, "", Theme.Text);
                cardPeak.Set(Txt.NA, "", Theme.Text);
                cardSat.Set(Txt.NA, "", Theme.Text);
            }

            if (newEps.Count > 0)
            {
                epList.BeginUpdate();
                foreach (var e in newEps)
                {
                    var it = new ListViewItem(e.Start.ToString("HH:mm:ss", Txt.Inv));
                    it.SubItems.Add(e.Seconds.ToString("0.0", Txt.Inv) + " s");
                    it.SubItems.Add(e.Dir);
                    it.SubItems.Add(e.PeakPct.ToString("0", Txt.Inv) + " %");
                    it.SubItems.Add(e.App);
                    epList.Items.Insert(0, it);
                }
                epList.EndUpdate();
                epList.StretchLast();
            }
            if (monitorPage.Visible) graph.Invalidate();
        }

        void ExportMonitor()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Export PCIe recording";
                dlg.Filter = "CSV file (*.csv)|*.csv";
                dlg.FileName = "pcie-" + DateTime.Now.ToString("yyyyMMdd-HHmm", Txt.Inv) + ".csv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { mon.ExportCsv(dlg.FileName); SetStatus("Saved " + dlg.FileName); }
                catch (Exception ex) { MessageBox.Show(this, "Could not save:" + Txt.NL + ex.Message, "GPU Toggle", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE && debounce != null) { debounce.Stop(); debounce.Start(); }
            base.WndProc(ref m);
        }

        GpuInfo Selected
        {
            get { return list.SelectedItems.Count == 0 ? null : (GpuInfo)list.SelectedItems[0].Tag; }
        }

        // ---------------------------------------------------------------- GPU page

        void RefreshList()
        {
            string keepId = Selected != null ? Selected.InstanceId : null;
            try { gpus = Gpu.Scan(); }
            catch (Exception ex) { SetStatus("Scan failed: " + ex.Message); return; }

            list.BeginUpdate();
            list.Items.Clear();
            foreach (var g in gpus)
            {
                if (!g.IsPhysical && !chkAll.Checked) continue;
                var it = new ListViewItem(g.Name);
                it.UseItemStyleForSubItems = false;
                it.SubItems.Add(g.Vendor);
                it.SubItems.Add(g.StatusText);
                for (int i = C_LOAD; i <= C_DISP; i++) it.SubItems.Add(Txt.NA);
                it.SubItems.Add(g.InstanceId);
                foreach (ListViewItem.ListViewSubItem si in it.SubItems) si.ForeColor = g.Enabled ? Theme.Text : Theme.Muted;
                it.Tag = g;
                list.Items.Add(it);
                if (g.InstanceId == keepId) it.Selected = true;
            }
            if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.Items[0].Selected = true;
            list.EndUpdate();
            list.StretchLast();

            if (gpuPage.Visible) SetStatus(list.Items.Count + " GPU(s) found. Double-click a row to toggle. Stats refresh every 2 s.");
            UpdateButtons();
            RefreshMonGpus();
        }

        void UpdateStats()
        {
            if (statsBusy) return;
            statsBusy = true;
            var snapshot = new List<GpuInfo>(gpus);
            var appPaths = new List<string>();
            foreach (ListViewItem it in appsList.Items) appPaths.Add(((AppEntry)it.Tag).Path);
            ThreadPool.QueueUserWorkItem(delegate
            {
                StatsResult r;
                try { r = Stats.Collect(snapshot, appPaths); } catch { r = new StatsResult(); }
                try { BeginInvoke((MethodInvoker)delegate { ApplyStats(r); statsBusy = false; }); }
                catch { statsBusy = false; }
            });
        }

        void ApplyStats(StatsResult r)
        {
            if (IsDisposed) return;
            list.BeginUpdate();
            foreach (ListViewItem it in list.Items)
            {
                var g = (GpuInfo)it.Tag;
                GpuStats s;
                if (!r.ByInstance.TryGetValue(g.InstanceId, out s)) s = new GpuStats();
                it.SubItems[C_LOAD].Text = s.Load;
                it.SubItems[C_TEMP].Text = s.Temp;
                it.SubItems[C_TEMP].ForeColor = s.TempValue >= 83 ? Theme.Red : (s.TempValue >= 75 ? Theme.Orange : Theme.Text);
                it.SubItems[C_POWER].Text = s.Power;
                it.SubItems[C_FAN].Text = s.Fan;
                it.SubItems[C_VRAM].Text = s.Vram;
                int n = 0;
                foreach (var d in r.Displays)
                    if (!string.IsNullOrEmpty(d.GpuDeviceId) && g.InstanceId.StartsWith(d.GpuDeviceId, StringComparison.OrdinalIgnoreCase)) n++;
                it.SubItems[C_DISP].Text = n.ToString();
            }
            list.EndUpdate();

            string sig = "";
            foreach (var d in r.Displays) sig += d.Output + d.Monitor + d.Mode + d.GpuName + d.Primary + "|";
            if (sig != lastDispSig)
            {
                lastDispSig = sig;
                dispList.BeginUpdate();
                dispList.Items.Clear();
                foreach (var d in r.Displays)
                {
                    var it = new ListViewItem(d.Output + (d.Primary ? "  (primary)" : ""));
                    it.SubItems.Add(d.Monitor);
                    it.SubItems.Add(d.Mode);
                    it.SubItems.Add(d.GpuName);
                    dispList.Items.Add(it);
                }
                dispList.EndUpdate();
                dispList.StretchLast();
            }

            appsList.BeginUpdate();
            foreach (ListViewItem it in appsList.Items)
            {
                var a = (AppEntry)it.Tag;
                string txt;
                if (!r.AppRunning.TryGetValue(a.Path, out txt)) continue;
                it.SubItems[A_RUN].Text = txt;
                it.SubItems[A_RUN].ForeColor = txt.StartsWith("Not running", StringComparison.Ordinal) || txt.StartsWith("Running -", StringComparison.Ordinal)
                    ? Theme.Muted : Theme.Text;
            }
            appsList.EndUpdate();
        }

        void UpdateButtons()
        {
            var g = Selected;
            btnEnable.Enabled = !busy && g != null && !g.Enabled;
            btnDisable.Enabled = !busy && g != null && g.Enabled;
            btnRefresh.Enabled = !busy;
            btnPcie.Enabled = !busy && g != null && g.Enabled && g.IsPhysical;
        }

        void TestBandwidth()
        {
            var g = Selected;
            if (g == null || !g.Enabled || !g.IsPhysical) return;
            RunBandwidthTest(g);
        }

        void SetStatus(string s) { lblStatus.Text = s; }

        void ToggleSelected()
        {
            var g = Selected;
            if (g == null) return;
            if (g.Enabled) DisableSelected(); else EnableSelected();
        }

        bool Apply(GpuInfo g, bool enable)
        {
            busy = true; UpdateButtons();
            Cursor = Cursors.WaitCursor;
            SetStatus((enable ? "Enabling " : "Disabling ") + g.Name + "...");
            Application.DoEvents();
            string output;
            bool ok;
            try { ok = Gpu.SetEnabled(g, enable, out output); }
            catch (Exception ex) { ok = false; output = ex.Message; }
            Cursor = Cursors.Default;
            busy = false;
            RefreshList();
            if (!ok)
                MessageBox.Show(this, "Failed:" + Txt.NL + Txt.NL + output.Trim(), "GPU Toggle", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                SetStatus(g.Name + (enable ? " enabled." : " disabled."));
            return ok;
        }

        void EnableSelected()
        {
            var g = Selected;
            if (g == null || g.Enabled) return;
            Apply(g, true);
        }

        void DisableSelected()
        {
            var g = Selected;
            if (g == null || !g.Enabled) return;

            int othersOn = 0;
            foreach (var o in gpus)
                if (o.IsPhysical && o.Enabled && o.InstanceId != g.InstanceId) othersOn++;
            if (g.IsPhysical && othersOn == 0)
            {
                var warn = MessageBox.Show(this,
                    "This is the last active GPU." + Txt.NL + Txt.NL +
                    "Windows will fall back to the Microsoft Basic Display Adapter (firmware framebuffer): " +
                    "low resolution, no GPU acceleration. On some machines the screen may stay black." + Txt.NL + Txt.NL +
                    "Continue anyway?",
                    "GPU Toggle", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (warn != DialogResult.Yes) return;
            }

            var ask = MessageBox.Show(this,
                "Disable " + Txt.Q + g.Name + Txt.Q + "?" + Txt.NL + Txt.NL +
                "Any screen connected to this GPU will go black (for the iGPU, that's the built-in screen)." + Txt.NL +
                "If you don't click Keep within " + RevertSeconds + " seconds, it will be re-enabled automatically.",
                "GPU Toggle", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask != DialogResult.Yes) return;

            if (!Apply(g, false)) return;

            using (var dlg = new RevertDialog(g.Name, RevertSeconds))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                {
                    SetStatus("Not confirmed - re-enabling " + g.Name + "...");
                    Apply(g, true);
                }
            }
        }

        // ---------------------------------------------------------------- Apps / Games page

        string PrefLabel(int pref)
        {
            string gpu = pref == 1 ? savingGpu : (pref == 2 ? perfGpu : null);
            return GpuPrefs.PrefName(pref) + (string.IsNullOrEmpty(gpu) ? "" : "  -  " + gpu);
        }

        List<AppEntry> SelectedApps()
        {
            var l = new List<AppEntry>();
            foreach (ListViewItem it in appsList.SelectedItems) l.Add((AppEntry)it.Tag);
            return l;
        }

        void RefreshApps()
        {
            try { savingGpu = Dxgi.PreferredAdapterName(1); } catch { savingGpu = null; }
            try { perfGpu = Dxgi.PreferredAdapterName(2); } catch { perfGpu = null; }
            lblPrefInfo.Text =
                "Power saving GPU:   " + (savingGpu ?? "unknown") + Txt.NL +
                "High performance GPU:   " + (perfGpu ?? "unknown") + Txt.NL +
                "A new GPU preference takes effect the next time the app is started.";
            btnPrefSave.Text = "Power saving" + (string.IsNullOrEmpty(savingGpu) ? "" : " (" + savingGpu + ")");
            btnPrefPerf.Text = "High performance" + (string.IsNullOrEmpty(perfGpu) ? "" : " (" + perfGpu + ")");

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in SelectedApps()) keep.Add(a.Path);
            var running = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem it in appsList.Items) running[((AppEntry)it.Tag).Path] = it.SubItems[A_RUN].Text;

            List<AppEntry> apps;
            try { apps = GpuPrefs.Load(); }
            catch (Exception ex) { SetStatus("Could not read GPU preferences: " + ex.Message); return; }

            appsList.BeginUpdate();
            appsList.Items.Clear();
            foreach (var a in apps)
            {
                var it = new ListViewItem(a.Name);
                it.UseItemStyleForSubItems = false;
                it.SubItems.Add(PrefLabel(a.Pref));
                string run;
                if (!running.TryGetValue(a.Path, out run)) run = Txt.NA;
                it.SubItems.Add(run).ForeColor = Theme.Muted;
                it.SubItems.Add(a.Path).ForeColor = Theme.Muted;
                it.Tag = a;
                appsList.Items.Add(it);
                if (keep.Contains(a.Path)) it.Selected = true;
            }
            appsList.EndUpdate();
            appsList.StretchLast();
            UpdateAppButtons();
            if (appsPage.Visible)
                SetStatus(apps.Count == 0
                    ? "No apps yet. Click Add app... or drop a game .exe on the list."
                    : apps.Count + " app(s). Select one, pick a GPU, then Launch to test.");
        }

        void UpdateAppButtons()
        {
            int n = appsList.SelectedItems.Count;
            btnRemoveApp.Enabled = n > 0;
            btnLaunch.Enabled = n == 1;
            btnPrefAuto.Enabled = n > 0;
            btnPrefSave.Enabled = n > 0;
            btnPrefPerf.Enabled = n > 0;
        }

        void AddApps(IEnumerable<string> files)
        {
            int added = 0;
            string last = null;
            foreach (var f in files)
            {
                if (string.IsNullOrEmpty(f) || !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                try { GpuPrefs.Add(f); added++; last = f; }
                catch (Exception ex) { SetStatus("Could not add " + f + ": " + ex.Message); }
            }
            if (added == 0) { SetStatus("Only .exe files can be added."); return; }
            RefreshApps();
            foreach (ListViewItem it in appsList.Items)
                it.Selected = string.Equals(((AppEntry)it.Tag).Path, last, StringComparison.OrdinalIgnoreCase);
            SetStatus("Added " + added + " app(s). Now pick a GPU for it.");
        }

        void AddAppFromFile()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Choose a game or app";
                dlg.Filter = "Applications (*.exe)|*.exe";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) == DialogResult.OK) AddApps(dlg.FileNames);
            }
        }

        void AddRunningApp()
        {
            using (var dlg = new ProcessPicker())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) AddApps(dlg.SelectedPaths);
            }
        }

        void RemoveSelectedApps()
        {
            var apps = SelectedApps();
            if (apps.Count == 0) return;
            foreach (var a in apps)
            {
                try { GpuPrefs.Remove(a.Path); }
                catch (Exception ex) { SetStatus("Could not remove " + a.Name + ": " + ex.Message); return; }
            }
            RefreshApps();
            SetStatus("Removed " + apps.Count + " app(s). They go back to Windows' default GPU choice.");
        }

        void SetSelectedPref(int pref)
        {
            var apps = SelectedApps();
            if (apps.Count == 0) return;
            foreach (var a in apps)
            {
                try { GpuPrefs.Set(a.Path, pref); }
                catch (Exception ex) { SetStatus("Could not save: " + ex.Message); return; }
            }
            RefreshApps();
            SetStatus((apps.Count == 1 ? apps[0].Name : apps.Count + " apps") + " set to " + PrefLabel(pref) +
                ". Restart the app if it is already running.");
        }

        void LaunchSelectedApp()
        {
            var apps = SelectedApps();
            if (apps.Count != 1) return;
            var a = apps[0];
            if (!File.Exists(a.Path))
            {
                MessageBox.Show(this, "File not found:" + Txt.NL + a.Path, "GPU Toggle", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                // Launch through Explorer so the game runs as a normal (non-admin) process.
                Process.Start("explorer.exe", Txt.Q + a.Path + Txt.Q);
                SetStatus("Launched " + a.Name + ". Watch the Running on column to see which GPU it uses.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not launch:" + Txt.NL + ex.Message, "GPU Toggle", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // Lists running apps that have a window, so a game that is already open can be added.
    class ProcessPicker : Form
    {
        StyledList plist;
        public List<string> SelectedPaths = new List<string>();

        public ProcessPicker()
        {
            Text = "Add running app";
            Font = Theme.Regular;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Theme.Hook(this);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(Ui.S(760), Ui.S(460));
            Padding = new Padding(Ui.S(14));

            plist = new StyledList();
            plist.MultiSelect = true;
            plist.Columns.Add("App", Ui.S(200));
            plist.Columns.Add("Window", Ui.S(220));
            plist.Columns.Add("Path", Ui.S(300));
            plist.DoubleClick += delegate { Accept(); };

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.BackColor = Theme.Surface;
            bar.Padding = new Padding(0, Ui.S(10), 0, 0);
            var cancel = Theme.MakeButton("Cancel", Theme.Card, false);
            var add = Theme.MakeButton("Add", Theme.Accent, true);
            cancel.DialogResult = DialogResult.Cancel;
            add.Click += delegate { Accept(); };
            bar.Controls.Add(cancel);
            bar.Controls.Add(add);
            CancelButton = cancel;

            Controls.Add(Theme.Section("Running apps", "Only apps with a window are shown", Theme.CardAround(plist)));
            Controls.Add(bar);
            Shown += delegate { Fill(); };
        }

        void Fill()
        {
            var rows = new List<string[]>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero) continue;
                    string file = p.MainModule.FileName;
                    if (string.IsNullOrEmpty(file) || !seen.Add(file)) continue;
                    rows.Add(new string[] { GpuPrefs.AppName(file), p.MainWindowTitle, file });
                }
                catch { }
                finally { p.Dispose(); }
            }
            rows.Sort(delegate (string[] a, string[] b) { return string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase); });
            plist.BeginUpdate();
            foreach (var r in rows) plist.Items.Add(new ListViewItem(r));
            plist.EndUpdate();
            plist.StretchLast();
        }

        void Accept()
        {
            SelectedPaths.Clear();
            foreach (ListViewItem it in plist.SelectedItems) SelectedPaths.Add(it.SubItems[2].Text);
            if (SelectedPaths.Count == 0) return;
            DialogResult = DialogResult.OK;
        }
    }

    // Measures how fast data moves between system memory and the GPU's VRAM, and shows the PCIe link it runs over.
    class BandwidthDialog : Form
    {
        GpuInfo gpu;
        Label lblLink, lblNote;
        Panel results;
        Button run;
        BandwidthResult last;
        string tunnel;
        string state = "Click Run test. It takes a few seconds and briefly loads the GPU.";
        double theoretical;

        bool autoRun;

        // autoRun: start the test right away and close by itself once it succeeds
        // (used by "Start recording" so the measured limit is in place before recording).
        public BandwidthDialog(GpuInfo g, bool autoRun = false)
        {
            gpu = g;
            this.autoRun = autoRun;
            if (autoRun) Shown += delegate { Start(); };
            Text = "PCIe bandwidth - " + g.Name;
            Font = Theme.Regular;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Theme.Hook(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(Ui.S(600), Ui.S(460));
            Padding = new Padding(Ui.S(16));

            var title = new Label();
            title.Dock = DockStyle.Top;
            title.Height = Ui.S(30);
            title.Font = Theme.Title;
            title.ForeColor = Theme.Text;
            title.Text = g.Name;
            title.AutoEllipsis = true;

            lblLink = new Label();
            lblLink.Dock = DockStyle.Top;
            lblLink.Height = Ui.S(100);
            lblLink.ForeColor = Theme.Text;
            lblLink.Padding = new Padding(0, Ui.S(6), 0, 0);

            results = new Panel();
            results.Dock = DockStyle.Top;
            results.Height = Ui.S(150);
            results.BackColor = Theme.Card;
            results.Paint += delegate (object s, PaintEventArgs e) { PaintResults(e.Graphics); };
            typeof(Panel).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(results, true, null);

            lblNote = new Label();
            lblNote.Dock = DockStyle.Fill;
            lblNote.Font = Theme.Small;
            lblNote.ForeColor = Theme.Muted;
            lblNote.Padding = new Padding(0, Ui.S(10), 0, 0);
            lblNote.Text =
                "The test copies 64 MB blocks between system memory and VRAM with Direct3D 11 and times them on the GPU. " +
                "Real transfers usually reach 80-90 % of the theoretical link bandwidth." + Txt.NL +
                "Thunderbolt / USB4 eGPUs tunnel PCIe, so the reported link may not show the real limit " +
                "(about 2.5-3.8 GB/s for Thunderbolt 3/4). The measured numbers are what counts.";

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.BackColor = Theme.Surface;
            var close = Theme.MakeButton("Close", Theme.Card, false);
            close.DialogResult = DialogResult.Cancel;
            run = Theme.MakeButton("Run test", Theme.Accent, true);
            run.Click += delegate { Start(); };
            bar.Controls.Add(close);
            bar.Controls.Add(run);
            CancelButton = close;
            AcceptButton = run;

            Controls.Add(lblNote);
            Controls.Add(results);
            Controls.Add(lblLink);
            Controls.Add(title);
            Controls.Add(bar);

            try { tunnel = Pcie.Tunnel(g.InstanceId); } catch { tunnel = null; }
            List<PcieLink> chain;
            try { chain = Pcie.Chain(g.InstanceId); } catch { chain = new List<PcieLink>(); }
            ShowLink(chain, true);
        }

        void ShowLink(List<PcieLink> chain, bool idle)
        {
            PcieLink own = chain.Count > 0 ? chain[0] : null;
            if (own == null)
            {
                theoretical = 0;
                lblLink.Text = "No PCIe link information for this GPU (integrated GPUs share system memory, " +
                    "so the result shows memory copy speed instead).";
                return;
            }
            var sb = new StringBuilder();
            sb.Append("PCIe link:  " + own.CurText + (idle ? " (now, idle)" : " (under load)"));
            if (own.MaxWidth > 0) sb.Append("   -   card supports " + own.MaxText);
            theoretical = own.CurGBs;
            if (tunnel != null)
                sb.Append(Txt.NL + "Connected through a " + tunnel + " tunnel - that is usually the real limit, not the link above.");
            if (theoretical > 0)
                sb.Append(Txt.NL + "Theoretical:  " + theoretical.ToString("0.0", Txt.Inv) + " GB/s per direction");
            if (idle) sb.Append(Txt.NL + "GPUs lower the link speed when idle; the test re-reads it under load.");
            lblLink.Text = sb.ToString();
        }

        void Start()
        {
            run.Enabled = false;
            last = null;
            state = "Starting...";
            results.Invalidate();
            Cursor = Cursors.AppStarting;
            var t = new Thread(delegate ()
            {
                BandwidthResult r;
                try
                {
                    r = D3D11Bandwidth.Test(gpu, delegate (string msg) { Post(delegate { state = msg; results.Invalidate(); }); });
                }
                catch (Exception ex) { r = new BandwidthResult(); r.Error = ex.Message; }
                Post(delegate { Done(r); });
            });
            t.IsBackground = true;
            t.Start();
        }

        void Post(MethodInvoker a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        void Done(BandwidthResult r)
        {
            if (IsDisposed) return;
            Cursor = Cursors.Default;
            run.Enabled = true;
            run.Text = "Run again";
            if (r.Error != null) { state = "Test failed: " + r.Error; results.Invalidate(); return; }
            last = r;
            D3D11Bandwidth.LastResults[gpu.InstanceId] = r;
            var chain = r.ChainLoad != null && r.ChainLoad.Count > 0 && r.ChainLoad[0] != null ? r.ChainLoad : r.Chain;
            ShowLink(chain, chain == r.Chain);
            state = null;
            results.Invalidate();
            if (autoRun)
            {
                // Leave the numbers on screen for a moment, then continue to recording.
                run.Enabled = false;
                var t = new System.Windows.Forms.Timer();
                t.Interval = 1500;
                t.Tick += delegate { t.Dispose(); if (!IsDisposed) { DialogResult = DialogResult.OK; Close(); } };
                t.Start();
            }
        }

        void PaintResults(Graphics g)
        {
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, results.Width - 1, results.Height - 1);
            if (last == null)
            {
                var r0 = new Rectangle(Ui.S(16), 0, results.Width - Ui.S(32), results.Height);
                TextRenderer.DrawText(g, state ?? "", Theme.Regular, r0, state != null && state.StartsWith("Test failed", StringComparison.Ordinal) ? Theme.Red : Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return;
            }
            double scale = theoretical > 0 ? theoretical : Math.Max(last.UploadGBs, last.DownloadGBs) * 1.15;
            DrawRow(g, Ui.S(18), "Host -> GPU  (upload)", last.UploadGBs, scale, flags);
            DrawRow(g, Ui.S(82), "GPU -> Host  (download)", last.DownloadGBs, scale, flags);
        }

        void DrawRow(Graphics g, int y, string label, double gbs, double scale, TextFormatFlags flags)
        {
            int x = Ui.S(16), w = results.Width - Ui.S(32);
            TextRenderer.DrawText(g, label, Theme.Semibold, new Rectangle(x, y, w, Ui.S(22)), Theme.Text, flags);
            string val = gbs.ToString("0.00", Txt.Inv) + " GB/s";
            if (theoretical > 0) val += "   (" + (gbs / theoretical * 100).ToString("0", Txt.Inv) + " % of link)";
            TextRenderer.DrawText(g, val, Theme.Semibold, new Rectangle(x, y, w, Ui.S(22)), Theme.Accent,
                (flags & ~TextFormatFlags.Left) | TextFormatFlags.Right);
            int bh = Ui.S(10);
            var track = new Rectangle(x, y + Ui.S(30), w, bh);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(track, bh / 2f))
            using (var br = new SolidBrush(Theme.Border))
                g.FillPath(br, path);
            int fw = scale > 0 ? (int)Math.Round(w * Math.Min(1.0, gbs / scale)) : 0;
            if (fw > 0)
            {
                using (var path = Theme.Round(new Rectangle(track.X, track.Y, Math.Max(fw, bh), bh), bh / 2f))
                using (var br = new SolidBrush(Theme.Accent))
                    g.FillPath(br, path);
            }
            g.SmoothingMode = old;
        }
    }

    class RevertDialog : Form
    {
        int left;
        string gpuName;
        Label lbl;
        System.Windows.Forms.Timer timer;

        public RevertDialog(string gpuName, int seconds)
        {
            this.gpuName = gpuName;
            left = seconds;

            Text = "Keep this change?";
            Font = Theme.Regular;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            Theme.Hook(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(Ui.S(460), Ui.S(156));

            var accent = new Panel();
            accent.Dock = DockStyle.Top;
            accent.Height = Ui.S(4);
            accent.BackColor = Theme.Orange;

            lbl = new Label();
            lbl.Location = new Point(Ui.S(18), Ui.S(20));
            lbl.Size = new Size(Ui.S(424), Ui.S(72));
            lbl.ForeColor = Theme.Text;

            var keep = Theme.MakeButton("Keep", Theme.Green, true);
            keep.DialogResult = DialogResult.OK;
            keep.AutoSize = false;
            keep.Size = new Size(Ui.S(110), Ui.S(34));
            keep.Location = new Point(Ui.S(214), Ui.S(104));

            var revert = Theme.MakeButton("Revert now", Theme.Card, false);
            revert.DialogResult = DialogResult.Cancel;
            revert.AutoSize = false;
            revert.Size = new Size(Ui.S(110), Ui.S(34));
            revert.Location = new Point(Ui.S(332), Ui.S(104));

            Controls.Add(lbl);
            Controls.Add(keep);
            Controls.Add(revert);
            Controls.Add(accent);
            AcceptButton = keep;
            CancelButton = revert;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate
            {
                left--;
                UpdateText();
                if (left <= 0) { timer.Stop(); DialogResult = DialogResult.Cancel; }
            };
            Shown += delegate { Activate(); timer.Start(); };
            FormClosed += delegate { timer.Stop(); };
            UpdateText();
        }

        void UpdateText()
        {
            lbl.Text = Txt.Q + gpuName + Txt.Q + " is now disabled." + Txt.NL + Txt.NL + "Keep this setting? Reverting in " + left + " s...";
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.Scale = g.DpiX / 96f;
            Theme.Init();
            Theme.LoadMode();
            Application.Run(new MainForm());
        }
    }
}
