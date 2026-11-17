// NVML (nvml.dll from the NVIDIA driver): live PCIe traffic counters, link, load, temperature, power, VRAM.

using System;
using System.Runtime.InteropServices;

namespace GpuToggle
{
    static class Nvml
    {
        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPciInfo_v3(IntPtr device, IntPtr pci);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPcieThroughput(IntPtr device, int counter, out uint kbPerSec);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCurrPcieLinkGeneration(IntPtr device, out uint gen);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr device, out uint width);

        static bool tried, ok;

        static bool Init()
        {
            if (!tried)
            {
                tried = true;
                try { ok = nvmlInit_v2() == 0; } catch { ok = false; }
            }
            return ok;
        }

        // Handle of the NVIDIA GPU with this PCI vendor/device id, or IntPtr.Zero.
        public static IntPtr Find(uint ven, uint dev)
        {
            if (!Init()) return IntPtr.Zero;
            uint n;
            if (nvmlDeviceGetCount_v2(out n) != 0) return IntPtr.Zero;
            IntPtr buf = Marshal.AllocHGlobal(256);
            try
            {
                for (uint i = 0; i < n; i++)
                {
                    IntPtr h;
                    if (nvmlDeviceGetHandleByIndex_v2(i, out h) != 0) continue;
                    for (int k = 0; k < 256; k++) Marshal.WriteByte(buf, k, 0);
                    if (nvmlDeviceGetPciInfo_v3(h, buf) != 0) continue;
                    uint id = (uint)Marshal.ReadInt32(buf, 28);   // pciDeviceId = device << 16 | vendor
                    if (id == ((dev << 16) | ven)) return h;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return IntPtr.Zero;
        }

        // Bytes per second over the last ~20 ms. rx = host -> GPU, tx = GPU -> host. False if unsupported.
        public static bool Throughput(IntPtr h, out double rx, out double tx)
        {
            uint r, t;
            rx = tx = 0;
            if (nvmlDeviceGetPcieThroughput(h, 1, out r) != 0 || nvmlDeviceGetPcieThroughput(h, 0, out t) != 0) return false;
            rx = r * 1024.0;
            tx = t * 1024.0;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)] struct NvmlUtil { public uint Gpu, Memory; }
        [StructLayout(LayoutKind.Sequential)] struct NvmlMem { public ulong Total, Free, Used; }
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtil util);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMem mem);

        // Load %, temperature C, power W, power limit W, VRAM used / total bytes. -1 where unsupported.
        public static void Basic(IntPtr h, out float load, out float temp, out float watts, out float limit, out double used, out double total)
        {
            load = temp = watts = limit = -1;
            used = total = -1;
            NvmlUtil u; uint t, p; NvmlMem m;
            if (nvmlDeviceGetUtilizationRates(h, out u) == 0) load = u.Gpu;
            if (nvmlDeviceGetTemperature(h, 0, out t) == 0) temp = t;
            if (nvmlDeviceGetPowerUsage(h, out p) == 0) watts = p / 1000f;
            if (nvmlDeviceGetEnforcedPowerLimit(h, out p) == 0) limit = p / 1000f;
            if (nvmlDeviceGetMemoryInfo(h, out m) == 0 && m.Total > 0) { used = m.Used; total = m.Total; }
        }

        public static string Link(IntPtr h, out int gen, out int width)
        {
            uint g, w;
            gen = width = 0;
            if (nvmlDeviceGetCurrPcieLinkGeneration(h, out g) != 0 || nvmlDeviceGetCurrPcieLinkWidth(h, out w) != 0) return Txt.NA;
            gen = (int)g; width = (int)w;
            return "Gen" + g + " x" + w;
        }
    }
}
