// GpuToggle - WinForms app to enable / disable GPUs (eGPU / iGPU) and monitor them.
// Build with build.bat (uses the C# compiler that ships with Windows, C# 5 syntax).

using System;
using System.Drawing;
using System.Windows.Forms;

namespace GpuToggle
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool startup = false;
            foreach (var a in args) if (string.Equals(a, LogonTask.StartupArg, StringComparison.OrdinalIgnoreCase)) startup = true;

            // Started with normal rights: show the running copy, or start through the sign-in task (no UAC), or ask for admin.
            if (!SingleInstance.IsElevated) { SingleInstance.Launch(args, startup); return; }
            // Already running (e.g. hidden since sign-in): show that window instead.
            if (!SingleInstance.Claim()) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.Scale = g.DpiX / 96f;
            Theme.Init();
            Theme.LoadMode();
            Application.Run(new MainForm(startup));
        }
    }
}
