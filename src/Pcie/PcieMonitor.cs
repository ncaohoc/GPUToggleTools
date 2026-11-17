// Live PCIe monitor: samples NVML traffic counters 4 times a second while apps / games run
// and records bottleneck episodes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace GpuToggle
{
    class PcieMonitor
    {
        public const int IntervalMs = 250;
        public const int MaxSamples = 4 * 3600 * 24;   // 24 h at 4 samples/s
        public const double MinEpisodeSeconds = 0.5;

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
            if (!g.IsNvidia)
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
            bool have = BandwidthHistory.TryGetMeasured(Gpu.InstanceId, out r);
            List<PcieLink> chain = null;
            if (have) chain = r.BestChain;
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
                Foreground.Window(out pid);
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
    }
}
