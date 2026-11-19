// OSD page: settings and a live preview of the in-game overlay, plus the Ctrl+Shift+O hotkey.
// The sampler and the ETW frame counter only run while the OSD is shown or this page is open.

using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace GpuToggle
{
    class OsdPage : AppPage
    {
        const int HotkeyId = 0x4F53;

        Button btnOsdShow;
        ComboBox cmbOsdGpu, cmbOsdPos, cmbOsdSize;
        NumericUpDown numOsdAlpha;
        StatCard cardFps, cardGpu, cardVram, cardCpu, cardRam, cardPcie, cardVerdict;
        Label lblOsdInfo;
        List<GpuInfo> osdGpus = new List<GpuInfo>();
        OsdSampler osdSampler = new OsdSampler();
        OsdOverlay osdOverlay;
        bool osdShown, osdComboSync;
        System.Windows.Forms.Timer osdTimer;
        GlobalHotkey hotkey;
        bool closed;

        public override string Title { get { return "OSD"; } }
        public override int NavWidth { get { return 84; } }
        public override string StatusHint
        {
            get { return "Show the OSD (or press Ctrl+Shift+O in game), then play. The bottleneck card says what limits the FPS."; }
        }

        public override void OnActivated() { UpdateOsdRunning(); }
        public override void OnDeactivated() { UpdateOsdRunning(); }

        public override void Shutdown()
        {
            closed = true;
            osdTimer.Stop();
            osdSampler.Stop();
            FrameCounter.Stop();
            hotkey.Dispose();
            if (osdOverlay != null) osdOverlay.Dispose();
        }

        Label OsdLabelCtl(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = Theme.Muted;
            l.Font = Theme.Semibold;
            l.Margin = new Padding(Ui.S(8), Ui.S(8), Ui.S(6), 0);
            return l;
        }

        ComboBox OsdCombo(int width, params string[] items)
        {
            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Width = Ui.S(width);
            c.Margin = new Padding(0, Ui.S(5), Ui.S(6), 0);
            c.Items.AddRange(items);
            return c;
        }

        public OsdPage()
        {
            OsdSettings.Load();
            osdSampler.GpuChoice = OsdSettings.Gpu;

            var bar = Theme.Toolbar();
            var lblGpu = OsdLabelCtl("GPU:");
            lblGpu.Margin = new Padding(0, Ui.S(8), Ui.S(6), 0);
            cmbOsdGpu = OsdCombo(250);
            cmbOsdGpu.SelectedIndexChanged += delegate
            {
                if (osdComboSync) return;
                int i = cmbOsdGpu.SelectedIndex;
                OsdSettings.Gpu = i <= 0 || i - 1 >= osdGpus.Count ? "auto" : osdGpus[i - 1].InstanceId;
                osdSampler.GpuChoice = OsdSettings.Gpu;
                OsdSettings.Save();
            };
            btnOsdShow = Theme.MakeButton("Show OSD", Theme.Green, true);
            btnOsdShow.Margin = new Padding(Ui.S(4), 0, Ui.S(8), Ui.S(8));
            btnOsdShow.Click += delegate { ToggleOsd(); };

            cmbOsdPos = OsdCombo(120, "Top left", "Top right", "Bottom left", "Bottom right");
            cmbOsdPos.SelectedIndex = OsdSettings.Corner;
            cmbOsdPos.SelectedIndexChanged += delegate { OsdSettings.Corner = cmbOsdPos.SelectedIndex; OsdSettings.Save(); OsdTick(); };
            cmbOsdSize = OsdCombo(90, "Small", "Medium", "Large");
            cmbOsdSize.SelectedIndex = OsdSettings.TextSize;
            cmbOsdSize.SelectedIndexChanged += delegate { OsdSettings.TextSize = cmbOsdSize.SelectedIndex; OsdSettings.Save(); OsdTick(); };
            numOsdAlpha = new NumericUpDown();
            numOsdAlpha.Minimum = 0; numOsdAlpha.Maximum = 100; numOsdAlpha.Increment = 5;
            numOsdAlpha.Value = OsdSettings.Alpha;
            numOsdAlpha.Width = Ui.S(56);
            numOsdAlpha.Margin = new Padding(0, Ui.S(5), Ui.S(4), 0);
            numOsdAlpha.ValueChanged += delegate { OsdSettings.Alpha = (int)numOsdAlpha.Value; OsdSettings.Save(); OsdTick(); };

            bar.Controls.Add(lblGpu);
            bar.Controls.Add(cmbOsdGpu);
            bar.Controls.Add(btnOsdShow);
            var pct = OsdLabelCtl("%");
            pct.Margin = new Padding(0, Ui.S(8), 0, 0);
            bar.Controls.Add(Theme.Group(OsdLabelCtl("Position:"), cmbOsdPos));
            bar.Controls.Add(Theme.Group(OsdLabelCtl("Size:"), cmbOsdSize));
            bar.Controls.Add(Theme.Group(OsdLabelCtl("Background:"), numOsdAlpha, pct));

            var bar2 = Theme.Toolbar();
            var lblShow = OsdLabelCtl("Show on the OSD:");
            lblShow.Margin = new Padding(0, Ui.S(4), Ui.S(10), Ui.S(6));
            bar2.Controls.Add(lblShow);
            string[] names = { "FPS", "GPU", "VRAM", "CPU", "RAM", "PCIe up / down", "Bottleneck" };
            for (int i = 0; i < names.Length; i++)
            {
                int bit = 1 << i;
                var cb = new CheckBox();
                cb.Text = names[i];
                cb.AutoSize = true;
                cb.ForeColor = Theme.Text;
                cb.BackColor = Theme.Surface;
                cb.Checked = (OsdSettings.Items & bit) != 0;
                cb.Margin = new Padding(0, Ui.S(4), Ui.S(14), Ui.S(6));
                cb.CheckedChanged += delegate
                {
                    if (cb.Checked) OsdSettings.Items |= bit; else OsdSettings.Items &= ~bit;
                    OsdSettings.Save();
                    OsdTick();
                };
                bar2.Controls.Add(cb);
            }

            var bar3 = Theme.Toolbar();
            var lblGraph = OsdLabelCtl("Graph next to:");
            lblGraph.Margin = new Padding(0, Ui.S(4), Ui.S(10), Ui.S(6));
            bar3.Controls.Add(lblGraph);
            string[] gnames = { "FPS", "GPU load", "VRAM", "CPU", "RAM", "PCIe up / down" };
            for (int i = 0; i < gnames.Length; i++)
            {
                int bit = 1 << i;
                var cb = new CheckBox();
                cb.Text = gnames[i];
                cb.AutoSize = true;
                cb.ForeColor = Theme.Text;
                cb.BackColor = Theme.Surface;
                cb.Checked = (OsdSettings.Graphs & bit) != 0;
                cb.Margin = new Padding(0, Ui.S(4), Ui.S(14), Ui.S(6));
                cb.CheckedChanged += delegate
                {
                    if (cb.Checked) OsdSettings.Graphs |= bit; else OsdSettings.Graphs &= ~bit;
                    OsdSettings.Save();
                    OsdTick();
                };
                bar3.Controls.Add(cb);
            }

            lblOsdInfo = new Label();
            lblOsdInfo.Dock = DockStyle.Top;
            lblOsdInfo.Height = Ui.S(24);
            lblOsdInfo.ForeColor = Theme.Muted;
            lblOsdInfo.Font = Theme.Small;
            lblOsdInfo.AutoEllipsis = true;

            var cards = new TableLayoutPanel();
            cards.Dock = DockStyle.Top;
            cards.Height = Ui.S(110);
            cards.ColumnCount = 6;
            cards.RowCount = 1;
            cards.BackColor = Theme.Surface;
            for (int i = 0; i < 6; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6));
            cardFps = new StatCard(); cardFps.Caption = "FPS";
            cardGpu = new StatCard(); cardGpu.Caption = "GPU";
            cardVram = new StatCard(); cardVram.Caption = "VRAM";
            cardCpu = new StatCard(); cardCpu.Caption = "CPU";
            cardRam = new StatCard(); cardRam.Caption = "RAM";
            cardPcie = new StatCard(); cardPcie.Caption = "PCIe up / down";
            StatCard[] all = { cardFps, cardGpu, cardVram, cardCpu, cardRam, cardPcie };
            for (int i = 0; i < all.Length; i++)
            {
                all[i].Dock = DockStyle.Fill;
                all[i].Margin = new Padding(i == 0 ? 0 : Ui.S(5), 0, i == all.Length - 1 ? 0 : Ui.S(5), 0);
                cards.Controls.Add(all[i], i, 0);
            }

            var gap = new Panel();
            gap.Dock = DockStyle.Top;
            gap.Height = Ui.S(10);
            gap.BackColor = Theme.Surface;

            cardVerdict = new StatCard();
            cardVerdict.Caption = "What limits the FPS (last 3 s)";
            cardVerdict.Dock = DockStyle.Top;
            cardVerdict.Height = Ui.S(100);
            cardVerdict.Margin = new Padding(0);

            var gap2 = new Panel();
            gap2.Dock = DockStyle.Top;
            gap2.Height = Ui.S(10);
            gap2.BackColor = Theme.Surface;

            // The text is taller than the space left on smaller windows: it scrolls (scroll bar only when needed).
            var helpBox = new Panel();
            helpBox.BackColor = Theme.Card;
            helpBox.AutoScroll = true;
            helpBox.Padding = new Padding(Ui.S(12), Ui.S(10), Ui.S(4), Ui.S(10));
            var help = new Label();
            help.AutoSize = true;
            help.BackColor = Theme.Card;
            help.ForeColor = Theme.Text;
            help.Location = new System.Drawing.Point(Ui.S(12), Ui.S(10));
            help.Margin = new Padding(0, 0, 0, Ui.S(10));
            helpBox.Controls.Add(help);
            helpBox.Layout += delegate
            {
                int w = helpBox.ClientSize.Width - Ui.S(12) - Ui.S(8);
                if (w > 0 && help.MaximumSize.Width != w) help.MaximumSize = new System.Drawing.Size(w, 0);
            };
            string nl = Txt.NL;
            help.Text =
                "GPU-bound (GPU >= 95 %): the normal case. For more FPS lower resolution / settings or use DLSS / FSR." + nl +
                "CPU-bound (busiest CPU thread >= 90 %, GPU below 95 %): the GPU waits for the game. Raising resolution costs no FPS; lower view distance, crowds, physics." + nl +
                "PCIe link (>= 90 % of the measured limit): typical for eGPUs. Lower textures / resolution, and connect the monitor to the eGPU so frames do not travel back over the link." + nl +
                "VRAM full (>= 95 %): textures spill into system RAM and come over PCIe, which causes stutter. Lower texture quality." + nl +
                "V-Sync / refresh cap: FPS sits at the monitor refresh rate. Limited elsewhere: nothing is maxed (FPS limiter, engine, one thread hopping between cores)." + nl + nl +
                "FPS comes from the present events Windows logs (ETW), nothing is injected into the game: DirectX 9 - 12, Vulkan / OpenGL through the kernel graphics events. " +
                "The OSD measures the app in the foreground. It is a click-through topmost window, so it shows over windowed and borderless games; exclusive fullscreen can hide it (switch the game to borderless). " +
                "PCIe traffic needs an NVIDIA GPU (NVML); run PCIe bandwidth... once so 100 % is the measured limit. CPU temperature: LibreHardwareMonitor if it is running, else the ACPI thermal zone.";
            var helpSection = Theme.Section("How to read it", "Hotkey Ctrl+Shift+O shows / hides the OSD from inside the game", Theme.CardAround(helpBox));

            Controls.Add(helpSection);
            Controls.Add(gap2);
            Controls.Add(cardVerdict);
            Controls.Add(gap);
            Controls.Add(cards);
            Controls.Add(lblOsdInfo);
            Controls.Add(bar3);
            Controls.Add(bar2);
            Controls.Add(bar);

            osdTimer = new System.Windows.Forms.Timer();
            osdTimer.Interval = 500;
            osdTimer.Tick += delegate { OsdTick(); };

            hotkey = new GlobalHotkey(HotkeyId, GlobalHotkey.Ctrl | GlobalHotkey.Shift | GlobalHotkey.NoRepeat, Keys.O);
            hotkey.Pressed += ToggleOsd;

        }

        // GPU combo: "Auto" + the enabled physical GPUs, NVIDIA first.
        public void SetGpus(List<GpuInfo> gpus)
        {
            var l = GpuDevices.ActiveNvidiaFirst(gpus);
            osdSampler.Gpus = l;
            var ids = new System.Text.StringBuilder();
            foreach (var g in l) ids.Append(g.InstanceId).Append('|');
            var old = new System.Text.StringBuilder();
            foreach (var g in osdGpus) old.Append(g.InstanceId).Append('|');
            if (ids.ToString() == old.ToString() && cmbOsdGpu.Items.Count > 0) return;

            osdGpus = l;
            osdComboSync = true;
            cmbOsdGpu.Items.Clear();
            cmbOsdGpu.Items.Add("Auto (GPU the game runs on)");
            int sel = 0;
            for (int i = 0; i < l.Count; i++)
            {
                cmbOsdGpu.Items.Add(l[i].Name);
                if (l[i].InstanceId == OsdSettings.Gpu) sel = i + 1;
            }
            cmbOsdGpu.SelectedIndex = sel;
            osdComboSync = false;
        }

        public void ToggleOsd()
        {
            osdShown = !osdShown;
            if (osdShown)
            {
                if (osdOverlay == null || osdOverlay.IsDisposed) osdOverlay = new OsdOverlay();
                osdOverlay.Show();
            }
            else if (osdOverlay != null) osdOverlay.Hide();
            btnOsdShow.Text = osdShown ? "Hide OSD" : "Show OSD";
            Theme.SetButtonColor(btnOsdShow, osdShown ? Theme.Red : Theme.Green);
            UpdateOsdRunning();
            OsdTick();
        }

        // The sampler and the ETW session only run while the OSD is shown or its page is open.
        void UpdateOsdRunning()
        {
            if (closed) return;
            bool need = osdShown || Visible;
            if (need)
            {
                if (!FrameCounter.Running) FrameCounter.Start();
                osdSampler.Start();
                osdTimer.Start();
            }
            else
            {
                osdTimer.Stop();
                osdSampler.Stop();
                FrameCounter.Stop();
            }
        }

        void OsdTick()
        {
            if (IsDisposed) return;
            var s = osdSampler.Latest;
            if (Visible) UpdateOsdCards(s);
            if (osdShown && osdOverlay != null && !osdOverlay.IsDisposed)
                osdOverlay.Render(OsdLines.Build(s), OsdSettings.Corner, OsdSettings.TextSize, OsdSettings.Alpha, s != null ? s.Hwnd : IntPtr.Zero);
        }

        void UpdateOsdCards(OsdSnapshot s)
        {
            if (s == null)
            {
                lblOsdInfo.Text = FrameCounter.Error ?? "Starting sensors...";
                return;
            }
            string fpsSrc = s.Frames != null ? "FPS from " + s.Frames.Api + " presents" : (s.FpsError != null ? s.FpsError : "No frames from this app yet");
            lblOsdInfo.Text = "Measuring: " + (s.App.Length > 0 ? s.App + " (pid " + s.Pid + ")" : "-") + " on " + (s.GpuName.Length > 0 ? s.GpuName : "-") +
                "   |   " + fpsSrc + "   |   OSD hotkey Ctrl+Shift+O" + (hotkey.Registered ? "" : " (taken by another app)");

            if (s.Frames != null)
                cardFps.Set(Fmt.F(s.Frames.Fps, "0"), Fmt.F(s.Frames.FrameMs, "0.0") + " ms per frame" + Txt.NL +
                    (s.Frames.Low1 > 0 ? "1 % low " + Fmt.F(s.Frames.Low1, "0") + " FPS" : "1 % low: collecting...") +
                    (s.RefreshHz > 0 ? Txt.NL + "Monitor " + s.RefreshHz + " Hz" : ""), Theme.Text);
            else cardFps.Set(Txt.NA, s.FpsError ?? ("Not presenting frames" + Txt.NL + "Bring the game to the front"), Theme.Text);

            if (s.GpuLoad >= 0 || s.GpuTemp > 0)
                cardGpu.Set((s.GpuLoad >= 0 ? Fmt.F(s.GpuLoad, "0") : Txt.NA) + " %",
                    (s.GpuTemp > 0 ? Fmt.F(s.GpuTemp, "0") + " " + Fmt.Deg : "Temp n/a") +
                    (s.GpuPower >= 0 ? "   " + Fmt.F(s.GpuPower, "0") + (s.GpuPowerLimit > 0 ? " / " + Fmt.F(s.GpuPowerLimit, "0") : "") + " W" : "") + Txt.NL + s.GpuName,
                    s.GpuTemp >= 83 ? Theme.Red : Theme.Text);
            else cardGpu.Set(Txt.NA, s.GpuName, Theme.Text);

            if (s.VramTotal > 0)
                cardVram.Set(Fmt.GiB(s.VramUsed) + " / " + Fmt.GiB(s.VramTotal) + " GB", Fmt.F(s.VramPct, "0") + " % used", s.VramPct >= 95 ? Theme.Red : (s.VramPct >= 85 ? Theme.Orange : Theme.Text));
            else cardVram.Set(Txt.NA, "", Theme.Text);

            if (s.CpuLoad >= 0)
                cardCpu.Set(Fmt.F(s.CpuLoad, "0") + " %", "Busiest thread " + Fmt.F(s.CpuMaxCore, "0") + " %" + Txt.NL +
                    (s.CpuTemp > 0 ? Fmt.F(s.CpuTemp, "0") + " " + Fmt.Deg + " (" + s.CpuTempSource + ")" : "Temp n/a: run LibreHardwareMonitor"),
                    s.CpuMaxCore >= 90 ? Theme.Orange : Theme.Text);
            else cardCpu.Set(Txt.NA, "", Theme.Text);

            if (s.RamTotal > 0)
                cardRam.Set(Fmt.GiB(s.RamUsed) + " / " + Fmt.GiB(s.RamTotal) + " GB", Fmt.F(s.RamPct, "0") + " % used", s.RamPct >= 92 ? Theme.Orange : Theme.Text);
            else cardRam.Set(Txt.NA, "", Theme.Text);

            if (s.PcieRx >= 0)
            {
                double p = s.PciePct;
                cardPcie.Set(Fmt.F(s.PcieRx / 1e9, "0.00") + " / " + Fmt.F(s.PcieTx / 1e9, "0.00"), "GB/s host -> GPU / GPU -> host" + Txt.NL +
                    (p >= 0 ? Fmt.F(p, "0") + " % of the " + (s.PcieMeasured ? "measured limit" : "theoretical link") : "") +
                    (s.PcieMeasured ? "" : Txt.NL + "Run PCIe bandwidth... for the real limit"),
                    p >= 90 ? Theme.Red : (p >= 70 ? Theme.Orange : Theme.Text));
            }
            else cardPcie.Set(Txt.NA, s.GpuNvidia ? "Waiting for NVML..." : "Needs an NVIDIA GPU (NVML)", Theme.Text);

            var v = s.Verdict;
            cardVerdict.Set(v.Text.Length > 0 ? v.Text : Txt.NA, v.Detail,
                v.Level == 2 ? Theme.Red : (v.Level == 1 ? Theme.Orange : (v.Level == 0 ? Theme.Green : Theme.Muted)));
        }
    }
}
