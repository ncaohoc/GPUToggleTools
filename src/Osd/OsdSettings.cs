// OSD settings (HKCU/Software/GPUToggle, next to the theme).

using System;
using Microsoft.Win32;

namespace GpuToggle
{
    static class OsdSettings
    {
        public const int ItemFps = 1, ItemGpu = 2, ItemVram = 4, ItemCpu = 8, ItemRam = 16, ItemPcie = 32, ItemLimit = 64;
        public static int Corner = 0, TextSize = 1, Alpha = 70, Items = 127;
        public static int Graphs = ItemFps | ItemPcie;   // which rows also draw a small graph (same bits as Items)
        public static string Gpu = "auto";

        public static void Load()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(AppSettings.KeyPath))
                {
                    if (k == null) return;
                    Corner = Clamp(k.GetValue("OsdCorner"), 0, 3, Corner);
                    TextSize = Clamp(k.GetValue("OsdSize"), 0, 2, TextSize);
                    Alpha = Clamp(k.GetValue("OsdBackground"), 0, 100, Alpha);
                    Items = Clamp(k.GetValue("OsdItems"), 0, 127, Items);
                    Graphs = Clamp(k.GetValue("OsdGraphs"), 0, 63, Graphs);
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
                using (var k = Registry.CurrentUser.CreateSubKey(AppSettings.KeyPath))
                {
                    k.SetValue("OsdCorner", Corner, RegistryValueKind.DWord);
                    k.SetValue("OsdSize", TextSize, RegistryValueKind.DWord);
                    k.SetValue("OsdBackground", Alpha, RegistryValueKind.DWord);
                    k.SetValue("OsdItems", Items, RegistryValueKind.DWord);
                    k.SetValue("OsdGraphs", Graphs, RegistryValueKind.DWord);
                    k.SetValue("OsdGpu", Gpu, RegistryValueKind.String);
                }
            }
            catch { }
        }
    }
}
