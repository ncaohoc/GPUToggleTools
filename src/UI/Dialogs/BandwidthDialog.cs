// Runs the PCIe bandwidth test for one GPU and shows the result next to its link.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace GpuToggle
{
    class BandwidthDialog : Form
    {
        GpuInfo gpu;
        Label lblLink, lblNote;
        Panel results;
        Button run;
        BandwidthResult last;
        string tunnel;
        string state = "Click Run test. It takes a few seconds and briefly loads the GPU.";
        double theoretical;

        bool autoRun;

        // autoRun: start the test right away and close by itself once it succeeds
        // (used by "Start recording" so the measured limit is in place before recording).
        public BandwidthDialog(GpuInfo g, bool autoRun = false)
        {
            gpu = g;
            this.autoRun = autoRun;
            if (autoRun) Shown += delegate { Start(); };
            Text = "PCIe bandwidth - " + g.Name;
            Font = Theme.Regular;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Theme.Hook(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(Ui.S(600), Ui.S(460));
            Padding = new Padding(Ui.S(16));

            var title = new Label();
            title.Dock = DockStyle.Top;
            title.Height = Ui.S(30);
            title.Font = Theme.Title;
            title.ForeColor = Theme.Text;
            title.Text = g.Name;
            title.AutoEllipsis = true;

            lblLink = new Label();
            lblLink.Dock = DockStyle.Top;
            lblLink.Height = Ui.S(100);
            lblLink.ForeColor = Theme.Text;
            lblLink.Padding = new Padding(0, Ui.S(6), 0, 0);

            results = new Panel();
            results.Dock = DockStyle.Top;
            results.Height = Ui.S(150);
            results.BackColor = Theme.Card;
            results.Paint += delegate (object s, PaintEventArgs e) { PaintResults(e.Graphics); };
            typeof(Panel).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(results, true, null);

            lblNote = new Label();
            lblNote.Dock = DockStyle.Fill;
            lblNote.Font = Theme.Small;
            lblNote.ForeColor = Theme.Muted;
            lblNote.Padding = new Padding(0, Ui.S(10), 0, 0);
            lblNote.Text =
                "The test copies 64 MB blocks between system memory and VRAM with Direct3D 11 and times them on the GPU. " +
                "Real transfers usually reach 80-90 % of the theoretical link bandwidth." + Txt.NL +
                "Thunderbolt / USB4 eGPUs tunnel PCIe, so the reported link may not show the real limit " +
                "(about 2.5-3.8 GB/s for Thunderbolt 3/4). The measured numbers are what counts.";

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.BackColor = Theme.Surface;
            var close = Theme.MakeButton("Close", Theme.Card, false);
            close.DialogResult = DialogResult.Cancel;
            run = Theme.MakeButton("Run test", Theme.Accent, true);
            run.Click += delegate { Start(); };
            bar.Controls.Add(close);
            bar.Controls.Add(run);
            CancelButton = close;
            AcceptButton = run;

            Controls.Add(lblNote);
            Controls.Add(results);
            Controls.Add(lblLink);
            Controls.Add(title);
            Controls.Add(bar);

            try { tunnel = Pcie.Tunnel(g.InstanceId); } catch { tunnel = null; }
            List<PcieLink> chain;
            try { chain = Pcie.Chain(g.InstanceId); } catch { chain = new List<PcieLink>(); }
            ShowLink(chain, true);
        }

        void ShowLink(List<PcieLink> chain, bool idle)
        {
            PcieLink own = chain.Count > 0 ? chain[0] : null;
            if (own == null)
            {
                theoretical = 0;
                lblLink.Text = "No PCIe link information for this GPU (integrated GPUs share system memory, " +
                    "so the result shows memory copy speed instead).";
                return;
            }
            var sb = new StringBuilder();
            sb.Append("PCIe link:  " + own.CurText + (idle ? " (now, idle)" : " (under load)"));
            if (own.MaxWidth > 0) sb.Append("   -   card supports " + own.MaxText);
            theoretical = own.CurGBs;
            if (tunnel != null)
                sb.Append(Txt.NL + "Connected through a " + tunnel + " tunnel - that is usually the real limit, not the link above.");
            if (theoretical > 0)
                sb.Append(Txt.NL + "Theoretical:  " + theoretical.ToString("0.0", Txt.Inv) + " GB/s per direction");
            if (idle) sb.Append(Txt.NL + "GPUs lower the link speed when idle; the test re-reads it under load.");
            lblLink.Text = sb.ToString();
        }

        void Start()
        {
            run.Enabled = false;
            last = null;
            state = "Starting...";
            results.Invalidate();
            Cursor = Cursors.AppStarting;
            var t = new Thread(delegate ()
            {
                BandwidthResult r;
                try
                {
                    r = D3D11Bandwidth.Test(gpu, delegate (string msg) { Post(delegate { state = msg; results.Invalidate(); }); });
                }
                catch (Exception ex) { r = new BandwidthResult(); r.Error = ex.Message; }
                Post(delegate { Done(r); });
            });
            t.IsBackground = true;
            t.Start();
        }

        void Post(MethodInvoker a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        void Done(BandwidthResult r)
        {
            if (IsDisposed) return;
            Cursor = Cursors.Default;
            run.Enabled = true;
            run.Text = "Run again";
            if (r.Error != null) { state = "Test failed: " + r.Error; results.Invalidate(); return; }
            last = r;
            BandwidthHistory.Save(gpu.InstanceId, r);
            var chain = r.BestChain;
            ShowLink(chain, chain == r.Chain);
            state = null;
            results.Invalidate();
            if (autoRun)
            {
                // Leave the numbers on screen for a moment, then continue to recording.
                run.Enabled = false;
                var t = new System.Windows.Forms.Timer();
                t.Interval = 1500;
                t.Tick += delegate { t.Dispose(); if (!IsDisposed) { DialogResult = DialogResult.OK; Close(); } };
                t.Start();
            }
        }

        void PaintResults(Graphics g)
        {
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, results.Width - 1, results.Height - 1);
            if (last == null)
            {
                var r0 = new Rectangle(Ui.S(16), 0, results.Width - Ui.S(32), results.Height);
                TextRenderer.DrawText(g, state ?? "", Theme.Regular, r0, state != null && state.StartsWith("Test failed", StringComparison.Ordinal) ? Theme.Red : Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return;
            }
            double scale = theoretical > 0 ? theoretical : Math.Max(last.UploadGBs, last.DownloadGBs) * 1.15;
            DrawRow(g, Ui.S(18), "Host -> GPU  (upload)", last.UploadGBs, scale, flags);
            DrawRow(g, Ui.S(82), "GPU -> Host  (download)", last.DownloadGBs, scale, flags);
        }

        void DrawRow(Graphics g, int y, string label, double gbs, double scale, TextFormatFlags flags)
        {
            int x = Ui.S(16), w = results.Width - Ui.S(32);
            TextRenderer.DrawText(g, label, Theme.Semibold, new Rectangle(x, y, w, Ui.S(22)), Theme.Text, flags);
            string val = gbs.ToString("0.00", Txt.Inv) + " GB/s";
            if (theoretical > 0) val += "   (" + (gbs / theoretical * 100).ToString("0", Txt.Inv) + " % of link)";
            TextRenderer.DrawText(g, val, Theme.Semibold, new Rectangle(x, y, w, Ui.S(22)), Theme.Accent,
                (flags & ~TextFormatFlags.Left) | TextFormatFlags.Right);
            int bh = Ui.S(10);
            var track = new Rectangle(x, y + Ui.S(30), w, bh);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(track, bh / 2f))
            using (var br = new SolidBrush(Theme.Border))
                g.FillPath(br, path);
            int fw = scale > 0 ? (int)Math.Round(w * Math.Min(1.0, gbs / scale)) : 0;
            if (fw > 0)
            {
                using (var path = Theme.Round(new Rectangle(track.X, track.Y, Math.Max(fw, bh), bh), bh / 2f))
                using (var br = new SolidBrush(Theme.Accent))
                    g.FillPath(br, path);
            }
            g.SmoothingMode = old;
        }
    }
}
