// Everything the OSD shows at one moment, plus the bottleneck verdict.

using System;

namespace GpuToggle
{
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
        public float[] PcieRxGraph, PcieTxGraph;        // last ~10 s, one point per snapshot (oldest first, -1 = no data)
        public float[] GpuGraph, VramGraph, CpuGraph, CpuMaxGraph, RamGraph;
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
}
