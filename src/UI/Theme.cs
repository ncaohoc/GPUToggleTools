// Theme: the light / dark palette, the saved mode and the fonts.

using System;
using System.Drawing;
using Microsoft.Win32;

namespace GpuToggle
{
    static partial class Theme
    {
        // Palette. Not readonly: SetDark swaps it and Retheme recolors the open windows.
        public static Color Header, HeaderSel, HeaderHover, HeaderMuted, Surface, Card, Border, Text, Muted,
            Accent, Green, Red, Orange, RowAlt, RowSel;
        public static bool Dark;
        public static string Mode = "system";   // "system", "light" or "dark" (saved in HKCU/Software/GPUToggle)

        static Color[] Palette()
        {
            return new Color[] { Header, HeaderSel, HeaderHover, HeaderMuted, Surface, Card, Border, Text, Muted, Accent, Green, Red, Orange, RowAlt, RowSel };
        }

        public static void SetDark(bool dark)
        {
            Dark = dark;
            Green = Color.FromArgb(22, 163, 74);
            Red = Color.FromArgb(220, 38, 38);
            Orange = Color.FromArgb(234, 88, 12);
            HeaderMuted = Color.FromArgb(148, 163, 184);
            if (!dark)
            {
                Header = Color.FromArgb(15, 27, 51);
                HeaderSel = Color.FromArgb(34, 52, 92);
                HeaderHover = Color.FromArgb(26, 41, 74);
                Surface = Color.FromArgb(243, 246, 250);
                Card = Color.White;
                Border = Color.FromArgb(226, 232, 240);
                Text = Color.FromArgb(30, 41, 59);
                Muted = Color.FromArgb(100, 116, 139);
                Accent = Color.FromArgb(37, 99, 235);
                RowAlt = Color.FromArgb(248, 250, 252);
                RowSel = Color.FromArgb(219, 234, 254);
            }
            else
            {
                Header = Color.FromArgb(8, 13, 28);
                HeaderSel = Color.FromArgb(37, 52, 82);
                HeaderHover = Color.FromArgb(24, 34, 56);
                Surface = Color.FromArgb(17, 24, 39);
                Card = Color.FromArgb(30, 39, 56);
                Border = Color.FromArgb(55, 67, 89);
                Text = Color.FromArgb(226, 232, 240);
                Muted = Color.FromArgb(140, 154, 176);   // must differ from HeaderMuted (palette swap maps by value)
                Accent = Color.FromArgb(77, 140, 255);
                RowAlt = Color.FromArgb(36, 46, 65);
                RowSel = Color.FromArgb(32, 62, 116);
            }
        }

        public static bool SystemDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Txt.RegPath("Software", "Microsoft", "Windows", "CurrentVersion", "Themes", "Personalize")))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static bool WantDark { get { return Mode == "dark" || (Mode == "system" && SystemDark()); } }

        public static void LoadMode()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(AppSettings.KeyPath))
                {
                    string v = k != null ? k.GetValue("Theme") as string : null;
                    if (v == "light" || v == "dark" || v == "system") Mode = v;
                }
            }
            catch { }
            SetDark(WantDark);
        }

        public static void SaveMode()
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(AppSettings.KeyPath)) k.SetValue("Theme", Mode, RegistryValueKind.String); } catch { }
        }

        public static event Action Changed;

        public static Font Regular, Semibold, SmallBold, Small, Title;

        public static void Init()
        {
            Regular = new Font("Segoe UI", 9.5f);
            Semibold = new Font("Segoe UI Semibold", 9.5f);
            SmallBold = new Font("Segoe UI Semibold", 8.5f);
            Small = new Font("Segoe UI", 8.5f);
            Title = new Font("Segoe UI Semibold", 15f);
        }
    }
}
