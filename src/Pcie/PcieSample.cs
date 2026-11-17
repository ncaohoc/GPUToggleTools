// Data recorded by the live PCIe monitor.

using System;

namespace GpuToggle
{
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
}
