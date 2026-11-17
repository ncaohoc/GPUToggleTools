// Lists the third-party driver packages by reading C:/Windows/INF/oem*.inf directly (pnputil's text
// output is localized). The store folder name comes from HKLM/SYSTEM/DriverDatabase/DriverInfFiles.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace GpuToggle
{
    static class DriverStore
    {
        public static List<DriverPackage> List()
        {
            var result = new List<DriverPackage>();
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            string[] files;
            try { files = Directory.GetFiles(dir, "oem*.inf"); }
            catch { return result; }
            Dictionary<long, List<string>> repo = null;
            foreach (string f in files)
            {
                try
                {
                    var p = Read(f);
                    if (p.StoreFolder == null)
                    {
                        if (repo == null) repo = RepositoryInfs();
                        FindStoreFolder(p, f, repo);
                    }
                    result.Add(p);
                }
                catch { }
            }
            return result;
        }

        // Size -> INF files of the driver store (FileRepository/<name>.inf_<arch>_<hash>/<name>.inf).
        static Dictionary<long, List<string>> RepositoryInfs()
        {
            var map = new Dictionary<long, List<string>>();
            string repo = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore"), "FileRepository");
            try
            {
                foreach (string d in Directory.GetDirectories(repo))
                {
                    string folder = Path.GetFileName(d);
                    int i = folder.IndexOf(".inf_", StringComparison.OrdinalIgnoreCase);
                    if (i <= 0) continue;
                    string inf = Path.Combine(d, folder.Substring(0, i + 4));
                    try
                    {
                        var fi = new FileInfo(inf);
                        if (!fi.Exists) continue;
                        List<string> l;
                        if (!map.TryGetValue(fi.Length, out l)) { l = new List<string>(); map[fi.Length] = l; }
                        l.Add(inf);
                    }
                    catch { }
                }
            }
            catch { }
            return map;
        }

        // Newer packages may be missing from DriverDatabase: oemNN.inf is a byte copy of the INF in its store folder.
        static void FindStoreFolder(DriverPackage p, string oemPath, Dictionary<long, List<string>> repo)
        {
            List<string> candidates;
            long len = new FileInfo(oemPath).Length;
            if (!repo.TryGetValue(len, out candidates)) return;
            byte[] mine = File.ReadAllBytes(oemPath);
            foreach (string c in candidates)
            {
                try
                {
                    byte[] other = File.ReadAllBytes(c);
                    bool same = other.Length == mine.Length;
                    for (int i = 0; same && i < mine.Length; i++) same = mine[i] == other[i];
                    if (!same) continue;
                    p.StoreFolder = Path.GetFileName(Path.GetDirectoryName(c));
                    p.OriginalName = Path.GetFileName(c).ToLowerInvariant();
                    return;
                }
                catch { }
            }
        }

        static DriverPackage Read(string path)
        {
            var p = new DriverPackage();
            p.OemInf = Path.GetFileName(path).ToLowerInvariant();
            p.OriginalName = p.OemInf;

            var version = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var localStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string section = "";
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = StripComment(raw).Trim();
                if (line.Length == 0) continue;
                if (line[0] == '[')
                {
                    int end = line.IndexOf(']');
                    section = (end > 0 ? line.Substring(1, end - 1) : line.Substring(1)).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = Unquote(line.Substring(eq + 1).Trim());
                if (section == "version") version[key] = val;
                else if (section == "strings") strings[key] = val;
                else if (section.StartsWith("strings.")) { if (!localStrings.ContainsKey(key)) localStrings[key] = val; }
            }
            foreach (var kv in localStrings) if (!strings.ContainsKey(kv.Key)) strings[kv.Key] = kv.Value;

            string v;
            if (version.TryGetValue("Provider", out v)) p.Provider = Resolve(v, strings);
            if (version.TryGetValue("Class", out v)) p.Class = Resolve(v, strings);
            if (version.TryGetValue("DriverVer", out v)) p.Version = v;

            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(Txt.RegPath("SYSTEM", "DriverDatabase", "DriverInfFiles", p.OemInf)))
                {
                    string active = k != null ? k.GetValue("Active") as string : null;
                    if (string.IsNullOrEmpty(active) && k != null)
                    {
                        var all = k.GetValue("") as string[];
                        if (all != null && all.Length > 0) active = all[all.Length - 1];
                    }
                    if (!string.IsNullOrEmpty(active))
                    {
                        p.StoreFolder = active;
                        int i = active.IndexOf(".inf_", StringComparison.OrdinalIgnoreCase);
                        if (i > 0) p.OriginalName = active.Substring(0, i + 4).ToLowerInvariant();
                    }
                }
            }
            catch { }
            return p;
        }

        // Removes a ; comment that is not inside quotes.
        static string StripComment(string s)
        {
            bool q = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"') q = !q;
                else if (s[i] == ';' && !q) return s.Substring(0, i);
            }
            return s;
        }

        static string Unquote(string s)
        {
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') return s.Substring(1, s.Length - 2);
            return s;
        }

        // %Token% -> value from [Strings].
        static string Resolve(string v, Dictionary<string, string> strings)
        {
            if (v.Length >= 2 && v[0] == '%' && v[v.Length - 1] == '%')
            {
                string r;
                if (strings.TryGetValue(v.Substring(1, v.Length - 2), out r)) return r;
            }
            return v;
        }
    }
}
