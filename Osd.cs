// OSD: a click-through, always-on-top overlay that shows FPS, GPU / CPU load and temperature, VRAM, RAM and
// PCIe traffic on top of a game, plus a guess at what is limiting the frame rate.
// Nothing is injected into the game: FPS comes from the present events Windows already logs (ETW).
// Source is plain ASCII on purpose and has no backslash characters (see README).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GpuToggle
{
    // ------------------------------------------------------------------ FPS from ETW present events

    class FrameStats
    {
        public double Fps, FrameMs;
        public double Low1;      // "1 % low" FPS over the last 10 s, 0 until there are enough frames
        public string Api;
    }

    static class FrameCounter
    {
        const string SessionName = "GPUToggle-OSD";
        const long Sec = 10000000;          // event timestamps are FILETIME (100 ns)
        const long History = 10 * Sec;

        static Guid DxgiGuid = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");      // Microsoft-Windows-DXGI (D3D10/11/12)
        static Guid D3d9Guid = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");      // Microsoft-Windows-D3D9
        static Guid KrnlGuid = new Guid("802EC45A-1E99-4B83-9920-87C98277BA9D");      // Microsoft-Windows-DxgKrnl (any API, e.g. Vulkan / OpenGL)

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int StartTraceW(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int ControlTraceW(ulong handle, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll")] static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint code, byte level, ulong anyKeyword, ulong allKeyword, uint timeout, IntPtr parameters);
        [DllImport("advapi32.dll")] static extern ulong OpenTraceW(IntPtr logfile);
        [DllImport("advapi32.dll")] static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] static extern int CloseTrace(ulong handle);

        delegate void RecordCallback(IntPtr record);

        class Frames
        {
            public List<long> Api = new List<long>();                  // DXGI / D3D9 Present_Start
            public List<long>[] Krnl = { new List<long>(), new List<long>(), new List<long>(), new List<long>() };   // DxgKrnl event groups
            public string ApiName = "DirectX";
            public long Last;
            public int Adds;
        }

        static readonly object Lock = new object();
        static Dictionary<uint, Frames> byPid = new Dictionary<uint, Frames>();
        static ulong session, trace;
        static Thread thread;
        static RecordCallback callback;     // kept alive while ETW calls it
        static IntPtr logfile, loggerName;
        static volatile bool running;
        public static string Error;
        public static bool Running { get { return running; } }

        const int PropsSize = 120 + 1024;   // EVENT_TRACE_PROPERTIES + room for the session name

        static IntPtr NewProps()
        {
            IntPtr p = Marshal.AllocHGlobal(PropsSize);
            for (int i = 0; i < PropsSize; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, PropsSize);        // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);               // Wnode.ClientContext = QPC timestamps
            Marshal.WriteInt32(p, 44, 0x00020000);      // WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(p, 48, 64);              // BufferSize (KB)
            Marshal.WriteInt32(p, 52, 8);               // MinimumBuffers
            Marshal.WriteInt32(p, 56, 64);              // MaximumBuffers
            Marshal.WriteInt32(p, 64, 0x100 | 0x10);    // EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_USE_MS_FLUSH_TIMER
            Marshal.WriteInt32(p, 68, 200);             // FlushTimer = 200 ms, so frames arrive with little delay
            Marshal.WriteInt32(p, 116, 120);            // LoggerNameOffset
            return p;
        }

        public static bool Start()
        {
            if (running) return true;
            Error = null;
            IntPtr props = NewProps();
            try
            {
                int r = StartTraceW(out session, SessionName, props);
                if (r == 183)   // ERROR_ALREADY_EXISTS: left over from a previous run that did not exit cleanly
                {
                    Marshal.FreeHGlobal(props); props = NewProps();
                    ControlTraceW(0, SessionName, props, 1);
                    Marshal.FreeHGlobal(props); props = NewProps();
                    r = StartTraceW(out session, SessionName, props);
                }
                if (r != 0)
                {
                    Error = r == 5 ? "FPS needs administrator rights." : "Could not start the ETW session for FPS (error " + r + ").";
                    return false;
                }
            }
            finally { Marshal.FreeHGlobal(props); }

            EnableTraceEx2(session, ref DxgiGuid, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
            EnableTraceEx2(session, ref D3d9Guid, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
            EnableTraceEx2(session, ref KrnlGuid, 1, 4, 0x8000001, 0, 0, IntPtr.Zero);   // Base | Present keywords

            // EVENT_TRACE_LOGFILEW (448 bytes on x64)
            logfile = Marshal.AllocHGlobal(448);
            for (int i = 0; i < 448; i++) Marshal.WriteByte(logfile, i, 0);
            loggerName = Marshal.StringToHGlobalUni(SessionName);
            Marshal.WriteIntPtr(logfile, 8, loggerName);                    // LoggerName
            Marshal.WriteInt32(logfile, 28, 0x100 | 0x10000000);            // PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD
            callback = OnEvent;
            Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(callback));   // EventRecordCallback
            trace = OpenTraceW(logfile);
            if (trace == ulong.MaxValue)
            {
                Error = "Could not open the ETW session for FPS.";
                StopSession();
                FreeLogfile();
                return false;
            }
            running = true;
            thread = new Thread(delegate ()
            {
                try { ProcessTrace(new ulong[] { trace }, 1, IntPtr.Zero, IntPtr.Zero); } catch { }
                running = false;
            });
            thread.IsBackground = true;
            thread.Name = "OSD frame counter";
            thread.Start();
            return true;
        }

        public static void Stop()
        {
            if (thread == null) return;
            StopSession();
            try { CloseTrace(trace); } catch { }
            thread.Join(3000);
            thread = null;
            running = false;
            FreeLogfile();
            lock (Lock) byPid.Clear();
        }

        static void StopSession()
        {
            IntPtr props = NewProps();
            try { ControlTraceW(session, null, props, 1); } catch { }   // EVENT_TRACE_CONTROL_STOP
            finally { Marshal.FreeHGlobal(props); }
        }

        static void FreeLogfile()
        {
            if (logfile != IntPtr.Zero) { Marshal.FreeHGlobal(logfile); logfile = IntPtr.Zero; }
            if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
        }

        // EVENT_RECORD: EventHeader.ProcessId @12, TimeStamp @16, ProviderId @24, EventDescriptor.Id @40
        static void OnEvent(IntPtr rec)
        {
            try
            {
                uint provider = (uint)Marshal.ReadInt32(rec, 24);   // Data1 of the provider GUID
                int id = (ushort)Marshal.ReadInt16(rec, 40);
                int kind;
                string api = null;
                if (provider == 0xCA11C036u) { if (id != 42 && id != 55) return; kind = 0; api = "DXGI"; }        // Present_Start, PresentMultiplaneOverlay_Start
                else if (provider == 0x783ACA0Au) { if (id != 1) return; kind = 0; api = "D3D9"; }                // Present_Start
                else if (provider == 0x802EC45Au)
                {
                    if (id == 166 || id == 168 || id == 252) kind = 1;      // Blit / Flip / FlipMultiPlaneOverlay
                    else if (id == 171) kind = 2;                            // PresentHistory_Start
                    else if (id == 215) kind = 3;                            // PresentHistoryDetailed_Start
                    else if (id == 184) kind = 4;                            // Present_Info
                    else return;
                }
                else return;
                uint pid = (uint)Marshal.ReadInt32(rec, 12);
                long ts = Marshal.ReadInt64(rec, 16);
                lock (Lock)
                {
                    Frames f;
                    if (!byPid.TryGetValue(pid, out f)) { f = new Frames(); byPid[pid] = f; }
                    if (api != null) f.ApiName = api;
                    Insert(kind == 0 ? f.Api : f.Krnl[kind - 1], ts);
                    if (ts > f.Last) f.Last = ts;
                    if (++f.Adds >= 512) { f.Adds = 0; Trim(f, f.Last - History); }
                }
            }
            catch { }
        }

        static void Insert(List<long> l, long ts)
        {
            int n = l.Count;
            if (n == 0 || ts >= l[n - 1]) { l.Add(ts); return; }
            int k = l.BinarySearch(ts);     // buffers from different CPUs can arrive slightly out of order
            l.Insert(k < 0 ? ~k : k, ts);
        }

        static void Trim(Frames f, long from)
        {
            Cut(f.Api, from);
            foreach (var k in f.Krnl) Cut(k, from);
        }

        static void Cut(List<long> l, long from)
        {
            int k = 0;
            while (k < l.Count && l[k] < from) k++;
            if (k > 0) l.RemoveRange(0, k);
        }

        static int Recent(List<long> l, long now)
        {
            int c = 0;
            for (int i = l.Count - 1; i >= 0 && now - l[i] <= 2 * Sec; i--) c++;
            return c;
        }

        // Frame rate of a process right now, or null if it is not presenting frames.
        public static FrameStats Get(uint pid)
        {
            long now = DateTime.UtcNow.ToFileTimeUtc();
            lock (Lock)
            {
                List<uint> dead = null;
                foreach (var kv in byPid)
                    if (now - kv.Value.Last > 15 * Sec) { if (dead == null) dead = new List<uint>(); dead.Add(kv.Key); }
                if (dead != null) foreach (var d in dead) byPid.Remove(d);

                Frames f;
                if (pid == 0 || !byPid.TryGetValue(pid, out f)) return null;
                Trim(f, now - History);
                List<long> l = f.Api;
                string api = f.ApiName;
                if (Recent(l, now) < 2)
                {
                    // No DirectX presents (Vulkan, OpenGL...): use the kernel group that saw the most presents.
                    l = null;
                    int best = 1;
                    foreach (var k in f.Krnl) { int c = Recent(k, now); if (c > best) { best = c; l = k; } }
                    api = "Vulkan / OpenGL";
                    if (l == null) return null;
                }
                return Compute(l, api);
            }
        }

        static FrameStats Compute(List<long> l, string api)
        {
            int n = l.Count;
            long last = l[n - 1];
            int i = n - 1;
            while (i > 0 && last - l[i - 1] <= Sec) i--;
            if (n - 1 - i < 1 || last == l[i]) return null;
            var s = new FrameStats();
            s.Api = api;
            s.Fps = (n - 1 - i) * (double)Sec / (last - l[i]);
            s.FrameMs = 1000.0 / s.Fps;
            if (n >= 30)
            {
                var ft = new long[n - 1];
                for (int k = 0; k < n - 1; k++) ft[k] = l[k + 1] - l[k];
                Array.Sort(ft);
                long p99 = ft[(int)Math.Floor((ft.Length - 1) * 0.99)];
                if (p99 > 0) s.Low1 = Math.Min(s.Fps, Sec / (double)p99);
            }
            return s;
        }
    }

    // ------------------------------------------------------------------ CPU load, RAM, temperatures

    static class CpuRam
    {
        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, byte[] buffer, int length, out int returned);

        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = 64, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);

        static long[] prevIdle, prevTotal;
        static readonly byte[] buf = new byte[48 * 256];

        // Total load and the busiest logical processor since the previous call (%).
        // A game held back by its main thread shows one busy core while the total stays low.
        public static bool Cpu(out float total, out float maxCore)
        {
            total = maxCore = -1;
            int ret;
            if (NtQuerySystemInformation(8, buf, buf.Length, out ret) != 0) return false;   // SystemProcessorPerformanceInformation
            int n = ret / 48;
            var idle = new long[n];
            var all = new long[n];
            for (int i = 0; i < n; i++)
            {
                idle[i] = BitConverter.ToInt64(buf, i * 48);
                all[i] = BitConverter.ToInt64(buf, i * 48 + 8) + BitConverter.ToInt64(buf, i * 48 + 16);   // kernel (includes idle) + user
            }
            bool ok = prevIdle != null && prevIdle.Length == n;
            if (ok)
            {
                long si = 0, st = 0;
                float mx = 0;
                for (int i = 0; i < n; i++)
                {
                    long di = idle[i] - prevIdle[i], dt = all[i] - prevTotal[i];
                    si += di; st += dt;
                    if (dt > 0) mx = Math.Max(mx, (float)(dt - di) * 100f / dt);
                }
                if (st > 0) { total = Math.Max(0, Math.Min(100, (float)(st - si) * 100f / st)); maxCore = Math.Max(0, Math.Min(100, mx)); }
                else ok = false;
            }
            prevIdle = idle;
            prevTotal = all;
            return ok;
        }

        public static void Ram(out double used, out double total)
        {
            var m = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(m)) { total = m.ullTotalPhys; used = m.ullTotalPhys - m.ullAvailPhys; }
            else used = total = -1;
        }
    }

    // CPU temperature. Windows has no standard API for it: we read LibreHardwareMonitor / OpenHardwareMonitor
    // (their WMI sensors, while one of them is running), else the ACPI thermal zone (approximate, not on every PC).
    static class HwSensors
    {
        static readonly string[] Namespaces = { "root/LibreHardwareMonitor", "root/OpenHardwareMonitor" };
        static readonly string[] CpuNames = { "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "CPU (Tctl/Tdie)", "Tctl", "Core Average", "CPU Cores" };
        static DateTime[] retry = new DateTime[2];

        public static float Read(out string source, out float gpuTemp)
        {
            source = "";
            gpuTemp = -1;
            for (int n = 0; n < Namespaces.Length; n++)
            {
                if (DateTime.Now < retry[n]) continue;
                try
                {
                    float cpu = -1, cpuAny = -1;
                    int rank = int.MaxValue;
                    using (var s = new ManagementObjectSearcher(Namespaces[n], "SELECT Name, Value, Parent FROM Sensor WHERE SensorType='Temperature'"))
                    {
                        foreach (ManagementObject mo in s.Get())
                        {
                            string name = Convert.ToString(mo["Name"]), parent = Convert.ToString(mo["Parent"]).ToLowerInvariant();
                            float v = Convert.ToSingle(mo["Value"]);
                            if (v <= 0 || v > 150) continue;
                            if (parent.Contains("cpu"))
                            {
                                int r = Array.IndexOf(CpuNames, name);
                                if (r >= 0 && r < rank) { rank = r; cpu = v; }
                                cpuAny = Math.Max(cpuAny, v);
                            }
                            else if (parent.Contains("gpu") && (name == "GPU Core" || (gpuTemp < 0 && name.StartsWith("GPU", StringComparison.Ordinal)))) gpuTemp = v;
                        }
                    }
                    if (cpu < 0) cpu = cpuAny;
                    if (cpu > 0) { source = n == 0 ? "LibreHardwareMonitor" : "OpenHardwareMonitor"; return cpu; }
                }
                catch { retry[n] = DateTime.Now.AddSeconds(30); }   // namespace missing = the tool is not running
            }
            try
            {
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                float best = -1;
                foreach (InstanceData d in cat.ReadCategory()["High Precision Temperature"].Values)
                    best = Math.Max(best, d.RawValue / 10f - 273.15f);     // tenths of a kelvin
                if (best > 5 && best < 150) { source = "ACPI thermal zone"; return best; }
            }
            catch { }
            return -1;
        }
    }

    // ------------------------------------------------------------------ Sampler (background thread)

    class OsdVerdict
    {
        public string Text = "", Detail = "";
        public int Level = -1;      // -1 neutral, 0 fine, 1 warning, 2 bottleneck
    }

    class OsdSnapshot
    {
        public IntPtr Hwnd;
        public uint Pid;
        public string App = "";
        public float CpuLoad = -1, CpuMaxCore = -1, CpuTemp = -1;
        public string CpuTempSource = "";
        public double RamUsed = -1, RamTotal = -1;
        public string GpuName = "";
        public bool GpuNvidia;
        public float GpuLoad = -1, GpuTemp = -1, GpuPower = -1, GpuPowerLimit = -1;
        public double VramUsed = -1, VramTotal = -1;
        public double PcieRx = -1, PcieTx = -1;        // bytes/s, host -> GPU / GPU -> host
        public double PcieRefRx, PcieRefTx;             // what counts as 100 %
        public bool PcieMeasured;                       // refs come from the bandwidth test (else theoretical link)
        public FrameStats Frames;
        public string FpsError;
        public int RefreshHz;
        public OsdVerdict Verdict = new OsdVerdict();

        public double VramPct { get { return VramTotal > 0 && VramUsed >= 0 ? VramUsed / VramTotal * 100 : -1; } }
        public double RamPct { get { return RamTotal > 0 && RamUsed >= 0 ? RamUsed / RamTotal * 100 : -1; } }
        public double PciePct
        {
            get
            {
                if (PcieRx < 0 || PcieRefRx <= 0 || PcieRefTx <= 0) return -1;
                return Math.Max(PcieRx / PcieRefRx, PcieTx / PcieRefTx) * 100;
            }
        }
    }

    class OsdSampler
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        public volatile List<GpuInfo> Gpus = new List<GpuInfo>();
        public volatile string GpuChoice = "auto";      // "auto" = the GPU the game in front runs on, else an InstanceId
        readonly object snapLock = new object();
        OsdSnapshot latest;
        Thread thread;
        volatile bool running;

        public OsdSnapshot Latest { get { lock (snapLock) return latest; } }

        // sampler-thread state
        Dictionary<string, CounterSample> enginePrev = new Dictionary<string, CounterSample>();
        Dictionary<string, float> luidLoad = new Dictionary<string, float>();
        Dictionary<int, Dictionary<string, float>> pidLoad = new Dictionary<int, Dictionary<string, float>>();
        Dictionary<string, long> vramUsed = new Dictionary<string, long>();
        List<DxgiAdapter> adapters = new List<DxgiAdapter>();
        DateTime adaptersAt = DateTime.MinValue;
        Dictionary<string, IntPtr> nvml = new Dictionary<string, IntPtr>();
        string pcieFor;
        double pcieTheo, pcieMeasRx, pcieMeasTx;
        DateTime pcieAt = DateTime.MinValue;
        double sumRx, sumTx;
        int nPcie;
        float cpuTemp = -1, lhmGpuTemp = -1;
        string cpuTempSrc = "";
        IntPtr hzWnd;
        int hz;
        DateTime hzAt = DateTime.MinValue;
        IntPtr gameWnd;
        uint gamePid, namePid;
        string appName = "";
        GpuInfo autoGpu;
        readonly Queue<OsdSnapshot> hist = new Queue<OsdSnapshot>();
        readonly uint selfPid = (uint)Process.GetCurrentProcess().Id;

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "OSD sampler";
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            if (thread != null) { thread.Join(1500); thread = null; }
            lock (snapLock) latest = null;
            hist.Clear();
        }

        void Loop()
        {
            int tick = 0;
            while (running)
            {
                var t0 = DateTime.Now;
                try { Step(tick++); } catch { }
                int wait = 100 - (int)(DateTime.Now - t0).TotalMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
            }
        }

        GpuInfo CurrentGpu()
        {
            var list = Gpus;
            string choice = GpuChoice;
            if (choice != "auto")
                foreach (var g in list) if (g.InstanceId == choice) return g;
            if (autoGpu != null) foreach (var g in list) if (g.InstanceId == autoGpu.InstanceId) return g;
            foreach (var g in list) if (g.VenId == 0x10DE) return g;
            return list.Count > 0 ? list[0] : null;
        }

        IntPtr NvmlHandle(GpuInfo g)
        {
            if (g == null || g.VenId != 0x10DE) return IntPtr.Zero;
            IntPtr h;
            if (!nvml.TryGetValue(g.InstanceId, out h))
            {
                try { h = Nvml.Find(g.VenId, g.DevId); } catch { h = IntPtr.Zero; }
                nvml[g.InstanceId] = h;
            }
            return h;
        }

        DxgiAdapter Adapter(GpuInfo g)
        {
            if (g == null) return null;
            foreach (var a in adapters) if (a.Ven == g.VenId && a.Dev == g.DevId) return a;
            return null;
        }

        void Step(int tick)
        {
            // PCIe counters cover only ~20 ms, so read them every 100 ms and average.
            GpuInfo gpu = CurrentGpu();
            IntPtr h = NvmlHandle(gpu);
            if (h != IntPtr.Zero)
            {
                double rx, tx;
                if (Nvml.Throughput(h, out rx, out tx)) { sumRx += rx; sumTx += tx; nPcie++; }
            }
            if (tick % 5 != 0) return;

            var s = new OsdSnapshot();
            ForegroundGame(s);

            float ct, cm;
            if (CpuRam.Cpu(out ct, out cm)) { s.CpuLoad = ct; s.CpuMaxCore = cm; }
            CpuRam.Ram(out s.RamUsed, out s.RamTotal);
            if (tick % 20 == 0) cpuTemp = HwSensors.Read(out cpuTempSrc, out lhmGpuTemp);
            s.CpuTemp = cpuTemp;
            s.CpuTempSource = cpuTempSrc;

            if ((DateTime.Now - adaptersAt).TotalSeconds > 10)
            {
                try { adapters = Dxgi.List(); } catch { }
                adaptersAt = DateTime.Now;
            }
            if (tick % 10 == 0)
            {
                try { luidLoad = PerfGpu.Load(ref enginePrev, out pidLoad); } catch { }
                try { vramUsed = PerfGpu.DedicatedUsage(); } catch { }
                PickAutoGpu(s.Pid);
            }

            var now = CurrentGpu();
            if (now != gpu) { sumRx = sumTx = 0; nPcie = 0; gpu = now; h = NvmlHandle(gpu); }
            if (gpu != null)
            {
                s.GpuName = gpu.Name;
                s.GpuNvidia = gpu.VenId == 0x10DE;
                var ad = Adapter(gpu);
                float l;
                long used;
                if (ad != null && luidLoad.TryGetValue(ad.Luid, out l)) s.GpuLoad = l;
                if (ad != null && ad.DedicatedTotal > 0 && vramUsed.TryGetValue(ad.Luid, out used)) { s.VramUsed = used; s.VramTotal = ad.DedicatedTotal; }
                if (h != IntPtr.Zero)
                {
                    float load, temp, watts, limit;
                    double vu, vt;
                    try
                    {
                        Nvml.Basic(h, out load, out temp, out watts, out limit, out vu, out vt);
                        if (load >= 0) s.GpuLoad = load;
                        s.GpuTemp = temp; s.GpuPower = watts; s.GpuPowerLimit = limit;
                        if (vt > 0) { s.VramUsed = vu; s.VramTotal = vt; }
                    }
                    catch { }
                }
                else if (gpu.VenId != 0x8086) s.GpuTemp = lhmGpuTemp;   // AMD: only if LibreHardwareMonitor runs
                PcieRefs(gpu, h, tick, s);
            }
            if (nPcie > 0) { s.PcieRx = sumRx / nPcie; s.PcieTx = sumTx / nPcie; }
            sumRx = sumTx = 0;
            nPcie = 0;

            s.Frames = FrameCounter.Running ? FrameCounter.Get(s.Pid) : null;
            s.FpsError = FrameCounter.Error;
            s.RefreshHz = RefreshHz(s.Hwnd);
            s.Verdict = Judge(s);
            lock (snapLock) latest = s;
        }

        // The window in front is the game. When it is this app (looking at the OSD page), keep the last game.
        void ForegroundGame(OsdSnapshot s)
        {
            IntPtr w = GetForegroundWindow();
            uint pid;
            GetWindowThreadProcessId(w, out pid);
            if (pid != 0 && pid != selfPid) { gameWnd = w; gamePid = pid; }
            s.Hwnd = gameWnd;
            s.Pid = gamePid;
            if (gamePid != namePid)
            {
                namePid = gamePid;
                try { using (var p = Process.GetProcessById((int)gamePid)) appName = p.ProcessName; } catch { appName = ""; }
            }
            s.App = appName;
        }

        // Auto mode: the GPU the game in front renders on (its busiest GPU in the per-process engine counters).
        void PickAutoGpu(uint pid)
        {
            Dictionary<string, float> per;
            string best = null;
            float bestValue = 0.5f;
            if (pidLoad.TryGetValue((int)pid, out per))
                foreach (var kv in per) if (kv.Value > bestValue) { best = kv.Key; bestValue = kv.Value; }
            if (best == null) return;     // game idle or not on a GPU: keep the previous pick
            foreach (var a in adapters)
                if (a.Luid == best)
                    foreach (var g in Gpus)
                        if (g.VenId == a.Ven && g.DevId == a.Dev) { autoGpu = g; return; }
        }

        // 100 % = what the bandwidth test measured, else the theoretical link (the faster of the link Windows
        // reports and the one NVML sees under load, because idle links drop to a lower PCIe gen).
        void PcieRefs(GpuInfo g, IntPtr h, int tick, OsdSnapshot s)
        {
            if (pcieFor != g.InstanceId || (DateTime.Now - pcieAt).TotalSeconds > 10)
            {
                if (pcieFor != g.InstanceId) pcieTheo = 0;
                pcieFor = g.InstanceId;
                pcieAt = DateTime.Now;
                pcieMeasRx = pcieMeasTx = 0;
                try
                {
                    BandwidthResult r;
                    if (D3D11Bandwidth.LastResults.TryGetValue(g.InstanceId, out r) && r.UploadGBs > 0 && r.DownloadGBs > 0)
                    { pcieMeasRx = r.UploadGBs * 1e9; pcieMeasTx = r.DownloadGBs * 1e9; }
                    var chain = Pcie.Chain(g.InstanceId);
                    if (chain.Count > 0 && chain[0] != null) pcieTheo = Math.Max(pcieTheo, chain[0].CurGBs * 1e9);
                }
                catch { }
            }
            if (h != IntPtr.Zero && tick % 10 == 0)
            {
                int gen, width;
                try { Nvml.Link(h, out gen, out width); pcieTheo = Math.Max(pcieTheo, PcieLink.LaneGBs(gen) * width * 1e9); } catch { }
            }
            s.PcieMeasured = pcieMeasRx > 0;
            s.PcieRefRx = s.PcieMeasured ? pcieMeasRx : pcieTheo;
            s.PcieRefTx = s.PcieMeasured ? pcieMeasTx : pcieTheo;
        }

        int RefreshHz(IntPtr wnd)
        {
            if (wnd != hzWnd || (DateTime.Now - hzAt).TotalSeconds > 5)
            {
                hzWnd = wnd;
                hzAt = DateTime.Now;
                try { hz = Displays.RefreshHz((wnd != IntPtr.Zero ? Screen.FromHandle(wnd) : Screen.PrimaryScreen).DeviceName); } catch { hz = 0; }
            }
            return hz;
        }

        // Averages the last 3 s so the verdict does not flicker between frames.
        double Avg(Func<OsdSnapshot, double> f)
        {
            double sum = 0;
            int n = 0;
            foreach (var x in hist) { double v = f(x); if (v >= 0) { sum += v; n++; } }
            return n > 0 ? sum / n : -1;
        }

        OsdVerdict Judge(OsdSnapshot s)
        {
            hist.Enqueue(s);
            while (hist.Count > 6) hist.Dequeue();
            double gpu = Avg(x => x.GpuLoad), cpu = Avg(x => x.CpuLoad), core = Avg(x => x.CpuMaxCore);
            double fps = Avg(x => x.Frames != null ? x.Frames.Fps : -1);
            double pcie = Avg(x => x.PciePct), vram = Avg(x => x.VramPct), ram = Avg(x => x.RamPct);
            var v = new OsdVerdict();

            if (pcie >= (s.PcieMeasured ? 90 : 85))
            {
                v.Text = "PCIe link"; v.Level = 2;
                v.Detail = "Traffic at " + pcie.ToString("0") + " % of the " + (s.PcieMeasured ? "measured" : "theoretical") + " link limit. Typical for eGPUs:" + Txt.NL +
                    "lower texture quality / resolution, connect the monitor to the eGPU.";
            }
            else if (vram >= 95)
            {
                v.Text = "VRAM full"; v.Level = 2;
                v.Detail = "VRAM " + vram.ToString("0") + " % used: textures spill into system RAM over PCIe (stutter)." + Txt.NL + "Lower texture quality.";
            }
            else if (fps < 0 && (gpu < 0 || gpu < 50))
            {
                v.Text = "No game"; v.Level = -1;
                v.Detail = "Bring a game to the front: the OSD measures the app in the foreground.";
            }
            else if (gpu >= 95)
            {
                v.Text = "GPU-bound"; v.Level = 0;
                v.Detail = "GPU " + gpu.ToString("0") + " % - the normal case. For more FPS lower resolution /" + Txt.NL + "settings or use DLSS / FSR.";
            }
            else if (core >= 90 || cpu >= 90)
            {
                v.Text = "CPU-bound"; v.Level = 1;
                v.Detail = "Busiest CPU thread " + core.ToString("0") + " % while the GPU waits (" + Math.Max(0, gpu).ToString("0") + " %)." + Txt.NL +
                    "Lower CPU-heavy settings (view distance, crowds, physics).";
            }
            else if (fps > 0 && s.RefreshHz > 0 && fps >= s.RefreshHz * 0.95 && fps <= s.RefreshHz * 1.03)
            {
                v.Text = "V-Sync / refresh cap"; v.Level = 0;
                v.Detail = "FPS sits at the " + s.RefreshHz + " Hz refresh rate: the game is capped, nothing is maxed.";
            }
            else if (ram >= 92)
            {
                v.Text = "RAM full"; v.Level = 1;
                v.Detail = "System RAM " + ram.ToString("0") + " % used: Windows pages to disk. Close background apps.";
            }
            else if (fps > 0 && gpu >= 0 && gpu < 85)
            {
                v.Text = "Limited elsewhere"; v.Level = 1;
                v.Detail = "Neither GPU (" + gpu.ToString("0") + " %) nor CPU is maxed: FPS limiter, game engine, or one" + Txt.NL +
                    "game thread hopping between cores.";
            }
            else
            {
                v.Text = "No bottleneck"; v.Level = 0;
                v.Detail = "GPU " + Math.Max(0, gpu).ToString("0") + " %, busiest CPU thread " + Math.Max(0, core).ToString("0") + " %.";
            }
            return v;
        }
    }

    // ------------------------------------------------------------------ Settings (HKCU, next to the theme)

    static class OsdSettings
    {
        public const int ItemFps = 1, ItemGpu = 2, ItemVram = 4, ItemCpu = 8, ItemRam = 16, ItemPcie = 32, ItemLimit = 64;
        public static int Corner = 0, TextSize = 1, Alpha = 70, Items = 127;
        public static string Gpu = "auto";

        static string Key { get { return "Software" + Txt.BS + "GPUToggle"; } }

        public static void Load()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k == null) return;
                    Corner = Clamp(k.GetValue("OsdCorner"), 0, 3, Corner);
                    TextSize = Clamp(k.GetValue("OsdSize"), 0, 2, TextSize);
                    Alpha = Clamp(k.GetValue("OsdBackground"), 0, 100, Alpha);
                    Items = Clamp(k.GetValue("OsdItems"), 0, 127, Items);
                    string g = k.GetValue("OsdGpu") as string;
                    if (!string.IsNullOrEmpty(g)) Gpu = g;
                }
            }
            catch { }
        }

        static int Clamp(object v, int min, int max, int def)
        {
            return v is int ? Math.Max(min, Math.Min(max, (int)v)) : def;
        }

        public static void Save()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    k.SetValue("OsdCorner", Corner, RegistryValueKind.DWord);
                    k.SetValue("OsdSize", TextSize, RegistryValueKind.DWord);
                    k.SetValue("OsdBackground", Alpha, RegistryValueKind.DWord);
                    k.SetValue("OsdItems", Items, RegistryValueKind.DWord);
                    k.SetValue("OsdGpu", Gpu, RegistryValueKind.String);
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Overlay window

    class OsdSeg
    {
        public string Text;
        public Color Color;
        public OsdSeg(string text, Color color) { Text = text; Color = color; }
    }

    // Layered, click-through, topmost tool window drawn with per-pixel alpha (UpdateLayeredWindow).
    // It never takes focus. Shows over windowed and borderless games; exclusive fullscreen can hide it.
    class OsdOverlay : Form
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, int key, ref BLEND blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        Font font;
        int fontSize = -1;
        float cw, lh;

        public OsdOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "GPU Toggle OSD";
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_LAYERED | WS_EX_TRANSPARENT (click-through) | WS_EX_TOOLWINDOW (no Alt+Tab) | WS_EX_NOACTIVATE | WS_EX_TOPMOST
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; }   // WM_NCHITTEST -> HTTRANSPARENT
            base.WndProc(ref m);
        }

        void EnsureFont(int size)
        {
            if (font != null && size == fontSize) return;
            if (font != null) font.Dispose();
            fontSize = size;
            float px = (size == 0 ? 13f : (size == 2 ? 21f : 16f)) * Ui.Scale;
            font = new Font("Consolas", px, FontStyle.Bold, GraphicsUnit.Pixel);
            using (var b = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(b))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                cw = g.MeasureString("MMMMMMMMMM", font, PointF.Empty, StringFormat.GenericTypographic).Width / 10f;
            }
            lh = font.GetHeight() * 1.12f;
        }

        public void Render(List<List<OsdSeg>> lines, int corner, int size, int alpha, IntPtr target)
        {
            if (IsDisposed || !IsHandleCreated || lines.Count == 0) return;
            EnsureFont(size);
            int maxChars = 0;
            foreach (var line in lines)
            {
                int n = 0;
                foreach (var seg in line) n += seg.Text.Length;
                maxChars = Math.Max(maxChars, n);
            }
            int pad = (int)Math.Round(lh * 0.45f);
            int w = (int)Math.Ceiling(maxChars * cw) + pad * 2, h = (int)Math.Ceiling(lines.Count * lh) + pad * 2 - (int)(lh * 0.12f);

            using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    if (alpha > 0)
                        using (var path = Theme.Round(new Rectangle(0, 0, w - 1, h - 1), pad * 0.8f))
                        using (var br = new SolidBrush(Color.FromArgb(alpha * 255 / 100, 8, 12, 20)))
                            g.FillPath(br, path);
                    var sf = StringFormat.GenericTypographic;
                    using (var shadow = new SolidBrush(Color.FromArgb(220, 0, 0, 0)))
                    {
                        float y = pad;
                        foreach (var line in lines)
                        {
                            float x = pad;
                            foreach (var seg in line)
                            {
                                if (seg.Text.Trim().Length > 0)
                                {
                                    g.DrawString(seg.Text, font, shadow, x + 1, y + 1, sf);
                                    using (var br = new SolidBrush(seg.Color)) g.DrawString(seg.Text, font, br, x, y, sf);
                                }
                                x += seg.Text.Length * cw;
                            }
                            y += lh;
                        }
                    }
                }

                Rectangle scr;
                try { scr = (target != IntPtr.Zero ? Screen.FromHandle(target) : Screen.PrimaryScreen).Bounds; }
                catch { scr = Screen.PrimaryScreen.Bounds; }
                int m = Ui.S(10);
                var dst = new POINT();
                dst.X = corner == 1 || corner == 3 ? scr.Right - w - m : scr.Left + m;
                dst.Y = corner >= 2 ? scr.Bottom - h - m : scr.Top + m;

                IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc), hBmp = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                    old = SelectObject(memDc, hBmp);
                    var sz = new SIZE();
                    sz.W = w; sz.H = h;
                    var src = new POINT();
                    var blend = new BLEND();
                    blend.Alpha = 255;
                    blend.Format = 1;   // AC_SRC_ALPHA
                    UpdateLayeredWindow(Handle, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, 2);   // ULW_ALPHA
                }
                finally
                {
                    if (old != IntPtr.Zero) SelectObject(memDc, old);
                    if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                    DeleteDC(memDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
            // Stay above the game even if it (or another app) made itself topmost later.
            SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);   // HWND_TOPMOST, NOSIZE | NOMOVE | NOACTIVATE
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && font != null) font.Dispose();
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ OSD page of the main window

    partial class MainForm
    {
        const int WM_HOTKEY = 0x0312, OsdHotkeyId = 0x4F53;
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        Panel osdPage;
        Button btnOsdShow;
        ComboBox cmbOsdGpu, cmbOsdPos, cmbOsdSize;
        NumericUpDown numOsdAlpha;
        StatCard cardFps, cardGpu, cardVram, cardCpu, cardRam, cardPcie, cardVerdict;
        Label lblOsdInfo;
        List<GpuInfo> osdGpus = new List<GpuInfo>();
        OsdSampler osdSampler = new OsdSampler();
        OsdOverlay osdOverlay;
        bool osdShown, osdHotkey, osdComboSync;
        System.Windows.Forms.Timer osdTimer;

        // OSD colors (fixed: the overlay sits on top of the game, not on the app theme)
        static readonly Color OsdLabel = Color.FromArgb(255, 170, 70), OsdValue = Color.FromArgb(245, 247, 250), OsdDim = Color.FromArgb(160, 170, 185),
            OsdGood = Color.FromArgb(110, 220, 130), OsdWarn = Color.FromArgb(255, 180, 60), OsdBad = Color.FromArgb(255, 90, 90);

        Label OsdLabelCtl(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = Theme.Muted;
            l.Font = Theme.Semibold;
            l.Margin = new Padding(Ui.S(8), Ui.S(8), Ui.S(6), 0);
            return l;
        }

        ComboBox OsdCombo(int width, params string[] items)
        {
            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Width = Ui.S(width);
            c.Margin = new Padding(0, Ui.S(5), Ui.S(6), 0);
            c.Items.AddRange(items);
            return c;
        }

        void BuildOsdPage()
        {
            OsdSettings.Load();
            osdSampler.GpuChoice = OsdSettings.Gpu;

            osdPage = new Panel();
            osdPage.Dock = DockStyle.Fill;
            osdPage.BackColor = Theme.Surface;
            osdPage.Visible = false;

            var bar = Theme.Toolbar();
            var lblGpu = OsdLabelCtl("GPU:");
            lblGpu.Margin = new Padding(0, Ui.S(8), Ui.S(6), 0);
            cmbOsdGpu = OsdCombo(250);
            cmbOsdGpu.SelectedIndexChanged += delegate
            {
                if (osdComboSync) return;
                int i = cmbOsdGpu.SelectedIndex;
                OsdSettings.Gpu = i <= 0 || i - 1 >= osdGpus.Count ? "auto" : osdGpus[i - 1].InstanceId;
                osdSampler.GpuChoice = OsdSettings.Gpu;
                OsdSettings.Save();
            };
            btnOsdShow = Theme.MakeButton("Show OSD", Theme.Green, true);
            btnOsdShow.Margin = new Padding(Ui.S(4), 0, Ui.S(8), Ui.S(8));
            btnOsdShow.Click += delegate { ToggleOsd(); };

            cmbOsdPos = OsdCombo(120, "Top left", "Top right", "Bottom left", "Bottom right");
            cmbOsdPos.SelectedIndex = OsdSettings.Corner;
            cmbOsdPos.SelectedIndexChanged += delegate { OsdSettings.Corner = cmbOsdPos.SelectedIndex; OsdSettings.Save(); OsdTick(); };
            cmbOsdSize = OsdCombo(90, "Small", "Medium", "Large");
            cmbOsdSize.SelectedIndex = OsdSettings.TextSize;
            cmbOsdSize.SelectedIndexChanged += delegate { OsdSettings.TextSize = cmbOsdSize.SelectedIndex; OsdSettings.Save(); OsdTick(); };
            numOsdAlpha = new NumericUpDown();
            numOsdAlpha.Minimum = 0; numOsdAlpha.Maximum = 100; numOsdAlpha.Increment = 5;
            numOsdAlpha.Value = OsdSettings.Alpha;
            numOsdAlpha.Width = Ui.S(56);
            numOsdAlpha.Margin = new Padding(0, Ui.S(5), Ui.S(4), 0);
            numOsdAlpha.ValueChanged += delegate { OsdSettings.Alpha = (int)numOsdAlpha.Value; OsdSettings.Save(); OsdTick(); };

            bar.Controls.Add(lblGpu);
            bar.Controls.Add(cmbOsdGpu);
            bar.Controls.Add(btnOsdShow);
            bar.Controls.Add(OsdLabelCtl("Position:"));
            bar.Controls.Add(cmbOsdPos);
            bar.Controls.Add(OsdLabelCtl("Size:"));
            bar.Controls.Add(cmbOsdSize);
            bar.Controls.Add(OsdLabelCtl("Background:"));
            bar.Controls.Add(numOsdAlpha);
            var pct = OsdLabelCtl("%");
            pct.Margin = new Padding(0, Ui.S(8), 0, 0);
            bar.Controls.Add(pct);

            var bar2 = Theme.Toolbar();
            var lblShow = OsdLabelCtl("Show on the OSD:");
            lblShow.Margin = new Padding(0, Ui.S(4), Ui.S(10), Ui.S(6));
            bar2.Controls.Add(lblShow);
            string[] names = { "FPS", "GPU", "VRAM", "CPU", "RAM", "PCIe up / down", "Bottleneck" };
            for (int i = 0; i < names.Length; i++)
            {
                int bit = 1 << i;
                var cb = new CheckBox();
                cb.Text = names[i];
                cb.AutoSize = true;
                cb.ForeColor = Theme.Text;
                cb.BackColor = Theme.Surface;
                cb.Checked = (OsdSettings.Items & bit) != 0;
                cb.Margin = new Padding(0, Ui.S(4), Ui.S(14), Ui.S(6));
                cb.CheckedChanged += delegate
                {
                    if (cb.Checked) OsdSettings.Items |= bit; else OsdSettings.Items &= ~bit;
                    OsdSettings.Save();
                    OsdTick();
                };
                bar2.Controls.Add(cb);
            }

            lblOsdInfo = new Label();
            lblOsdInfo.Dock = DockStyle.Top;
            lblOsdInfo.Height = Ui.S(24);
            lblOsdInfo.ForeColor = Theme.Muted;
            lblOsdInfo.Font = Theme.Small;
            lblOsdInfo.AutoEllipsis = true;

            var cards = new TableLayoutPanel();
            cards.Dock = DockStyle.Top;
            cards.Height = Ui.S(110);
            cards.ColumnCount = 6;
            cards.RowCount = 1;
            cards.BackColor = Theme.Surface;
            for (int i = 0; i < 6; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6));
            cardFps = new StatCard(); cardFps.Caption = "FPS";
            cardGpu = new StatCard(); cardGpu.Caption = "GPU";
            cardVram = new StatCard(); cardVram.Caption = "VRAM";
            cardCpu = new StatCard(); cardCpu.Caption = "CPU";
            cardRam = new StatCard(); cardRam.Caption = "RAM";
            cardPcie = new StatCard(); cardPcie.Caption = "PCIe up / down";
            StatCard[] all = { cardFps, cardGpu, cardVram, cardCpu, cardRam, cardPcie };
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

            cardVerdict = new StatCard();
            cardVerdict.Caption = "What limits the FPS (last 3 s)";
            cardVerdict.Dock = DockStyle.Top;
            cardVerdict.Height = Ui.S(100);
            cardVerdict.Margin = new Padding(0);

            var gap2 = new Panel();
            gap2.Dock = DockStyle.Top;
            gap2.Height = Ui.S(10);
            gap2.BackColor = Theme.Surface;

            var help = new Label();
            help.BackColor = Theme.Card;
            help.ForeColor = Theme.Text;
            help.Padding = new Padding(Ui.S(12), Ui.S(10), Ui.S(12), Ui.S(10));
            string nl = Txt.NL;
            help.Text =
                "GPU-bound (GPU >= 95 %): the normal case. For more FPS lower resolution / settings or use DLSS / FSR." + nl +
                "CPU-bound (busiest CPU thread >= 90 %, GPU below 95 %): the GPU waits for the game. Raising resolution costs no FPS; lower view distance, crowds, physics." + nl +
                "PCIe link (>= 90 % of the measured limit): typical for eGPUs. Lower textures / resolution, and connect the monitor to the eGPU so frames do not travel back over the link." + nl +
                "VRAM full (>= 95 %): textures spill into system RAM and come over PCIe, which causes stutter. Lower texture quality." + nl +
                "V-Sync / refresh cap: FPS sits at the monitor refresh rate. Limited elsewhere: nothing is maxed (FPS limiter, engine, one thread hopping between cores)." + nl + nl +
                "FPS comes from the present events Windows logs (ETW), nothing is injected into the game: DirectX 9 - 12, Vulkan / OpenGL through the kernel graphics events. " +
                "The OSD measures the app in the foreground. It is a click-through topmost window, so it shows over windowed and borderless games; exclusive fullscreen can hide it (switch the game to borderless). " +
                "PCIe traffic needs an NVIDIA GPU (NVML); run PCIe bandwidth... once so 100 % is the measured limit. CPU temperature: LibreHardwareMonitor if it is running, else the ACPI thermal zone.";
            var helpSection = Theme.Section("How to read it", "Hotkey Ctrl+Shift+O shows / hides the OSD from inside the game", Theme.CardAround(help));

            osdPage.Controls.Add(helpSection);
            osdPage.Controls.Add(gap2);
            osdPage.Controls.Add(cardVerdict);
            osdPage.Controls.Add(gap);
            osdPage.Controls.Add(cards);
            osdPage.Controls.Add(lblOsdInfo);
            osdPage.Controls.Add(bar2);
            osdPage.Controls.Add(bar);

            osdTimer = new System.Windows.Forms.Timer();
            osdTimer.Interval = 500;
            osdTimer.Tick += delegate { OsdTick(); };

            HandleCreated += delegate { osdHotkey = RegisterHotKey(Handle, OsdHotkeyId, 0x2 | 0x4 | 0x4000, (uint)Keys.O); };   // Ctrl+Shift+O, no repeat
            FormClosing += delegate
            {
                osdTimer.Stop();
                osdSampler.Stop();
                FrameCounter.Stop();
                if (IsHandleCreated) UnregisterHotKey(Handle, OsdHotkeyId);
                if (osdOverlay != null) osdOverlay.Dispose();
            };
        }

        // GPU combo: "Auto" + the enabled physical GPUs, NVIDIA first.
        void RefreshOsdGpus()
        {
            if (cmbOsdGpu == null) return;
            var l = new List<GpuInfo>();
            foreach (var g in gpus) if (g.IsPhysical && g.Enabled && g.VenId == 0x10DE) l.Add(g);
            foreach (var g in gpus) if (g.IsPhysical && g.Enabled && g.VenId != 0x10DE) l.Add(g);
            osdSampler.Gpus = l;
            var ids = new System.Text.StringBuilder();
            foreach (var g in l) ids.Append(g.InstanceId).Append('|');
            var old = new System.Text.StringBuilder();
            foreach (var g in osdGpus) old.Append(g.InstanceId).Append('|');
            if (ids.ToString() == old.ToString() && cmbOsdGpu.Items.Count > 0) return;

            osdGpus = l;
            osdComboSync = true;
            cmbOsdGpu.Items.Clear();
            cmbOsdGpu.Items.Add("Auto (GPU the game runs on)");
            int sel = 0;
            for (int i = 0; i < l.Count; i++)
            {
                cmbOsdGpu.Items.Add(l[i].Name);
                if (l[i].InstanceId == OsdSettings.Gpu) sel = i + 1;
            }
            cmbOsdGpu.SelectedIndex = sel;
            osdComboSync = false;
        }

        void ToggleOsd()
        {
            osdShown = !osdShown;
            if (osdShown)
            {
                if (osdOverlay == null || osdOverlay.IsDisposed) osdOverlay = new OsdOverlay();
                osdOverlay.Show();
            }
            else if (osdOverlay != null) osdOverlay.Hide();
            btnOsdShow.Text = osdShown ? "Hide OSD" : "Show OSD";
            Theme.SetButtonColor(btnOsdShow, osdShown ? Theme.Red : Theme.Green);
            UpdateOsdRunning();
            OsdTick();
        }

        // The sampler and the ETW session only run while the OSD is shown or its page is open.
        void UpdateOsdRunning()
        {
            if (osdPage == null || osdTimer == null) return;
            bool need = osdShown || osdPage.Visible;
            if (need)
            {
                if (!FrameCounter.Running) FrameCounter.Start();
                osdSampler.Start();
                osdTimer.Start();
            }
            else
            {
                osdTimer.Stop();
                osdSampler.Stop();
                FrameCounter.Stop();
            }
        }

        void OsdTick()
        {
            if (IsDisposed || osdPage == null) return;
            var s = osdSampler.Latest;
            if (osdPage.Visible) UpdateOsdCards(s);
            if (osdShown && osdOverlay != null && !osdOverlay.IsDisposed)
                osdOverlay.Render(OsdLines(s), OsdSettings.Corner, OsdSettings.TextSize, OsdSettings.Alpha, s != null ? s.Hwnd : IntPtr.Zero);
        }

        // ---- formatting

        static string F(double v, string fmt) { return v.ToString(fmt, Txt.Inv); }
        static string Gb(double bytes) { return F(bytes / 1073741824.0, "0.0"); }
        static string Deg { get { return ((char)0xB0).ToString() + "C"; } }
        static string PadL(string s, int n) { return s.Length >= n ? s : new string(' ', n - s.Length) + s; }

        static Color TempColor(float t) { return t >= 90 ? OsdBad : (t >= 80 ? OsdWarn : OsdValue); }
        static Color PctColor(double p, double warn, double bad) { return p >= bad ? OsdBad : (p >= warn ? OsdWarn : OsdValue); }

        List<List<OsdSeg>> OsdLines(OsdSnapshot s)
        {
            var lines = new List<List<OsdSeg>>();
            int items = OsdSettings.Items;
            if (s == null)
            {
                lines.Add(new List<OsdSeg> { new OsdSeg("OSD  starting...", OsdDim) });
                return lines;
            }

            if ((items & OsdSettings.ItemFps) != 0)
            {
                var l = new List<OsdSeg> { new OsdSeg("FPS  ", OsdLabel) };
                if (s.Frames != null)
                {
                    l.Add(new OsdSeg(PadL(F(s.Frames.Fps, "0"), 4), OsdValue));
                    l.Add(new OsdSeg(PadL(F(s.Frames.FrameMs, "0.0"), 6) + " ms", OsdDim));
                    if (s.Frames.Low1 > 0) l.Add(new OsdSeg("  1% " + F(s.Frames.Low1, "0"), s.Frames.Low1 < s.Frames.Fps * 0.5 ? OsdWarn : OsdDim));
                }
                else l.Add(new OsdSeg(s.FpsError != null ? "n/a" : "   -", OsdDim));
                lines.Add(l);
            }
            if ((items & OsdSettings.ItemGpu) != 0)
            {
                var l = new List<OsdSeg> { new OsdSeg("GPU  ", OsdLabel) };
                l.Add(new OsdSeg(s.GpuLoad >= 0 ? PadL(F(s.GpuLoad, "0"), 3) + " %" : "  - %", OsdValue));
                if (s.GpuTemp > 0) l.Add(new OsdSeg(PadL(F(s.GpuTemp, "0"), 5) + Deg, TempColor(s.GpuTemp)));
                if (s.GpuPower >= 0) l.Add(new OsdSeg(PadL(F(s.GpuPower, "0"), 5) + " W", s.GpuPowerLimit > 0 && s.GpuPower >= s.GpuPowerLimit * 0.97 ? OsdWarn : OsdDim));
                lines.Add(l);
            }
            if ((items & OsdSettings.ItemVram) != 0)
            {
                var l = new List<OsdSeg> { new OsdSeg("VRAM ", OsdLabel) };
                if (s.VramTotal > 0) l.Add(new OsdSeg(Gb(s.VramUsed) + " / " + Gb(s.VramTotal) + " GB", PctColor(s.VramPct, 85, 95)));
                else l.Add(new OsdSeg("-", OsdDim));
                lines.Add(l);
            }
            if ((items & OsdSettings.ItemCpu) != 0)
            {
                var l = new List<OsdSeg> { new OsdSeg("CPU  ", OsdLabel) };
                l.Add(new OsdSeg(s.CpuLoad >= 0 ? PadL(F(s.CpuLoad, "0"), 3) + " %" : "  - %", OsdValue));
                if (s.CpuMaxCore >= 0) l.Add(new OsdSeg("  max " + PadL(F(s.CpuMaxCore, "0"), 3) + " %", PctColor(s.CpuMaxCore, 85, 95)));
                if (s.CpuTemp > 0) l.Add(new OsdSeg(PadL(F(s.CpuTemp, "0"), 5) + Deg, TempColor(s.CpuTemp)));
                lines.Add(l);
            }
            if ((items & OsdSettings.ItemRam) != 0)
            {
                var l = new List<OsdSeg> { new OsdSeg("RAM  ", OsdLabel) };
                if (s.RamTotal > 0) l.Add(new OsdSeg(Gb(s.RamUsed) + " / " + Gb(s.RamTotal) + " GB", PctColor(s.RamPct, 85, 92)));
                else l.Add(new OsdSeg("-", OsdDim));
                lines.Add(l);
            }
            if ((items & OsdSettings.ItemPcie) != 0)
            {
                var l = new List<OsdSeg> { new OsdSeg("PCIe ", OsdLabel) };
                if (s.PcieRx >= 0)
                {
                    l.Add(new OsdSeg(((char)0x2191).ToString() + F(s.PcieRx / 1e9, "0.00") + " " + ((char)0x2193).ToString() + F(s.PcieTx / 1e9, "0.00") + " GB/s", OsdValue));
                    double p = s.PciePct;
                    if (p >= 0) l.Add(new OsdSeg(PadL(F(p, "0"), 5) + " %", PctColor(p, 70, s.PcieMeasured ? 90 : 85)));
                }
                else l.Add(new OsdSeg(s.GpuNvidia ? "-" : "n/a (NVIDIA only)", OsdDim));
                lines.Add(l);
            }
            if ((items & OsdSettings.ItemLimit) != 0 && s.Verdict.Text.Length > 0)
            {
                Color c = s.Verdict.Level == 2 ? OsdBad : (s.Verdict.Level == 1 ? OsdWarn : (s.Verdict.Level == 0 ? OsdGood : OsdDim));
                lines.Add(new List<OsdSeg> { new OsdSeg("LIM  ", OsdLabel), new OsdSeg(s.Verdict.Text, c) });
            }
            if (lines.Count == 0) lines.Add(new List<OsdSeg> { new OsdSeg("OSD", OsdLabel) });
            return lines;
        }

        void UpdateOsdCards(OsdSnapshot s)
        {
            if (s == null)
            {
                lblOsdInfo.Text = FrameCounter.Error ?? "Starting sensors...";
                return;
            }
            string fpsSrc = s.Frames != null ? "FPS from " + s.Frames.Api + " presents" : (s.FpsError != null ? s.FpsError : "No frames from this app yet");
            lblOsdInfo.Text = "Measuring: " + (s.App.Length > 0 ? s.App + " (pid " + s.Pid + ")" : "-") + " on " + (s.GpuName.Length > 0 ? s.GpuName : "-") +
                "   |   " + fpsSrc + "   |   OSD hotkey Ctrl+Shift+O" + (osdHotkey ? "" : " (taken by another app)");

            if (s.Frames != null)
                cardFps.Set(F(s.Frames.Fps, "0"), F(s.Frames.FrameMs, "0.0") + " ms per frame" + Txt.NL +
                    (s.Frames.Low1 > 0 ? "1 % low " + F(s.Frames.Low1, "0") + " FPS" : "1 % low: collecting...") +
                    (s.RefreshHz > 0 ? Txt.NL + "Monitor " + s.RefreshHz + " Hz" : ""), Theme.Text);
            else cardFps.Set(Txt.NA, s.FpsError ?? ("Not presenting frames" + Txt.NL + "Bring the game to the front"), Theme.Text);

            if (s.GpuLoad >= 0 || s.GpuTemp > 0)
                cardGpu.Set((s.GpuLoad >= 0 ? F(s.GpuLoad, "0") : Txt.NA) + " %",
                    (s.GpuTemp > 0 ? F(s.GpuTemp, "0") + " " + Deg : "Temp n/a") +
                    (s.GpuPower >= 0 ? "   " + F(s.GpuPower, "0") + (s.GpuPowerLimit > 0 ? " / " + F(s.GpuPowerLimit, "0") : "") + " W" : "") + Txt.NL + s.GpuName,
                    s.GpuTemp >= 83 ? Theme.Red : Theme.Text);
            else cardGpu.Set(Txt.NA, s.GpuName, Theme.Text);

            if (s.VramTotal > 0)
                cardVram.Set(Gb(s.VramUsed) + " / " + Gb(s.VramTotal) + " GB", F(s.VramPct, "0") + " % used", s.VramPct >= 95 ? Theme.Red : (s.VramPct >= 85 ? Theme.Orange : Theme.Text));
            else cardVram.Set(Txt.NA, "", Theme.Text);

            if (s.CpuLoad >= 0)
                cardCpu.Set(F(s.CpuLoad, "0") + " %", "Busiest thread " + F(s.CpuMaxCore, "0") + " %" + Txt.NL +
                    (s.CpuTemp > 0 ? F(s.CpuTemp, "0") + " " + Deg + " (" + s.CpuTempSource + ")" : "Temp n/a: run LibreHardwareMonitor"),
                    s.CpuMaxCore >= 90 ? Theme.Orange : Theme.Text);
            else cardCpu.Set(Txt.NA, "", Theme.Text);

            if (s.RamTotal > 0)
                cardRam.Set(Gb(s.RamUsed) + " / " + Gb(s.RamTotal) + " GB", F(s.RamPct, "0") + " % used", s.RamPct >= 92 ? Theme.Orange : Theme.Text);
            else cardRam.Set(Txt.NA, "", Theme.Text);

            if (s.PcieRx >= 0)
            {
                double p = s.PciePct;
                cardPcie.Set(F(s.PcieRx / 1e9, "0.00") + " / " + F(s.PcieTx / 1e9, "0.00"), "GB/s host -> GPU / GPU -> host" + Txt.NL +
                    (p >= 0 ? F(p, "0") + " % of the " + (s.PcieMeasured ? "measured limit" : "theoretical link") : "") +
                    (s.PcieMeasured ? "" : Txt.NL + "Run PCIe bandwidth... for the real limit"),
                    p >= 90 ? Theme.Red : (p >= 70 ? Theme.Orange : Theme.Text));
            }
            else cardPcie.Set(Txt.NA, s.GpuNvidia ? "Waiting for NVML..." : "Needs an NVIDIA GPU (NVML)", Theme.Text);

            var v = s.Verdict;
            cardVerdict.Set(v.Text.Length > 0 ? v.Text : Txt.NA, v.Detail,
                v.Level == 2 ? Theme.Red : (v.Level == 1 ? Theme.Orange : (v.Level == 0 ? Theme.Green : Theme.Muted)));
        }
    }
}
