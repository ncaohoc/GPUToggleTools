// Runs a console tool (pnputil, bcdedit, sc, powercfg...) hidden and returns its exit code and output.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GpuToggle
{
    static class Shell
    {
        public static int Run(string exe, string args, out string output)
        {
            return Run(exe, args, 120000, out output);
        }

        public static int Run(string exe, string args, int timeoutMs, out string output)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            try
            {
                var enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                psi.StandardOutputEncoding = enc;
                psi.StandardErrorEncoding = enc;
            }
            catch { }
            var sb = new StringBuilder();
            object gate = new object();
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    DataReceivedEventHandler h = delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (gate) sb.AppendLine(e.Data);
                    };
                    p.OutputDataReceived += h;
                    p.ErrorDataReceived += h;
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        lock (gate) output = sb.ToString() + "(timed out)";
                        return -1;
                    }
                    p.WaitForExit();
                    lock (gate) output = sb.ToString();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        // Last non-empty line of a tool's output (usually the error message).
        public static string LastLine(string s)
        {
            var lines = (s ?? "").Trim().Split(new char[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
        }

        // 0 = success, 3010 = success but a restart is needed.
        public static bool Ok(int code) { return code == 0 || code == 3010; }
    }
}
