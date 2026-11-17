// Writes a PCIe monitor recording to CSV: summary, bottleneck episodes, then every sample.

using System.IO;
using System.Text;

namespace GpuToggle
{
    static class PcieCsvExport
    {
        public static void Write(PcieMonitor m, string path)
        {
            var sb = new StringBuilder();
            lock (m.Lock)
            {
                sb.Append("# GPU," + Csv(m.Gpu != null ? m.Gpu.Name : "") + Txt.NL);
                sb.Append("# 100 % = host->GPU " + (m.CapRx / 1e9).ToString("0.00", Txt.Inv) + " GB/s; GPU->host " + (m.CapTx / 1e9).ToString("0.00", Txt.Inv) + " GB/s (" + m.CapSource + ")" + Txt.NL);
                sb.Append("# Threshold," + m.ThresholdPct.ToString("0", Txt.Inv) + " %" + Txt.NL);
                if (m.Seconds > 0)
                    sb.Append("# Average GB/s,host->GPU " + (m.SumRx / m.Seconds / 1e9).ToString("0.000", Txt.Inv) + ",GPU->host " + (m.SumTx / m.Seconds / 1e9).ToString("0.000", Txt.Inv) + Txt.NL);
                sb.Append("# Peak GB/s,host->GPU " + (m.MaxRx / 1e9).ToString("0.000", Txt.Inv) + ",GPU->host " + (m.MaxTx / 1e9).ToString("0.000", Txt.Inv) + Txt.NL);
                sb.Append("# Bottleneck seconds," + m.SatSeconds.ToString("0.0", Txt.Inv) + Txt.NL + Txt.NL);

                sb.Append("episode_start,episode_end,seconds,direction,peak_pct,app" + Txt.NL);
                foreach (var e in m.Episodes)
                    sb.Append(e.Start.ToString("yyyy-MM-dd HH:mm:ss.fff", Txt.Inv) + "," + e.End.ToString("yyyy-MM-dd HH:mm:ss.fff", Txt.Inv) + "," +
                        e.Seconds.ToString("0.00", Txt.Inv) + "," + Csv(e.Dir) + "," + e.PeakPct.ToString("0", Txt.Inv) + "," + Csv(e.App) + Txt.NL);
                sb.Append(Txt.NL);

                sb.Append("time,host_to_gpu_GBps,gpu_to_host_GBps,host_to_gpu_pct,gpu_to_host_pct,link,foreground_app,bottleneck" + Txt.NL);
                foreach (var s in m.Samples)
                    sb.Append(s.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", Txt.Inv) + "," +
                        (s.Rx / 1e9).ToString("0.000", Txt.Inv) + "," + (s.Tx / 1e9).ToString("0.000", Txt.Inv) + "," +
                        (m.CapRx > 0 ? s.Rx / m.CapRx * 100 : 0).ToString("0.0", Txt.Inv) + "," + (m.CapTx > 0 ? s.Tx / m.CapTx * 100 : 0).ToString("0.0", Txt.Inv) + "," +
                        Csv(s.Link) + "," + Csv(s.App) + "," + (s.Saturated ? "1" : "0") + Txt.NL);
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        static string Csv(string v)
        {
            if (v == null) return "";
            if (v.IndexOf(',') < 0 && v.IndexOf('"') < 0) return v;
            return Txt.Q + v.Replace(Txt.Q, Txt.Q + Txt.Q) + Txt.Q;
        }
    }
}
