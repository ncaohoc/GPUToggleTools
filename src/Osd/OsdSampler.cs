// Background sampler of the OSD: reads all sensors 10 times a second and publishes a snapshot
// every 0.5 s for the game in front.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace GpuToggle
{
    class OsdSampler
    {
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
        public const int PcieGraphPoints = 20;
        readonly float[] pcieRxHist = NewHist(), pcieTxHist = NewHist();
        readonly float[] gpuHist = NewHist(), vramHist = NewHist(), cpuHist = NewHist(), cpuMaxHist = NewHist(), ramHist = NewHist();
        readonly BottleneckJudge judge = new BottleneckJudge();
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
            judge.Clear();
            ClearGpuHist();
            ClearHist(cpuHist); ClearHist(cpuMaxHist); ClearHist(ramHist);
        }

        void ClearGpuHist() { ClearHist(pcieRxHist); ClearHist(pcieTxHist); ClearHist(gpuHist); ClearHist(vramHist); }

        static float[] NewHist() { var a = new float[PcieGraphPoints]; ClearHist(a); return a; }
        static void ClearHist(float[] a) { for (int i = 0; i < a.Length; i++) a[i] = -1; }
        static float[] Push(float[] a, double v)
        {
            Array.Copy(a, 1, a, 0, a.Length - 1);
            a[a.Length - 1] = (float)v;
            return (float[])a.Clone();
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
            foreach (var g in list) if (g.IsNvidia) return g;
            return list.Count > 0 ? list[0] : null;
        }

        IntPtr NvmlHandle(GpuInfo g)
        {
            if (g == null || !g.IsNvidia) return IntPtr.Zero;
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
            if (now != gpu) { sumRx = sumTx = 0; nPcie = 0; gpu = now; h = NvmlHandle(gpu); ClearGpuHist(); }
            if (gpu != null)
            {
                s.GpuName = gpu.Name;
                s.GpuNvidia = gpu.IsNvidia;
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
                else if (gpu.VenId != PciVendor.Intel) s.GpuTemp = lhmGpuTemp;   // AMD: only if LibreHardwareMonitor runs
                PcieRefs(gpu, h, tick, s);
            }
            if (nPcie > 0) { s.PcieRx = sumRx / nPcie; s.PcieTx = sumTx / nPcie; }
            s.PcieRxGraph = Push(pcieRxHist, s.PcieRx);
            s.PcieTxGraph = Push(pcieTxHist, s.PcieTx);
            s.GpuGraph = Push(gpuHist, s.GpuLoad);
            s.VramGraph = Push(vramHist, s.VramPct);
            s.CpuGraph = Push(cpuHist, s.CpuLoad);
            s.CpuMaxGraph = Push(cpuMaxHist, s.CpuMaxCore);
            s.RamGraph = Push(ramHist, s.RamPct);
            sumRx = sumTx = 0;
            nPcie = 0;

            s.Frames = FrameCounter.Running ? FrameCounter.Get(s.Pid) : null;
            s.FpsError = FrameCounter.Error;
            s.RefreshHz = RefreshHz(s.Hwnd);
            s.Verdict = judge.Judge(s);
            lock (snapLock) latest = s;
        }

        // The window in front is the game. When it is this app (looking at the OSD page), keep the last game.
        void ForegroundGame(OsdSnapshot s)
        {
            uint pid;
            IntPtr w = Foreground.Window(out pid);
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
                    if (BandwidthHistory.TryGetMeasured(g.InstanceId, out r))
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
    }
}
