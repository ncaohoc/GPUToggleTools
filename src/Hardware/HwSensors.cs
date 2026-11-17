// CPU temperature (and AMD GPU temperature) from LibreHardwareMonitor / OpenHardwareMonitor or ACPI.

using System;
using System.Diagnostics;
using System.Management;

namespace GpuToggle
{
    static class HwSensors
    {
        static readonly string[] Namespaces = { "root/LibreHardwareMonitor", "root/OpenHardwareMonitor" };
        static readonly string[] CpuNames = { "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "CPU (Tctl/Tdie)", "Tctl", "Core Average", "CPU Cores" };
        static DateTime[] retry = new DateTime[2];

        public static float Read(out string source, out float gpuTemp)
        {
            source = "";
            gpuTemp = -1;
            for (int n = 0; n < Namespaces.Length; n++)
            {
                if (DateTime.Now < retry[n]) continue;
                try
                {
                    float cpu = -1, cpuAny = -1;
                    int rank = int.MaxValue;
                    using (var s = new ManagementObjectSearcher(Namespaces[n], "SELECT Name, Value, Parent FROM Sensor WHERE SensorType='Temperature'"))
                    {
                        foreach (ManagementObject mo in s.Get())
                        {
                            string name = Convert.ToString(mo["Name"]), parent = Convert.ToString(mo["Parent"]).ToLowerInvariant();
                            float v = Convert.ToSingle(mo["Value"]);
                            if (v <= 0 || v > 150) continue;
                            if (parent.Contains("cpu"))
                            {
                                int r = Array.IndexOf(CpuNames, name);
                                if (r >= 0 && r < rank) { rank = r; cpu = v; }
                                cpuAny = Math.Max(cpuAny, v);
                            }
                            else if (parent.Contains("gpu") && (name == "GPU Core" || (gpuTemp < 0 && name.StartsWith("GPU", StringComparison.Ordinal)))) gpuTemp = v;
                        }
                    }
                    if (cpu < 0) cpu = cpuAny;
                    if (cpu > 0) { source = n == 0 ? "LibreHardwareMonitor" : "OpenHardwareMonitor"; return cpu; }
                }
                catch { retry[n] = DateTime.Now.AddSeconds(30); }   // namespace missing = the tool is not running
            }
            try
            {
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                float best = -1;
                foreach (InstanceData d in cat.ReadCategory()["High Precision Temperature"].Values)
                    best = Math.Max(best, d.RawValue / 10f - 273.15f);     // tenths of a kelvin
                if (best > 5 && best < 150) { source = "ACPI thermal zone"; return best; }
            }
            catch { }
            return -1;
        }
    }
}
