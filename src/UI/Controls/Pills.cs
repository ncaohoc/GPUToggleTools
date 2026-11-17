// Pill colors for the status / preference columns of the lists, per theme.

using System;
using System.Drawing;

namespace GpuToggle
{
    static class Pills
    {
        public static Color[] GpuStatus(string t)
        {
            if (Theme.Dark)
            {
                if (t == "Enabled") return new Color[] { Color.FromArgb(20, 83, 45), Color.FromArgb(134, 239, 172) };
                if (t == "Disabled") return new Color[] { Color.FromArgb(51, 65, 85), Color.FromArgb(148, 163, 184) };
                return new Color[] { Color.FromArgb(127, 29, 29), Color.FromArgb(252, 165, 165) };
            }
            if (t == "Enabled") return new Color[] { Color.FromArgb(220, 252, 231), Color.FromArgb(21, 128, 61) };
            if (t == "Disabled") return new Color[] { Color.FromArgb(241, 245, 249), Color.FromArgb(100, 116, 139) };
            return new Color[] { Color.FromArgb(254, 226, 226), Color.FromArgb(185, 28, 28) };
        }

        public static Color[] GpuPreference(string t)
        {
            if (Theme.Dark)
            {
                if (t.StartsWith("High", StringComparison.Ordinal)) return new Color[] { Color.FromArgb(30, 58, 138), Color.FromArgb(147, 197, 253) };
                if (t.StartsWith("Power", StringComparison.Ordinal)) return new Color[] { Color.FromArgb(120, 53, 15), Color.FromArgb(253, 230, 138) };
                return new Color[] { Color.FromArgb(51, 65, 85), Color.FromArgb(203, 213, 225) };
            }
            if (t.StartsWith("High", StringComparison.Ordinal)) return new Color[] { Color.FromArgb(219, 234, 254), Color.FromArgb(29, 78, 216) };
            if (t.StartsWith("Power", StringComparison.Ordinal)) return new Color[] { Color.FromArgb(254, 243, 199), Color.FromArgb(180, 83, 9) };
            return new Color[] { Color.FromArgb(241, 245, 249), Color.FromArgb(71, 85, 105) };
        }
    }
}
