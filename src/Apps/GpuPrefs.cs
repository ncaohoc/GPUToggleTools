// Per-app GPU preference, stored where Windows Graphics settings keeps it
// (HKCU/Software/Microsoft/DirectX/UserGpuPreferences).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace GpuToggle
{
    class AppEntry
    {
        public string Path;
        public string Name;
        public int Pref;
    }

    static class GpuPrefs
    {
        static string KeyPath
        {
            get { return Txt.RegPath("Software", "Microsoft", "DirectX", "UserGpuPreferences"); }
        }

        public static List<AppEntry> Load()
        {
            var list = new List<AppEntry>();
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (k == null) return list;
                foreach (string name in k.GetValueNames())
                {
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var e = new AppEntry();
                    e.Path = name;
                    e.Name = AppName(name);
                    e.Pref = ParsePref(Convert.ToString(k.GetValue(name)));
                    list.Add(e);
                }
            }
            list.Sort(delegate (AppEntry a, AppEntry b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        public static string AppName(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrEmpty(vi.FileDescription) && vi.FileDescription.Trim().Length > 0)
                        return vi.FileDescription.Trim();
                }
            }
            catch { }
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        public static int ParsePref(string data)
        {
            if (string.IsNullOrEmpty(data)) return 0;
            foreach (var part in data.Split(';'))
            {
                var t = part.Trim();
                if (!t.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)) continue;
                int v;
                if (int.TryParse(t.Substring("GpuPreference=".Length), NumberStyles.Integer, Txt.Inv, out v)) return v;
            }
            return 0;
        }

        // Adds the app with "Let Windows decide" if it is not in the list yet.
        public static void Add(string path)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (k.GetValue(path) == null) k.SetValue(path, "GpuPreference=0;", RegistryValueKind.String);
            }
        }

        // Rewrites GpuPreference=N; and keeps any other key=value; parts Windows stored.
        public static void Set(string path, int pref)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                string old = Convert.ToString(k.GetValue(path));
                var sb = new StringBuilder("GpuPreference=" + pref.ToString(Txt.Inv) + ";");
                foreach (var part in old.Split(';'))
                {
                    var t = part.Trim();
                    if (t.Length == 0 || t.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(t).Append(';');
                }
                k.SetValue(path, sb.ToString(), RegistryValueKind.String);
            }
        }

        public static void Remove(string path)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
            {
                if (k != null) k.DeleteValue(path, false);
            }
        }

        public static string PrefName(int pref)
        {
            if (pref == 1) return "Power saving";
            if (pref == 2) return "High performance";
            return "Let Windows decide";
        }
    }
}
