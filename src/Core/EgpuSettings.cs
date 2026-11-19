// Which GPUs the user marked as eGPU (and whether to enable them by themselves), plus the eGPU options.
// Stored in HKCU/Software/GPUToggle/eGPU: one DWORD per GPU, named by its instance ID.
// The last part of an instance ID depends on the port, so a GPU plugged into another port is still found
// by its hardware part (VEN / DEV / SUBSYS / REV).

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace GpuToggle
{
    static class EgpuSettings
    {
        public const int Egpu = 1, Auto = 2;

        static readonly string Key = Txt.RegPath("Software", "GPUToggle", "eGPU");

        static Dictionary<string, int> Load()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k == null) return d;
                    foreach (var n in k.GetValueNames())
                    {
                        object v = k.GetValue(n);
                        if (v is int && (int)v != 0) d[n] = (int)v;
                    }
                }
            }
            catch { }
            return d;
        }

        // Instance ID without the port-dependent last part.
        static string HardwarePart(string instanceId)
        {
            int i = (instanceId ?? "").LastIndexOf(Txt.BS);
            return i > 0 ? instanceId.Substring(0, i) : instanceId;
        }

        public static int Flags(GpuInfo g)
        {
            if (g == null || !g.IsPhysical) return 0;
            var d = Load();
            int f;
            if (d.TryGetValue(g.InstanceId, out f)) return f;
            string hw = HardwarePart(g.InstanceId);
            foreach (var kv in d)
                if (string.Equals(HardwarePart(kv.Key), hw, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return 0;
        }

        public static bool IsEgpu(GpuInfo g) { return (Flags(g) & Egpu) != 0; }

        public static void SetFlags(GpuInfo g, int flags)
        {
            if (g == null) return;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    // Drop the entries of the same card on other ports, then write this one.
                    string hw = HardwarePart(g.InstanceId);
                    foreach (var n in k.GetValueNames())
                        if (string.Equals(HardwarePart(n), hw, StringComparison.OrdinalIgnoreCase)) k.DeleteValue(n, false);
                    if (flags != 0) k.SetValue(g.InstanceId, flags, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        // Disable the marked eGPUs when Windows shuts down or restarts (needs the app running, see LogonTask).
        public static bool DisableOnShutdown
        {
            get { return GetBool("eGpuDisableOnShutdown", true); }
            set { SetBool("eGpuDisableOnShutdown", value); }
        }

        static bool GetBool(string name, bool def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(AppSettings.KeyPath))
                {
                    object v = k != null ? k.GetValue(name) : null;
                    return v is int ? (int)v != 0 : def;
                }
            }
            catch { return def; }
        }

        static void SetBool(string name, bool on)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(AppSettings.KeyPath)) k.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord); } catch { }
        }
    }
}
