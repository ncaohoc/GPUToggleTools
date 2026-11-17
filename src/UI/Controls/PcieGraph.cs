// Real-time line graph of the last 2 minutes of PCIe traffic, as % of the link capacity.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GpuToggle
{
    class PcieGraph : Control
    {
        public PcieMonitor Mon;
        public int WindowSeconds = 120;
        public string Message;
        static Color RxColor { get { return Theme.Accent; } }
        static readonly Color TxColor = Color.FromArgb(234, 88, 12);

        public PcieGraph()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Theme.Card;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Card);
            var tf = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            if (Message != null || Mon == null)
            {
                TextRenderer.DrawText(g, Message ?? "", Theme.Regular, new Rectangle(Ui.S(20), 0, Width - Ui.S(40), Height), Theme.Muted,
                    TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            var plot = new Rectangle(Ui.S(48), Ui.S(30), Width - Ui.S(64), Height - Ui.S(56));
            if (plot.Width < 40 || plot.Height < 30) return;

            List<PcieSample> pts = new List<PcieSample>();
            double capRx, capTx, thr;
            lock (Mon.Lock)
            {
                capRx = Mon.CapRx; capTx = Mon.CapTx; thr = Mon.ThresholdPct;
                DateTime from = DateTime.Now.AddSeconds(-WindowSeconds - 1);
                for (int i = Mon.Samples.Count - 1; i >= 0 && Mon.Samples[i].Time >= from; i--) pts.Add(Mon.Samples[i]);
            }
            pts.Reverse();

            double top = 100;
            foreach (var p in pts) top = Math.Max(top, Math.Max(Pct(p.Rx, capRx), Pct(p.Tx, capTx)));
            top = Math.Ceiling(top / 25) * 25;

            // Bottleneck shading
            DateTime now = DateTime.Now;
            using (var sat = new SolidBrush(Color.FromArgb(40, 220, 38, 38)))
                foreach (var p in pts)
                {
                    if (!p.Saturated) continue;
                    float x1 = X(plot, now, p.Time.AddSeconds(-p.Dt)), x2 = X(plot, now, p.Time);
                    g.FillRectangle(sat, x1, plot.Top, Math.Max(1f, x2 - x1), plot.Height);
                }

            // Grid + axis labels
            using (var grid = new Pen(Theme.Border))
                for (int v = 0; v <= top; v += 25)
                {
                    float y = Y(plot, v, top);
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    TextRenderer.DrawText(g, v + " %", Theme.Small, new Rectangle(0, (int)y - Ui.S(8), plot.Left - Ui.S(6), Ui.S(16)), Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
            for (int sec = 0; sec <= WindowSeconds; sec += 30)
            {
                float x = plot.Right - (float)sec / WindowSeconds * plot.Width;
                TextRenderer.DrawText(g, sec == 0 ? "now" : "-" + sec + " s", Theme.Small, new Point((int)x - Ui.S(14), plot.Bottom + Ui.S(6)), Theme.Muted, tf);
            }
            using (var th = new Pen(Theme.Red, 1f))
            {
                th.DashStyle = DashStyle.Dash;
                float y = Y(plot, thr, top);
                g.DrawLine(th, plot.Left, y, plot.Right, y);
                TextRenderer.DrawText(g, (Mon.Measured ? "measured limit / bottleneck " : "bottleneck ") + thr.ToString("0", Txt.Inv) + " %", Theme.Small, new Point(plot.Left + Ui.S(4), (int)y - Ui.S(16)), Theme.Red, tf);
            }

            // Lines
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawLine(g, plot, now, top, pts, capRx, true, RxColor);
            DrawLine(g, plot, now, top, pts, capTx, false, TxColor);
            g.SmoothingMode = SmoothingMode.Default;

            // Legend
            int lx = plot.Left;
            lx = Legend(g, lx, "Host -> GPU (upload)", RxColor, tf);
            Legend(g, lx + Ui.S(18), "GPU -> Host (download)", TxColor, tf);
        }

        int Legend(Graphics g, int x, string text, Color c, TextFormatFlags tf)
        {
            using (var b = new SolidBrush(c)) g.FillRectangle(b, x, Ui.S(10), Ui.S(14), Ui.S(4));
            TextRenderer.DrawText(g, text, Theme.Small, new Point(x + Ui.S(20), Ui.S(4)), Theme.Text, tf);
            return x + Ui.S(20) + TextRenderer.MeasureText(text, Theme.Small).Width;
        }

        void DrawLine(Graphics g, Rectangle plot, DateTime now, double top, List<PcieSample> pts, double cap, bool rx, Color c)
        {
            if (pts.Count < 2) return;
            var arr = new PointF[pts.Count];
            for (int i = 0; i < pts.Count; i++)
                arr[i] = new PointF(X(plot, now, pts[i].Time), Y(plot, Pct(rx ? pts[i].Rx : pts[i].Tx, cap), top));
            using (var pen = new Pen(c, Ui.S(2))) g.DrawLines(pen, arr);
        }

        static double Pct(double v, double cap) { return cap > 0 ? v / cap * 100 : 0; }
        float X(Rectangle plot, DateTime now, DateTime t)
        {
            double age = (now - t).TotalSeconds;
            return (float)Math.Max(plot.Left, plot.Right - age / WindowSeconds * plot.Width);
        }
        static float Y(Rectangle plot, double pct, double top) { return (float)(plot.Bottom - Math.Min(pct, top) / top * plot.Height); }
    }
}
