// DPI scale of the UI.

using System;

namespace GpuToggle
{
    static class Ui
    {
        public static float Scale = 1f;
        public static int S(int v) { return (int)Math.Round(v * Scale); }
    }
}
