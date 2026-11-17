// PCIe monitor page: live PCIe traffic of one GPU while a game runs, the bottleneck log, and the
// bandwidth test that sets the real link limit.

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace GpuToggle
{
    class PcieMonitorPage : AppPage
    {
        PcieMonitor mon = new PcieMonitor();
        PcieGraph graph;
        StatCard cardLimit, cardNow, cardAvg, cardPeak, cardSat;
        StyledList epList;
        ComboBox cmbMonGpu;
        NumericUpDown numThr;
        Label lblThrPct, lblMonInfo;
        Button btnMonStart, btnMonReset, btnMonMeasure, btnMonExport;
        List<GpuInfo> monGpus = new List<GpuInfo>();
        int shownEpisodes;
        System.Windows.Forms.Timer monTimer;

        public override string Title { get { return "PCIe monitor"; } }
        public override string StatusHint
        {
            get { return "Start recording, then play your game. NVIDIA GPUs only (NVML). Shaded areas = PCIe bottleneck."; }
        }

        public override void Shutdown()
        {
            monTimer.Stop();
            mon.Stop();
        }

        public PcieMonitorPage()
        {
            var bar = Theme.Toolbar();
            var lblGpu = new Label();
            lblGpu.Text = "GPU:";
            lblGpu.AutoSize = true;
            lblGpu.ForeColor = Theme.Muted;
            lblGpu.Font = Theme.Semibold;
            lblGpu.Margin = new Padding(0, Ui.S(8), Ui.S(6), 0);
            cmbMonGpu = new ComboBox();
            cmbMonGpu.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMonGpu.Width = Ui.S(250);
            cmbMonGpu.Margin = new Padding(0, Ui.S(5), Ui.S(10), 0);
            cmbMonGpu.SelectedIndexChanged += delegate { AttachMonitor(); };

            btnMonStart = Theme.MakeButton("Start recording", Theme.Green, true);
            btnMonReset = Theme.MakeButton("Reset", Theme.Card, false);
            btnMonMeasure = Theme.MakeButton("Measure limit...", Theme.Card, false);
            btnMonExport = Theme.MakeButton("Export CSV...", Theme.Card, false);
            btnMonStart.Click += delegate { ToggleMonitor(); };
            btnMonReset.Click += delegate
            {
                mon.Reset();
                shownEpisodes = 0;
                epList.Items.Clear();
                UpdateMonitorUi();
            };
            btnMonMeasure.Click += delegate { if (mon.Gpu != null) RunBandwidthTest(mon.Gpu); };
            btnMonExport.Click += delegate { ExportMonitor(); };

            var lblThr = new Label();
            lblThr.Text = "Bottleneck at";
            lblThr.AutoSize = true;
            lblThr.ForeColor = Theme.Muted;
            lblThr.Margin = new Padding(Ui.S(8), Ui.S(8), Ui.S(6), 0);
            numThr = new NumericUpDown();
            numThr.Minimum = 5; numThr.Maximum = 100; numThr.Value = 80; numThr.Increment = 5;
            numThr.Width = Ui.S(56);
            numThr.Margin = new Padding(0, Ui.S(5), Ui.S(4), 0);
            numThr.ValueChanged += delegate { mon.ThresholdPct = (double)numThr.Value; graph.Invalidate(); };
            var lblPct = lblThrPct = new Label();
            lblPct.Text = "% of link";
            lblPct.AutoSize = true;
            lblPct.ForeColor = Theme.Muted;
            lblPct.Margin = new Padding(0, Ui.S(8), 0, 0);

            bar.Controls.Add(lblGpu);
            bar.Controls.Add(cmbMonGpu);
            bar.Controls.Add(btnMonStart);
            bar.Controls.Add(btnMonReset);
            bar.Controls.Add(btnMonMeasure);
            bar.Controls.Add(btnMonExport);
            bar.Controls.Add(lblThr);
            bar.Controls.Add(numThr);
            bar.Controls.Add(lblPct);

            lblMonInfo = new Label();
            lblMonInfo.Dock = DockStyle.Top;
            lblMonInfo.Height = Ui.S(24);
            lblMonInfo.ForeColor = Theme.Muted;
            lblMonInfo.Font = Theme.Small;
            lblMonInfo.AutoEllipsis = true;

            var cards = new TableLayoutPanel();
            cards.Dock = DockStyle.Top;
            cards.Height = Ui.S(110);
            cards.ColumnCount = 5;
            cards.RowCount = 1;
            cards.BackColor = Theme.Surface;
            for (int i = 0; i < 5; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, i == 0 ? 28f : 18f));
            cardLimit = new StatCard(); cardLimit.Caption = "Link limit";
            cardNow = new StatCard(); cardNow.Caption = "Now";
            cardAvg = new StatCard(); cardAvg.Caption = "Average";
            cardPeak = new StatCard(); cardPeak.Caption = "Peak";
            cardSat = new StatCard(); cardSat.Caption = "Bottleneck time";
            StatCard[] all = { cardLimit, cardNow, cardAvg, cardPeak, cardSat };
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

            graph = new PcieGraph();
            graph.Mon = mon;

            epList = new StyledList();
            epList.Columns.Add("Start", Ui.S(110));
            epList.Columns.Add("Duration", Ui.S(90));
            epList.Columns.Add("Direction", Ui.S(130));
            epList.Columns.Add("Peak", Ui.S(80));
            epList.Columns.Add("App in foreground", Ui.S(240));

            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.BackColor = Theme.Surface;
            split.SplitterWidth = Ui.S(14);
            split.Panel1.Controls.Add(Theme.Section("Live PCIe traffic", "Last 2 minutes, % of the theoretical link. Dashed line = measured limit, red areas = bottleneck.", Theme.CardAround(graph)));
            split.Panel2.Controls.Add(Theme.Section("Bottleneck log", "Every stretch of at least " +
                PcieMonitor.MinEpisodeSeconds.ToString("0.0", Txt.Inv) + " s above the threshold, newest first", Theme.CardAround(epList)));
            Resize += delegate
            {
                if (split.Height > Ui.S(200)) split.SplitterDistance = (int)(split.Height * 0.62);
            };

            Controls.Add(split);
            Controls.Add(gap);
            Controls.Add(cards);
            Controls.Add(lblMonInfo);
            Controls.Add(bar);

            monTimer = new System.Windows.Forms.Timer();
            monTimer.Interval = 500;
            monTimer.Tick += delegate { UpdateMonitorUi(); };
            monTimer.Start();
        }

        // Keeps the GPU combo in sync with the GPU list; NVIDIA GPUs first.
        public void SetGpus(List<GpuInfo> gpus)
        {
            string keep = mon.Gpu != null ? mon.Gpu.InstanceId : null;
            var l = GpuDevices.ActiveNvidiaFirst(gpus);
            var ids = new StringBuilder();
            foreach (var g in l) ids.Append(g.InstanceId).Append('|');
            var old = new StringBuilder();
            foreach (var g in monGpus) old.Append(g.InstanceId).Append('|');
            if (ids.ToString() == old.ToString()) return;

            monGpus = l;
            cmbMonGpu.Items.Clear();
            int sel = -1;
            for (int i = 0; i < l.Count; i++)
            {
                cmbMonGpu.Items.Add(l[i].Name);
                if (l[i].InstanceId == keep) sel = i;
            }
            if (sel < 0 && l.Count > 0) sel = 0;
            if (sel >= 0) cmbMonGpu.SelectedIndex = sel;   // fires AttachMonitor
            else AttachMonitor();
        }

        void AttachMonitor()
        {
            int i = cmbMonGpu.SelectedIndex;
            GpuInfo g = i >= 0 && i < monGpus.Count ? monGpus[i] : null;
            if (g != null && mon.Gpu != null && g.InstanceId == mon.Gpu.InstanceId && mon.Error == null) return;
            bool wasRunning = mon.Running;
            mon.Reset();
            shownEpisodes = 0;
            epList.Items.Clear();
            bool ok = mon.Attach(g);
            SyncThreshold();
            if (ok && wasRunning) mon.Start();
            UpdateMonitorUi();
        }

        void ToggleMonitor()
        {
            if (mon.Running) mon.Stop();
            else
            {
                if (mon.Gpu == null || mon.Error != null) AttachMonitor();
                if (mon.Error != null) { UpdateMonitorUi(); return; }
                // Bottlenecks are judged against what the link really delivers, so a measurement is required.
                if (!mon.Measured)
                {
                    if (MessageBox.Show(OwnerWindow,
                        "The real PCIe bandwidth of " + mon.Gpu.Name + " has not been measured yet. Real transfers never reach the theoretical link " +
                        "(an eGPU behind a USB4 / Thunderbolt tunnel may only get half of it), so the bottleneck threshold has to come from a measurement." + Txt.NL + Txt.NL +
                        "The bandwidth test runs now (a few seconds), the threshold is lowered to the measured % of the link, and recording starts right after it.",
                        "Measure PCIe bandwidth first", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                        return;
                    RunBandwidthTest(mon.Gpu, true);
                    if (!mon.Measured) { SetStatus("Recording not started: the bandwidth was not measured."); return; }
                }
                mon.Start();
            }
            UpdateMonitorUi();
        }

        // Shows the threshold the monitor picked for its current 100 % reference.
        void SyncThreshold()
        {
            decimal v = (decimal)Math.Max((double)numThr.Minimum, Math.Min((double)numThr.Maximum, mon.ThresholdPct));
            if (numThr.Value != v) numThr.Value = v;
            lblThrPct.Text = "% of link";
            graph.Invalidate();
        }

        // Pauses recording while the bandwidth test runs (it would show up as a bottleneck).
        public void RunBandwidthTest(GpuInfo g, bool autoRun = false)
        {
            bool resume = mon.Running;
            if (resume) mon.Stop();
            using (var dlg = new BandwidthDialog(g, autoRun)) dlg.ShowDialog(OwnerWindow);
            if (mon.Gpu != null && mon.Gpu.InstanceId == g.InstanceId) { mon.UpdateCapacity(); SyncThreshold(); }
            if (resume) mon.Start();
            UpdateMonitorUi();
        }

        // "Theoretical Gen4 x4 = 1.97 x 4 = 7.88 GB/s": per-lane speed of the current PCIe gen x lanes in use.
        string TheoText(double cap)
        {
            double lane = PcieLink.LaneGBs(mon.CapGen);
            if (lane > 0 && mon.CapLanes > 0)
                return "Theoretical " + PcieLink.Gen(mon.CapGen) + " x" + mon.CapLanes + ": " + lane.ToString("0.00", Txt.Inv) + " x " + mon.CapLanes + " = " + Fmt.GBs(cap) + " GB/s";
            return "Theoretical: " + Fmt.GBs(cap) + " GB/s";
        }


        void UpdateMonitorUi()
        {
            if (IsDisposed) return;
            bool ok = mon.Error == null && mon.Gpu != null;
            graph.Message = mon.Gpu == null ? "No enabled GPU found." : mon.Error;
            btnMonStart.Text = mon.Running ? "Stop recording" : "Start recording";
            Theme.SetButtonColor(btnMonStart, mon.Running ? Theme.Red : Theme.Green);
            btnMonStart.Enabled = ok;
            btnMonMeasure.Enabled = mon.Gpu != null;

            PcieSample last = null;
            double secs, sumRx, sumTx, maxRx, maxTx, sat, capRx, capTx;
            DateTime maxRxT, maxTxT, started;
            int epCount;
            var newEps = new List<PcieEpisode>();
            bool inEpisode;
            lock (mon.Lock)
            {
                if (mon.Samples.Count > 0) last = mon.Samples[mon.Samples.Count - 1];
                secs = mon.Seconds; sumRx = mon.SumRx; sumTx = mon.SumTx; maxRx = mon.MaxRx; maxTx = mon.MaxTx; sat = mon.SatSeconds;
                capRx = mon.CapRx; capTx = mon.CapTx; maxRxT = mon.MaxRxTime; maxTxT = mon.MaxTxTime; started = mon.Started;
                epCount = mon.Episodes.Count;
                for (int i = shownEpisodes; i < mon.Episodes.Count; i++) newEps.Add(mon.Episodes[i]);
                shownEpisodes = mon.Episodes.Count;
                inEpisode = mon.OpenEpisode != null;
            }
            btnMonExport.Enabled = secs > 0;
            btnMonReset.Enabled = secs > 0;

            if (ok)
            {
                lblMonInfo.Text = (mon.Running ? "Recording" : (secs > 0 ? "Paused" : "Not recording")) +
                    (secs > 0 ? "  " + Fmt.Dur(secs) + " (since " + started.ToString("HH:mm:ss", Txt.Inv) + ")" : "") +
                    "   |   Link " + (last != null && mon.Running ? last.Link : (mon.CapLanes > 0 ? PcieLink.Gen(mon.CapGen) + " x" + mon.CapLanes : Txt.NA)) +
                    "   |   100 % = theoretical link, bottleneck line = measured limit";
            }
            else lblMonInfo.Text = "";

            if (!ok) cardLimit.Set(Txt.NA, "", Theme.Text);
            else if (mon.Measured)
                cardLimit.Set(Fmt.GBs(mon.MeasRx) + " / " + Fmt.GBs(mon.MeasTx),
                    "GB/s measured up / down = " + Fmt.Pct(mon.MeasRx, capRx) + " / " + Fmt.Pct(mon.MeasTx, capTx) + Txt.NL +
                    TheoText(capRx), Theme.Accent);
            else
                cardLimit.Set("Not measured",
                    "Measured when you start recording" + Txt.NL +
                    TheoText(capRx), Theme.Text);

            if (last != null && mon.Running)
                cardNow.Set(Fmt.GBs(last.Rx) + " / " + Fmt.GBs(last.Tx), "GB/s up / down" + Txt.NL + Fmt.Pct(last.Rx, capRx) + " / " + Fmt.Pct(last.Tx, capTx) + " of link" +
                    Txt.NL + "App: " + last.App, last.Saturated ? Theme.Red : Theme.Text);
            else cardNow.Set(Txt.NA, mon.Running ? "Waiting for data..." : "Not recording", Theme.Text);

            if (secs > 0)
            {
                cardAvg.Set(Fmt.GBs(sumRx / secs) + " / " + Fmt.GBs(sumTx / secs), "GB/s up / down" + Txt.NL + Fmt.Pct(sumRx / secs, capRx) + " / " + Fmt.Pct(sumTx / secs, capTx) + " of link", Theme.Text);
                cardPeak.Set(Fmt.GBs(maxRx) + " / " + Fmt.GBs(maxTx), "Up at " + maxRxT.ToString("HH:mm:ss", Txt.Inv) + " (" + Fmt.Pct(maxRx, capRx) + ")" + Txt.NL +
                    "Down at " + maxTxT.ToString("HH:mm:ss", Txt.Inv) + " (" + Fmt.Pct(maxTx, capTx) + ")", Theme.Text);
                cardSat.Set(sat.ToString(sat < 60 ? "0.0" : "0", Txt.Inv) + " s",
                    (sat / secs * 100).ToString("0.0", Txt.Inv) + " % of " + Fmt.Dur(secs) + Txt.NL + epCount + " episode(s)" + (inEpisode ? Txt.NL + "Bottleneck right now" : ""),
                    sat > 0 ? Theme.Red : Theme.Green);
            }
            else
            {
                cardAvg.Set(Txt.NA, "", Theme.Text);
                cardPeak.Set(Txt.NA, "", Theme.Text);
                cardSat.Set(Txt.NA, "", Theme.Text);
            }

            if (newEps.Count > 0)
            {
                epList.BeginUpdate();
                foreach (var e in newEps)
                {
                    var it = new ListViewItem(e.Start.ToString("HH:mm:ss", Txt.Inv));
                    it.SubItems.Add(e.Seconds.ToString("0.0", Txt.Inv) + " s");
                    it.SubItems.Add(e.Dir);
                    it.SubItems.Add(e.PeakPct.ToString("0", Txt.Inv) + " %");
                    it.SubItems.Add(e.App);
                    epList.Items.Insert(0, it);
                }
                epList.EndUpdate();
                epList.StretchLast();
            }
            if (Visible) graph.Invalidate();
        }

        void ExportMonitor()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Export PCIe recording";
                dlg.Filter = "CSV file (*.csv)|*.csv";
                dlg.FileName = "pcie-" + DateTime.Now.ToString("yyyyMMdd-HHmm", Txt.Inv) + ".csv";
                if (dlg.ShowDialog(OwnerWindow) != DialogResult.OK) return;
                try { PcieCsvExport.Write(mon, dlg.FileName); SetStatus("Saved " + dlg.FileName); }
                catch (Exception ex) { MessageBox.Show(OwnerWindow, "Could not save:" + Txt.NL + ex.Message, "GPU Toggle", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
