// One GPU Toggle per session, and no UAC prompt when it is already running or can be started by the sign-in task.
// The exe starts with normal rights (app.manifest: asInvoker) and then:
//   - a copy is already running (e.g. hidden since sign-in): ask it to show its window, exit;
//   - "Start with Windows" is on: start the app through that task (it runs with highest privileges, no UAC), exit;
//   - otherwise: start itself again as administrator (one UAC prompt), exit.
// Only the elevated copy runs the app.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GpuToggle
{
    static class SingleInstance
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, int action, IntPtr info);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

        const string MutexName = "GPUToggle.SingleInstance";
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);
        public static readonly int ShowMessage = RegisterWindowMessage("GPUToggle.ShowWindow");
        static Mutex mutex;

        public static bool IsElevated
        {
            get
            {
                try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        static bool Running()
        {
            try
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(MutexName, out m)) return false;
                m.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return true; }   // made by the elevated copy
            catch { return false; }
        }

        static void ShowRunning()
        {
            AllowSetForegroundWindow(-1);   // ASFW_ANY: let the running copy bring its window to the front
            PostMessage(HWND_BROADCAST, ShowMessage, IntPtr.Zero, IntPtr.Zero);
        }

        // Started without admin rights: hand over to the running copy, the sign-in task or an elevated copy.
        public static void Launch(string[] args, bool startup)
        {
            if (Running()) { ShowRunning(); return; }
            if (startup) return;   // the sign-in task without admin rights: nothing useful to do

            // The task starts the app hidden: wait for its window, then ask it to show itself.
            string output;
            if (Shell.Run("schtasks.exe", "/Run /TN " + LogonTask.Name, 15000, out output) == 0)
            {
                AllowSetForegroundWindow(-1);
                string title = MainForm.WindowTitle;
                for (int i = 0; i < 100; i++)   // up to 20 s
                {
                    Thread.Sleep(200);
                    IntPtr hwnd = FindWindow(null, title);
                    if (hwnd == IntPtr.Zero) continue;
                    if (IsWindowVisible(hwnd)) return;
                    PostMessage(hwnd, ShowMessage, IntPtr.Zero, IntPtr.Zero);
                }
                return;
            }

            var sb = new StringBuilder();
            foreach (var a in args) sb.Append(Txt.Q).Append(a).Append(Txt.Q).Append(' ');
            var psi = new ProcessStartInfo(Application.ExecutablePath, sb.ToString().Trim());
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            try { Process.Start(psi); }
            catch (Win32Exception) { }   // UAC prompt cancelled
        }

        // True if this is the first elevated copy; otherwise the first copy is told to show itself.
        public static bool Claim()
        {
            bool created;
            mutex = new Mutex(true, MutexName, out created);
            if (!created) ShowRunning();
            return created;
        }

        // The window runs elevated: let normal-rights copies (see Launch) send it the show message.
        public static void AllowShowMessage(IntPtr hwnd)
        {
            try { ChangeWindowMessageFilterEx(hwnd, ShowMessage, 1, IntPtr.Zero); } catch { }   // MSGFLT_ALLOW
        }
    }
}
