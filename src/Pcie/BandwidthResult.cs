// Result of a bandwidth test, and the latest successful result per GPU.

using System;
using System.Collections.Generic;

namespace GpuToggle
{
    class BandwidthResult
    {
        public string Error;
        public double UploadGBs, DownloadGBs;     // host -> GPU, GPU -> host (best of several runs)
        public List<PcieLink> Chain = new List<PcieLink>();       // links at idle
        public List<PcieLink> ChainLoad;                           // links sampled while the test was running
        public string Tunnel;                                      // USB4 / Thunderbolt controller above the GPU, if any
        public double NvmlRxPeak, NvmlTxPeak;                      // NVML counter peaks (bytes/s) during the test (informational)
        public bool Integrated;

        // Links under load if they were read, else the idle ones (GPUs lower the link speed when idle).
        public List<PcieLink> BestChain
        {
            get { return ChainLoad != null && ChainLoad.Count > 0 && ChainLoad[0] != null ? ChainLoad : Chain; }
        }

        public bool Measured { get { return Error == null && UploadGBs > 0 && DownloadGBs > 0; } }
    }

    // Latest successful test per GPU instance id. The PCIe monitor and the OSD use it as the link capacity.
    static class BandwidthHistory
    {
        static readonly object Lock = new object();
        static readonly Dictionary<string, BandwidthResult> last = new Dictionary<string, BandwidthResult>();

        public static void Save(string instanceId, BandwidthResult r)
        {
            lock (Lock) last[instanceId] = r;
        }

        public static bool TryGetMeasured(string instanceId, out BandwidthResult r)
        {
            lock (Lock) return last.TryGetValue(instanceId, out r) && r.Measured;
        }
    }
}
