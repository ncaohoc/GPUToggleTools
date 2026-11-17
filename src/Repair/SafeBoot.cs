// Restarts once into Safe Mode, where no GPU driver is loaded (the cleanest way to remove one).
// Instead of setting safeboot on the normal boot entry (which keeps booting into Safe Mode until it is
// cleared), it copies the entry, marks the copy as Safe Mode, hides it from the boot menu and boots it
// once with bcdedit /bootsequence. The restart after that is a normal one by itself. The copy is
// deleted the next time the app starts in normal mode.

using System;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GpuToggle
{
    static class SafeBoot
    {
        const string ValueName = "SafeBootEntry";

        public static bool InSafeMode { get { return SystemInformation.BootMode != BootMode.Normal; } }

        // Prepares the one-time Safe Mode boot. Returns null on success, otherwise the error.
        public static string Prepare()
        {
            string output;
            int code = Shell.Run("bcdedit.exe", "/copy {current} /d " + Txt.Q + "Safe Mode (GPU Toggle)" + Txt.Q, out output);
            var m = Regex.Match(output ?? "", "[{][0-9a-fA-F-]{36}[}]");
            if (code != 0 || !m.Success) return "bcdedit /copy failed: " + Shell.LastLine(output);
            string id = m.Value;
            Save(id);
            code = Shell.Run("bcdedit.exe", "/set " + id + " safeboot minimal", out output);
            if (code != 0) { Cleanup(); return "bcdedit /set safeboot failed: " + Shell.LastLine(output); }
            Shell.Run("bcdedit.exe", "/displayorder " + id + " /remove", out output);
            code = Shell.Run("bcdedit.exe", "/bootsequence " + id, out output);
            if (code != 0) { Cleanup(); return "bcdedit /bootsequence failed: " + Shell.LastLine(output); }
            return null;
        }

        public static void Restart()
        {
            string output;
            Shell.Run("shutdown.exe", "/r /t 3", out output);
        }

        // Removes the Safe Mode boot entry left by Prepare (only in normal mode: in Safe Mode it is the running one).
        public static void Cleanup()
        {
            string id = Load();
            if (id == null || InSafeMode) return;
            string output;
            Shell.Run("bcdedit.exe", "/delete " + id, 20000, out output);
            Save(null);
        }

        static string Load()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(Txt.RegPath("SOFTWARE", "GPUToggle")))
                {
                    string v = k != null ? k.GetValue(ValueName) as string : null;
                    return v != null && Regex.IsMatch(v, "^[{][0-9a-fA-F-]{36}[}]$") ? v : null;
                }
            }
            catch { return null; }
        }

        static void Save(string id)
        {
            try
            {
                using (var k = Registry.LocalMachine.CreateSubKey(Txt.RegPath("SOFTWARE", "GPUToggle")))
                {
                    if (id == null) k.DeleteValue(ValueName, false);
                    else k.SetValue(ValueName, id, RegistryValueKind.String);
                }
            }
            catch { }
        }
    }
}
