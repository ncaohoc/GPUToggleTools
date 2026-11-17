// Main window: header with the page buttons and the light / dark switch, the pages, and the status bar.
// It wires the pages together; each page does its own work (see UI/Pages).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GpuToggle
{
    class MainForm : Form
    {
        const int WM_DEVICECHANGE = 0x0219;

        readonly GpusPage gpusPage = new GpusPage();
        readonly AppsPage appsPage = new AppsPage();
        readonly PcieMonitorPage monitorPage = new PcieMonitorPage();
        readonly OsdPage osdPage = new OsdPage();
        readonly List<AppPage> pages = new List<AppPage>();
        readonly List<Button> navs = new List<Button>();
        int curPage;

        Panel header;
        ThemeSwitch themeSwitch;
        Label lblStatus;
        Bitmap logo;
        System.Windows.Forms.Timer debounce;
        StatsPoller stats;

        public MainForm()
        {
            Text = "GPU Toggle v" + AppVersion.Text;
            Font = Theme.Regular;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Theme.Hook(this);
            ClientSize = new Size(Ui.S(1180), Ui.S(680));
            MinimumSize = new Size(Ui.S(960), Ui.S(560));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            logo = LoadLogo(Ui.S(40));

            pages.Add(gpusPage);
            pages.Add(appsPage);
            pages.Add(monitorPage);
            pages.Add(osdPage);
            foreach (var p in pages) p.StatusChanged += SetStatus;

            // Wiring between pages
            gpusPage.GpusChanged += delegate (List<GpuInfo> gpus) { monitorPage.SetGpus(gpus); osdPage.SetGpus(gpus); };
            gpusPage.BandwidthTestRequested += delegate (GpuInfo g) { monitorPage.RunBandwidthTest(g); };
            stats = new StatsPoller(this, delegate { return gpusPage.Gpus; }, appsPage.AppPaths);
            stats.Updated += delegate (StatsResult r) { gpusPage.ApplyStats(r); appsPage.ApplyStats(r); };

            BuildHeader();

            var content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Theme.Surface;
            content.Padding = new Padding(Ui.S(16), Ui.S(14), Ui.S(16), Ui.S(10));
            foreach (var p in pages) content.Controls.Add(p);

            // Docking: the last control added is docked first (outermost).
            Controls.Add(content);
            Controls.Add(BuildStatusBar());
            Controls.Add(header);

            // Plugging / unplugging a GPU sends a burst of WM_DEVICECHANGE: rescan once it settles.
            debounce = new System.Windows.Forms.Timer();
            debounce.Interval = 800;
            debounce.Tick += delegate
            {
                debounce.Stop();
                if (gpusPage.Busy) return;
                gpusPage.RefreshList();
                if (appsPage.Visible) appsPage.RefreshApps();
            };

            Shown += delegate
            {
                ShowPage(0);
                gpusPage.RefreshList();
                appsPage.RefreshApps();
                stats.Start();
            };
            FormClosing += delegate
            {
                stats.Stop();
                foreach (var p in pages) p.Shutdown();
            };
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE && debounce != null) { debounce.Stop(); debounce.Start(); }
            base.WndProc(ref m);
        }

        void SetStatus(string s) { lblStatus.Text = s; }

        void ShowPage(int page)
        {
            curPage = page;
            for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == page;
            StyleNav();
            for (int i = 0; i < pages.Count; i++)
                if (i == page) pages[i].OnActivated(); else pages[i].OnDeactivated();
            string hint = pages[page].StatusHint;
            if (hint != null) SetStatus(hint);
        }

        // ---------------------------------------------------------------- layout

        Panel BuildStatusBar()
        {
            var statusBar = new Panel();
            statusBar.Dock = DockStyle.Bottom;
            statusBar.Height = Ui.S(30);
            statusBar.BackColor = Theme.Card;
            statusBar.Paint += delegate (object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, statusBar.Width, 0);
            };
            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.Padding = new Padding(Ui.S(14), 0, 0, 0);
            lblStatus.ForeColor = Theme.Muted;
            lblStatus.Font = Theme.Small;
            statusBar.Controls.Add(lblStatus);
            return statusBar;
        }

        void BuildHeader()
        {
            header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = Ui.S(72);
            header.BackColor = Theme.Header;
            header.Paint += delegate (object s, PaintEventArgs e) { PaintHeader(e.Graphics); };

            var nav = new FlowLayoutPanel();
            nav.Dock = DockStyle.Right;
            nav.AutoSize = true;
            nav.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            nav.WrapContents = false;
            nav.BackColor = Theme.Header;
            nav.Padding = new Padding(0, Ui.S(18), Ui.S(14), 0);
            for (int i = 0; i < pages.Count; i++)
            {
                int index = i;
                var b = MakeNav(pages[i].Title, pages[i].NavWidth);
                b.Click += delegate { ShowPage(index); };
                navs.Add(b);
                nav.Controls.Add(b);
            }
            // Light / dark switch. Until it is clicked the app follows the Windows setting.
            themeSwitch = new ThemeSwitch();
            themeSwitch.Margin = new Padding(Ui.S(16), Ui.S(2), 0, 0);
            themeSwitch.SetState(Theme.Dark);
            new ToolTip().SetToolTip(themeSwitch, "Light / dark mode");
            themeSwitch.Toggled += delegate
            {
                Theme.Mode = themeSwitch.On ? "dark" : "light";
                Theme.SaveMode();
                Theme.Retheme(themeSwitch.On);
            };
            nav.Controls.Add(themeSwitch);
            header.Controls.Add(nav);

            Theme.Changed += delegate { StyleNav(); };
            UserPreferenceChangedEventHandler upc = delegate (object s, UserPreferenceChangedEventArgs e)
            {
                if (Theme.Mode == "system" && !IsDisposed)
                    BeginInvoke((MethodInvoker)delegate { Theme.Retheme(Theme.WantDark); StyleNav(); });
            };
            SystemEvents.UserPreferenceChanged += upc;
            FormClosed += delegate { SystemEvents.UserPreferenceChanged -= upc; };
        }

        void PaintHeader(Graphics g)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int x = Ui.S(18);
            if (logo != null)
            {
                int sz = Ui.S(40);
                g.DrawImage(logo, x, (header.Height - sz) / 2, sz, sz);
                x += sz + Ui.S(12);
            }
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, "GPU Toggle", Theme.Title, new Point(x, Ui.S(12)), Color.White, flags);
            int tw = TextRenderer.MeasureText(g, "GPU Toggle", Theme.Title, Size.Empty, flags).Width;
            var tsz = TextRenderer.MeasureText(g, "v", Theme.Title, Size.Empty, flags);
            var vsz = TextRenderer.MeasureText(g, "v", Theme.Small, Size.Empty, flags);
            TextRenderer.DrawText(g, "v" + AppVersion.Text, Theme.Small, new Point(x + tw + Ui.S(8), Ui.S(12) + tsz.Height - vsz.Height - Ui.S(3)), Theme.HeaderMuted, flags);
            TextRenderer.DrawText(g, "Enable / disable GPUs for eGPU setups and choose which GPU each game uses",
                Theme.Small, new Point(x + Ui.S(1), Ui.S(44)), Theme.HeaderMuted, flags);
        }

        static Button MakeNav(string text, int width)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.HeaderHover;
            b.FlatAppearance.MouseDownBackColor = Theme.HeaderSel;
            b.BackColor = Theme.Header;
            b.ForeColor = Theme.HeaderMuted;
            b.Font = Theme.Semibold;
            b.Cursor = Cursors.Hand;
            b.Size = new Size(Ui.S(width), Ui.S(36));
            b.Margin = new Padding(Ui.S(4), 0, 0, 0);
            return b;
        }

        // Header buttons: selected page highlighted; theme switch follows the current palette.
        void StyleNav()
        {
            for (int i = 0; i < navs.Count; i++)
            {
                bool sel = i == curPage;
                navs[i].BackColor = sel ? Theme.HeaderSel : Theme.Header;
                navs[i].ForeColor = sel ? Color.White : Theme.HeaderMuted;
                navs[i].FlatAppearance.MouseOverBackColor = Theme.HeaderHover;
                navs[i].FlatAppearance.MouseDownBackColor = Theme.HeaderSel;
            }
            if (themeSwitch.On != Theme.Dark) themeSwitch.SetState(Theme.Dark);   // e.g. Windows switched while on Auto
            if (themeSwitch.Parent != null) themeSwitch.Parent.BackColor = Theme.Header;
            themeSwitch.Invalidate();
            header.BackColor = Theme.Header;
            header.Invalidate();
        }

        // Reads the largest frame of the embedded app.ico. PNG frames are decoded directly,
        // because Icon.ToBitmap does not decode them reliably.
        static Bitmap LoadLogo(int size)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                {
                    if (s != null)
                    {
                        var ms = new MemoryStream();
                        s.CopyTo(ms);
                        byte[] ico = ms.ToArray();
                        int count = BitConverter.ToUInt16(ico, 4);
                        int bestSize = -1, bestOffset = 0, bestLength = 0;
                        for (int i = 0; i < count; i++)
                        {
                            int e = 6 + i * 16;
                            int w = ico[e] == 0 ? 256 : ico[e];
                            if (w > bestSize)
                            {
                                bestSize = w;
                                bestLength = BitConverter.ToInt32(ico, e + 8);
                                bestOffset = BitConverter.ToInt32(ico, e + 12);
                            }
                        }
                        if (bestSize > 0 && ico[bestOffset] == 0x89)
                        {
                            using (var img = Image.FromStream(new MemoryStream(ico, bestOffset, bestLength)))
                                return new Bitmap(img);
                        }
                        ms.Position = 0;
                        using (var ic = new Icon(ms, size, size)) return ic.ToBitmap();
                    }
                }
            }
            catch { }
            try
            {
                using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath)) return ic.ToBitmap();
            }
            catch { return null; }
        }
    }
}
