// GpuToggle - WinForms app to enable/disable GPUs (eGPU / iGPU) and monitor them.
// Build with build.bat (uses the C# compiler that ships with Windows).
// Written in C# 5 syntax so the built-in csc.exe can compile it.
// NOTE: no backslash characters anywhere in this file (they can get stripped when downloading).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

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

    class DxgiAdapter { public uint Ven, Dev; public string Luid; public ulong DedicatedTotal; }

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
                        list.Add(x);
                    }
                    Marshal.ReleaseComObject(a);
                }
            }
            finally { Marshal.ReleaseComObject(f); }
            return list;
        }
    }

    // ------------------------------------------------------------------ Stats: Windows GPU perf counters (all vendors)

    static class PerfGpu
    {
        static Dictionary<string, CounterSample> prev = new Dictionary<string, CounterSample>();
        static readonly Regex LuidRx = new Regex("luid_0x[0-9a-f]{8}_0x[0-9a-f]{8}", RegexOptions.IgnoreCase);
        static readonly Regex EngRx = new Regex("eng_([0-9]+)", RegexOptions.IgnoreCase);

        // Returns luid -> load % (busiest engine, like Task Manager)
        public static Dictionary<string, float> Load()
        {
            var result = new Dictionary<string, float>();
            var cat = new PerformanceCounterCategory("GPU Engine");
            InstanceDataCollection col = cat.ReadCategory()["Utilization Percentage"];
            var sums = new Dictionary<string, float>();
            var next = new Dictionary<string, CounterSample>();
            foreach (InstanceData d in col.Values)
            {
                next[d.InstanceName] = d.Sample;
                CounterSample old;
                if (!prev.TryGetValue(d.InstanceName, out old)) continue;
                float v = CounterSample.Calculate(old, d.Sample);
                var lm = LuidRx.Match(d.InstanceName);
                var em = EngRx.Match(d.InstanceName);
                if (!lm.Success || !em.Success) continue;
                string key = lm.Value.ToLowerInvariant() + "|" + em.Groups[1].Value;
                float s;
                sums.TryGetValue(key, out s);
                sums[key] = s + v;
            }
            prev = next;
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
    }

    static class Stats
    {
        static string Gb(double bytes) { return (bytes / 1073741824.0).ToString("0.0", Txt.Inv); }

        public static StatsResult Collect(List<GpuInfo> gpus)
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
            return r;
        }
    }

    // ------------------------------------------------------------------ UI

    static class Ui
    {
        public static float Scale = 1f;
        public static int S(int v) { return (int)Math.Round(v * Scale); }
    }

    class MainForm : Form
    {
        const int RevertSeconds = 15;
        const int WM_DEVICECHANGE = 0x0219;
        // GPU list columns
        const int C_STATUS = 2, C_LOAD = 3, C_TEMP = 4, C_POWER = 5, C_FAN = 6, C_VRAM = 7, C_DISP = 8;

        ListView list, dispList;
        Button btnEnable, btnDisable, btnRefresh;
        CheckBox chkAll;
        Label lblStatus;
        System.Windows.Forms.Timer debounce, statsTimer;
        List<GpuInfo> gpus = new List<GpuInfo>();
        bool busy, statsBusy;
        string lastDispSig = "";

        public MainForm()
        {
            Text = "GPU Toggle";
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(Ui.S(1060), Ui.S(520));
            MinimumSize = new Size(Ui.S(720), Ui.S(400));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            list = MakeList();
            list.Columns.Add("GPU", Ui.S(250));
            list.Columns.Add("Vendor", Ui.S(70));
            list.Columns.Add("Status", Ui.S(80));
            list.Columns.Add("Load", Ui.S(60));
            list.Columns.Add("Temp", Ui.S(60));
            list.Columns.Add("Power / TDP", Ui.S(100));
            list.Columns.Add("Fan", Ui.S(110));
            list.Columns.Add("VRAM", Ui.S(115));
            list.Columns.Add("Displays", Ui.S(70));
            list.Columns.Add("Instance ID", Ui.S(300));
            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            list.DoubleClick += delegate { ToggleSelected(); };

            dispList = MakeList();
            dispList.Columns.Add("Output", Ui.S(140));
            dispList.Columns.Add("Monitor", Ui.S(220));
            dispList.Columns.Add("Mode", Ui.S(190));
            dispList.Columns.Add("Driven by GPU", Ui.S(300));

            var dispHeader = new Label();
            dispHeader.Text = "Displays - which monitor is driven by which GPU";
            dispHeader.Dock = DockStyle.Top;
            dispHeader.Height = Ui.S(26);
            dispHeader.Padding = new Padding(Ui.S(2), Ui.S(6), 0, 0);
            dispHeader.Font = new Font(Font, FontStyle.Bold);

            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(dispList);
            split.Panel2.Controls.Add(dispHeader);
            dispList.BringToFront();

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Ui.S(46);
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.Padding = new Padding(Ui.S(6));

            btnRefresh = MakeButton("Refresh");
            btnDisable = MakeButton("Disable");
            btnEnable = MakeButton("Enable");
            btnRefresh.Click += delegate { RefreshList(); };
            btnDisable.Click += delegate { DisableSelected(); };
            btnEnable.Click += delegate { EnableSelected(); };

            chkAll = new CheckBox();
            chkAll.Text = "Show virtual adapters";
            chkAll.AutoSize = true;
            chkAll.Margin = new Padding(Ui.S(6), Ui.S(9), Ui.S(12), 0);
            chkAll.CheckedChanged += delegate { RefreshList(); };

            bar.Controls.Add(btnRefresh);
            bar.Controls.Add(btnDisable);
            bar.Controls.Add(btnEnable);
            bar.Controls.Add(chkAll);

            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Bottom;
            lblStatus.Height = Ui.S(24);
            lblStatus.Padding = new Padding(Ui.S(6), Ui.S(4), 0, 0);
            lblStatus.ForeColor = SystemColors.GrayText;

            Controls.Add(split);
            Controls.Add(lblStatus);
            Controls.Add(bar);
            split.BringToFront();

            debounce = new System.Windows.Forms.Timer();
            debounce.Interval = 800;
            debounce.Tick += delegate { debounce.Stop(); if (!busy) RefreshList(); };

            statsTimer = new System.Windows.Forms.Timer();
            statsTimer.Interval = 2000;
            statsTimer.Tick += delegate { UpdateStats(); };

            Shown += delegate
            {
                split.SplitterDistance = (int)(split.Height * 0.5);
                RefreshList();
                UpdateStats();
                statsTimer.Start();
            };
            FormClosing += delegate { statsTimer.Stop(); };
        }

        ListView MakeList()
        {
            var l = new ListView();
            l.Dock = DockStyle.Fill;
            l.View = View.Details;
            l.FullRowSelect = true;
            l.MultiSelect = false;
            l.HideSelection = false;
            l.GridLines = true;
            return l;
        }

        Button MakeButton(string text)
        {
            var b = new Button();
            b.Text = text;
            b.Size = new Size(Ui.S(96), Ui.S(32));
            return b;
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
                var st = it.SubItems.Add(g.StatusText);
                st.ForeColor = g.ErrorCode == 0 ? Color.ForestGreen : (g.ErrorCode == 22 ? Color.Gray : Color.Firebrick);
                st.Font = new Font(Font, FontStyle.Bold);
                for (int i = C_LOAD; i <= C_DISP; i++) it.SubItems.Add(Txt.NA);
                it.SubItems.Add(g.InstanceId);
                it.Tag = g;
                list.Items.Add(it);
                if (g.InstanceId == keepId) it.Selected = true;
            }
            if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.Items[0].Selected = true;
            list.EndUpdate();

            SetStatus(list.Items.Count + " GPU(s) found. Double-click a row to toggle. Stats refresh every 2 s.");
            UpdateButtons();
        }

        void UpdateStats()
        {
            if (statsBusy) return;
            statsBusy = true;
            var snapshot = new List<GpuInfo>(gpus);
            ThreadPool.QueueUserWorkItem(delegate
            {
                StatsResult r;
                try { r = Stats.Collect(snapshot); } catch { r = new StatsResult(); }
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
                it.SubItems[C_TEMP].ForeColor = s.TempValue >= 83 ? Color.Firebrick : (s.TempValue >= 75 ? Color.DarkOrange : list.ForeColor);
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
                    var it = new ListViewItem(d.Output + (d.Primary ? " (primary)" : ""));
                    it.SubItems.Add(d.Monitor);
                    it.SubItems.Add(d.Mode);
                    it.SubItems.Add(d.GpuName);
                    dispList.Items.Add(it);
                }
                dispList.EndUpdate();
            }
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
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(Ui.S(440), Ui.S(140));

            lbl = new Label();
            lbl.Location = new Point(Ui.S(14), Ui.S(14));
            lbl.Size = new Size(Ui.S(412), Ui.S(70));

            var keep = new Button();
            keep.Text = "Keep";
            keep.DialogResult = DialogResult.OK;
            keep.Size = new Size(Ui.S(100), Ui.S(32));
            keep.Location = new Point(Ui.S(214), Ui.S(94));

            var revert = new Button();
            revert.Text = "Revert now";
            revert.DialogResult = DialogResult.Cancel;
            revert.Size = new Size(Ui.S(100), Ui.S(32));
            revert.Location = new Point(Ui.S(326), Ui.S(94));

            Controls.Add(lbl);
            Controls.Add(keep);
            Controls.Add(revert);
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
            Application.Run(new MainForm());
        }
    }
}
