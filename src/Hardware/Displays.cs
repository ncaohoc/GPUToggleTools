// Which monitor is driven by which GPU, and display refresh rates.

using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;

namespace GpuToggle
{
    class DisplayInfo
    {
        public string Output, Monitor, Mode, GpuName, GpuDeviceId;
        public bool Primary;
    }

    static class Displays
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

        const int ATTACHED = 0x1, PRIMARY = 0x4;

        // Refresh rate of a display (e.g. Screen.DeviceName), or 0.
        public static int RefreshHz(string device)
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return EnumDisplaySettings(device, -1, ref dm) ? dm.dmDisplayFrequency : 0;
        }

        public static List<DisplayInfo> List()
        {
            var names = MonitorNames();
            var list = new List<DisplayInfo>();
            for (uint i = 0; i < 32; i++)
            {
                var ad = new DISPLAY_DEVICE();
                ad.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(null, i, ref ad, 0)) break;
                if ((ad.StateFlags & ATTACHED) == 0) continue;

                var di = new DisplayInfo();
                int k = ad.DeviceName.IndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
                di.Output = k >= 0 ? ad.DeviceName.Substring(k) : ad.DeviceName;
                di.Primary = (ad.StateFlags & PRIMARY) != 0;
                di.GpuName = ad.DeviceString;
                di.GpuDeviceId = ad.DeviceID;

                var mon = new DISPLAY_DEVICE();
                mon.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                di.Monitor = "Unknown monitor";
                if (EnumDisplayDevices(ad.DeviceName, 0, ref mon, 1)) // EDD_GET_DEVICE_INTERFACE_NAME
                {
                    di.Monitor = mon.DeviceString;
                    string key = MonitorKey(mon.DeviceID);
                    if (key != null)
                        foreach (var kv in names)
                            if (kv.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase)) { di.Monitor = kv.Value; break; }
                }

                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(ad.DeviceName, -1, ref dm)) // ENUM_CURRENT_SETTINGS
                    di.Mode = dm.dmPelsWidth + " x " + dm.dmPelsHeight + " @ " + dm.dmDisplayFrequency + " Hz";
                else
                    di.Mode = Txt.NA;
                list.Add(di);
            }
            return list;
        }

        // Interface path like  ??DISPLAY#GSM5B7F#5&abc&0&UID4352#{guid}  ->  DISPLAY<bs>GSM5B7F<bs>5&abc&0&UID4352
        static string MonitorKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int i = path.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase);
            int j = path.IndexOf("#{", StringComparison.Ordinal);
            if (i < 0 || j <= i) return null;
            return path.Substring(i, j - i).Replace('#', Txt.BS);
        }

        // WmiMonitorID: InstanceName -> friendly name from EDID
        static Dictionary<string, string> MonitorNames()
        {
            var d = new Dictionary<string, string>();
            try
            {
                using (var s = new ManagementObjectSearcher("root/wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        var raw = mo["UserFriendlyName"] as ushort[];
                        if (raw == null) continue;
                        var chars = new List<char>();
                        foreach (var c in raw) if (c != 0) chars.Add((char)c);
                        string name = new string(chars.ToArray()).Trim();
                        if (name.Length > 0) d[Convert.ToString(mo["InstanceName"])] = name;
                    }
                }
            }
            catch { }
            return d;
        }
    }
}
