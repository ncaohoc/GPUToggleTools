// The window in front (the game, for the PCIe monitor and the OSD).

using System;
using System.Runtime.InteropServices;

namespace GpuToggle
{
    static class Foreground
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        // Foreground window and its process id (0 when there is none).
        public static IntPtr Window(out uint pid)
        {
            IntPtr w = GetForegroundWindow();
            pid = 0;
            GetWindowThreadProcessId(w, out pid);
            return w;
        }
    }
}
