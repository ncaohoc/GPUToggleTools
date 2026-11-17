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
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.Scale = g.DpiX / 96f;
            Theme.Init();
            Theme.LoadMode();
            Application.Run(new MainForm());
        }
    }
}
