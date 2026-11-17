// One PCIe link (current / maximum generation and width) and its theoretical bandwidth.

using System;

namespace GpuToggle
{
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
}
