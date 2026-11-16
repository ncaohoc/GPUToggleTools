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
        public static readonly Color Header = Color.FromArgb(15, 27, 51);
        public static readonly Color HeaderSel = Color.FromArgb(34, 52, 92);
        public static readonly Color HeaderHover = Color.FromArgb(26, 41, 74);
        public static readonly Color HeaderMuted = Color.FromArgb(148, 163, 184);
        public static readonly Color Surface = Color.FromArgb(243, 246, 250);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(226, 232, 240);
        public static readonly Color Text = Color.FromArgb(30, 41, 59);
        public static readonly Color Muted = Color.FromArgb(100, 116, 139);
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color Green = Color.FromArgb(22, 163, 74);
        public static readonly Color Red = Color.FromArgb(220, 38, 38);
        public static readonly Color Orange = Color.FromArgb(234, 88, 12);
        public static readonly Color RowAlt = Color.FromArgb(248, 250, 252);
        public static readonly Color RowSel = Color.FromArgb(219, 234, 254);

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
            Color bg = primary ? color : Card;
            Color fg = primary ? Color.White : Text;
            b.BackColor = bg;
            b.ForeColor = fg;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = primary ? Darken(bg, 18) : Surface;
            b.FlatAppearance.MouseDownBackColor = primary ? Darken(bg, 34) : Border;
            b.EnabledChanged += delegate
            {
                b.BackColor = b.Enabled ? bg : Border;
                b.ForeColor = b.Enabled ? fg : Muted;
            };
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
            if (t == "Enabled") return new Color[] { Color.FromArgb(220, 252, 231), Color.FromArgb(21, 128, 61) };
            if (t == "Disabled") return new Color[] { Color.FromArgb(241, 245, 249), Color.FromArgb(100, 116, 139) };
            return new Color[] { Color.FromArgb(254, 226, 226), Color.FromArgb(185, 28, 28) };
        }

        public static Color[] PrefPill(string t)
        {
            if (t.StartsWith("High", StringComparison.Ordinal)) return new Color[] { Color.FromArgb(219, 234, 254), Color.FromArgb(29, 78, 216) };
            if (t.StartsWith("Power", StringComparison.Ordinal)) return new Color[] { Color.FromArgb(254, 243, 199), Color.FromArgb(180, 83, 9) };
            return new Color[] { Color.FromArgb(241, 245, 249), Color.FromArgb(71, 85, 105) };
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
        Button btnEnable, btnDisable, btnRefresh;
        Button btnAddApp, btnAddRunning, btnRemoveApp, btnLaunch, btnGfxSettings, btnPrefAuto, btnPrefSave, btnPrefPerf;
        Button navGpus, navApps;
        Panel gpuPage, appsPage, header;
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
            ClientSize = new Size(Ui.S(1120), Ui.S(680));
            MinimumSize = new Size(Ui.S(820), Ui.S(520));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            logo = LoadLogo(Ui.S(40));

            BuildHeader();
            BuildGpuPage();
            BuildAppsPage();

            var content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Theme.Surface;
            content.Padding = new Padding(Ui.S(16), Ui.S(14), Ui.S(16), Ui.S(10));
            content.Controls.Add(gpuPage);
            content.Controls.Add(appsPage);

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
            FormClosing += delegate { statsTimer.Stop(); };
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
            navGpus.Click += delegate { ShowPage(0); };
            navApps.Click += delegate { ShowPage(1); };
            nav.Controls.Add(navGpus);
            nav.Controls.Add(navApps);
            header.Controls.Add(nav);
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

        void ShowPage(int page)
        {
            gpuPage.Visible = page == 0;
            appsPage.Visible = page == 1;
            navGpus.BackColor = page == 0 ? Theme.HeaderSel : Theme.Header;
            navGpus.ForeColor = page == 0 ? Color.White : Theme.HeaderMuted;
            navApps.BackColor = page == 1 ? Theme.HeaderSel : Theme.Header;
            navApps.ForeColor = page == 1 ? Color.White : Theme.HeaderMuted;
            if (page == 1) RefreshApps();
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

            chkAll = new CheckBox();
            chkAll.Text = "Show virtual adapters";
            chkAll.AutoSize = true;
            chkAll.ForeColor = Theme.Muted;
            chkAll.Margin = new Padding(Ui.S(8), Ui.S(8), 0, 0);
            chkAll.CheckedChanged += delegate { RefreshList(); };

            bar.Controls.Add(btnEnable);
            bar.Controls.Add(btnDisable);
            bar.Controls.Add(btnRefresh);
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
            Application.Run(new MainForm());
        }
    }
}
