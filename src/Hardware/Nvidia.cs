// NVIDIA extras for the GPUs page: nvidia-smi (temperature, power, fan %) and the NVAPI fan tachometer.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GpuToggle
{
    class NvSmi { public float Temp = -1, Power = -1, Limit = -1, FanPct = -1, MemUsed = -1, MemTotal = -1, Util = -1; }

    static class Nvidia
    {
        public static NvSmi Query()
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi.exe",
                    "--query-gpu=temperature.gpu,power.draw,power.limit,fan.speed,memory.used,memory.total,utilization.gpu --format=csv,noheader,nounits");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    string line = p.StandardOutput.ReadLine();
                    p.WaitForExit(3000);
                    if (string.IsNullOrEmpty(line)) return null;
                    string[] f = line.Split(',');
                    if (f.Length < 7) return null;
                    var r = new NvSmi();
                    r.Temp = Num(f[0]); r.Power = Num(f[1]); r.Limit = Num(f[2]); r.FanPct = Num(f[3]);
                    r.MemUsed = Num(f[4]); r.MemTotal = Num(f[5]); r.Util = Num(f[6]);
                    return r;
                }
            }
            catch { return null; }
        }

        static float Num(string s)
        {
            float v;
            return float.TryParse(s.Trim(), NumberStyles.Float, Txt.Inv, out v) ? v : -1;
        }

        // ---- NVAPI fan tachometer (RPM)
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr NvQuery(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumFn([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] IntPtr[] handles, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TachFn(IntPtr handle, out uint rpm);

        static bool nvapiTried, nvapiOk;
        static EnumFn enumGpus;
        static TachFn tach;

        static T Fn<T>(uint id) where T : class
        {
            IntPtr p = NvQuery(id);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        // Returns RPM, or -1 if unavailable
        public static int FanRpm()
        {
            try
            {
                if (!nvapiTried)
                {
                    nvapiTried = true;
                    var init = Fn<InitFn>(0x0150E828);
                    enumGpus = Fn<EnumFn>(0xE5AC921F);
                    tach = Fn<TachFn>(0x5F608315);
                    nvapiOk = init != null && enumGpus != null && tach != null && init() == 0;
                }
                if (!nvapiOk) return -1;
                var h = new IntPtr[64];
                int n;
                if (enumGpus(h, out n) != 0 || n < 1) return -1;
                uint rpm;
                return tach(h[0], out rpm) == 0 ? (int)rpm : -1;
            }
            catch { nvapiOk = false; return -1; }
        }
    }
}
