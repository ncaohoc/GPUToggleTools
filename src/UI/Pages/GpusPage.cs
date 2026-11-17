// GPUs page: the graphics adapters (enable / disable, live stats, bandwidth test) and which monitor each one drives.

using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace GpuToggle
{
    class GpusPage : AppPage
    {
        const int RevertSeconds = 15;
        // GPU list columns
        const int C_STATUS = 2, C_LOAD = 3, C_TEMP = 4, C_POWER = 5, C_FAN = 6, C_VRAM = 7, C_DISP = 8;

        StyledList list, dispList;
        Button btnEnable, btnDisable, btnRefresh, btnPcie;
        CheckBox chkAll;
        List<GpuInfo> gpus = new List<GpuInfo>();
        bool busy;
        string lastDispSig = "";

        // After every rescan, with all adapters (also disabled and virtual ones).
        public event Action<List<GpuInfo>> GpusChanged;
        // "PCIe bandwidth..." was clicked for this GPU.
        public event Action<GpuInfo> BandwidthTestRequested;

        public override string Title { get { return "GPUs"; } }
        public override string StatusHint
        {
            get { return list.Items.Count + " GPU(s) found. Double-click a row to toggle. Stats refresh every 2 s."; }
        }

        public bool Busy { get { return busy; } }
        public List<GpuInfo> Gpus { get { return gpus; } }

        public GpusPage()
        {
            var bar = Theme.Toolbar();
            btnEnable = Theme.MakeButton("Enable", Theme.Green, true);
            btnDisable = Theme.MakeButton("Disable", Theme.Red, true);
            btnRefresh = Theme.MakeButton("Refresh", Theme.Card, false);
            btnRefresh.Click += delegate { RefreshList(); };
            btnDisable.Click += delegate { DisableSelected(); };
            btnEnable.Click += delegate { EnableSelected(); };
            btnPcie = Theme.MakeButton("PCIe bandwidth...", Theme.Card, false);
            btnPcie.Click += delegate { TestBandwidth(); };

            chkAll = new CheckBox();
            chkAll.Text = "Show virtual adapters";
            chkAll.AutoSize = true;
            chkAll.ForeColor = Theme.Muted;
            chkAll.Margin = new Padding(Ui.S(8), Ui.S(8), 0, 0);
            chkAll.CheckedChanged += delegate { RefreshList(); };

            bar.Controls.Add(btnEnable);
            bar.Controls.Add(btnDisable);
            bar.Controls.Add(btnRefresh);
            bar.Controls.Add(btnPcie);
            bar.Controls.Add(chkAll);

            list = new StyledList();
            list.Columns.Add("GPU", Ui.S(220));
            list.Columns.Add("Vendor", Ui.S(72));
            list.Columns.Add("Status", Ui.S(96));
            list.Columns.Add("Load", Ui.S(120));
            list.Columns.Add("Temp", Ui.S(62));
            list.Columns.Add("Power / TDP", Ui.S(110));
            list.Columns.Add("Fan", Ui.S(96));
            list.Columns.Add("VRAM", Ui.S(112));
            list.Columns.Add("Displays", Ui.S(78));
            list.Columns.Add("Instance ID", Ui.S(300));
            list.ColumnKinds[C_STATUS] = "pill";
            list.ColumnKinds[C_LOAD] = "bar";
            list.PillStyle = Pills.GpuStatus;
            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            list.DoubleClick += delegate { ToggleSelected(); };

            dispList = new StyledList();
            dispList.Columns.Add("Output", Ui.S(160));
            dispList.Columns.Add("Monitor", Ui.S(240));
            dispList.Columns.Add("Mode", Ui.S(200));
            dispList.Columns.Add("Driven by GPU", Ui.S(300));

            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.BackColor = Theme.Surface;
            split.SplitterWidth = Ui.S(14);
            split.Panel1.Controls.Add(Theme.Section("Graphics adapters", "Double-click a row to enable / disable it", Theme.CardAround(list)));
            split.Panel2.Controls.Add(Theme.Section("Displays", "Which monitor is driven by which GPU", Theme.CardAround(dispList)));
            Resize += delegate
            {
                if (split.Height > Ui.S(200)) split.SplitterDistance = (int)(split.Height * 0.55);
            };

            Controls.Add(split);
            Controls.Add(bar);
        }

        GpuInfo Selected
        {
            get { return list.SelectedItems.Count == 0 ? null : (GpuInfo)list.SelectedItems[0].Tag; }
        }

        public void RefreshList()
        {
            string keepId = Selected != null ? Selected.InstanceId : null;
            try { gpus = GpuDevices.Scan(); }
            catch (Exception ex) { SetStatus("Scan failed: " + ex.Message); return; }

            list.BeginUpdate();
            list.Items.Clear();
            foreach (var g in gpus)
            {
                if (!g.IsPhysical && !chkAll.Checked) continue;
                var it = new ListViewItem(g.Name);
                it.UseItemStyleForSubItems = false;
                it.SubItems.Add(g.Vendor);
                it.SubItems.Add(g.StatusText);
                for (int i = C_LOAD; i <= C_DISP; i++) it.SubItems.Add(Txt.NA);
                it.SubItems.Add(g.InstanceId);
                foreach (ListViewItem.ListViewSubItem si in it.SubItems) si.ForeColor = g.Enabled ? Theme.Text : Theme.Muted;
                it.Tag = g;
                list.Items.Add(it);
                if (g.InstanceId == keepId) it.Selected = true;
            }
            if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.Items[0].Selected = true;
            list.EndUpdate();
            list.StretchLast();

            if (Visible) SetStatus(StatusHint);
            UpdateButtons();
            if (GpusChanged != null) GpusChanged(gpus);
        }

        public void ApplyStats(StatsResult r)
        {
            if (IsDisposed) return;
            list.BeginUpdate();
            foreach (ListViewItem it in list.Items)
            {
                var g = (GpuInfo)it.Tag;
                GpuStats s;
                if (!r.ByInstance.TryGetValue(g.InstanceId, out s)) s = new GpuStats();
                it.SubItems[C_LOAD].Text = s.Load;
                it.SubItems[C_TEMP].Text = s.Temp;
                it.SubItems[C_TEMP].ForeColor = s.TempValue >= 83 ? Theme.Red : (s.TempValue >= 75 ? Theme.Orange : Theme.Text);
                it.SubItems[C_POWER].Text = s.Power;
                it.SubItems[C_FAN].Text = s.Fan;
                it.SubItems[C_VRAM].Text = s.Vram;
                int n = 0;
                foreach (var d in r.Displays)
                    if (!string.IsNullOrEmpty(d.GpuDeviceId) && g.InstanceId.StartsWith(d.GpuDeviceId, StringComparison.OrdinalIgnoreCase)) n++;
                it.SubItems[C_DISP].Text = n.ToString();
            }
            list.EndUpdate();

            string sig = "";
            foreach (var d in r.Displays) sig += d.Output + d.Monitor + d.Mode + d.GpuName + d.Primary + "|";
            if (sig != lastDispSig)
            {
                lastDispSig = sig;
                dispList.BeginUpdate();
                dispList.Items.Clear();
                foreach (var d in r.Displays)
                {
                    var it = new ListViewItem(d.Output + (d.Primary ? "  (primary)" : ""));
                    it.SubItems.Add(d.Monitor);
                    it.SubItems.Add(d.Mode);
                    it.SubItems.Add(d.GpuName);
                    dispList.Items.Add(it);
                }
                dispList.EndUpdate();
                dispList.StretchLast();
            }
        }

        void UpdateButtons()
        {
            var g = Selected;
            btnEnable.Enabled = !busy && g != null && !g.Enabled;
            btnDisable.Enabled = !busy && g != null && g.Enabled;
            btnRefresh.Enabled = !busy;
            btnPcie.Enabled = !busy && g != null && g.Enabled && g.IsPhysical;
        }

        void TestBandwidth()
        {
            var g = Selected;
            if (g == null || !g.Enabled || !g.IsPhysical) return;
            if (BandwidthTestRequested != null) BandwidthTestRequested(g);
        }

        void ToggleSelected()
        {
            var g = Selected;
            if (g == null) return;
            if (g.Enabled) DisableSelected(); else EnableSelected();
        }

        bool Apply(GpuInfo g, bool enable)
        {
            busy = true; UpdateButtons();
            SetWaitCursor(true);
            SetStatus((enable ? "Enabling " : "Disabling ") + g.Name + "...");
            Application.DoEvents();
            string output;
            bool ok;
            try { ok = GpuDevices.SetEnabled(g, enable, out output); }
            catch (Exception ex) { ok = false; output = ex.Message; }
            SetWaitCursor(false);
            busy = false;
            RefreshList();
            if (!ok)
                MessageBox.Show(OwnerWindow, "Failed:" + Txt.NL + Txt.NL + output.Trim(), "GPU Toggle", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                SetStatus(g.Name + (enable ? " enabled." : " disabled."));
            return ok;
        }

        void EnableSelected()
        {
            var g = Selected;
            if (g == null || g.Enabled) return;
            Apply(g, true);
        }

        void DisableSelected()
        {
            var g = Selected;
            if (g == null || !g.Enabled) return;

            int othersOn = 0;
            foreach (var o in gpus)
                if (o.IsPhysical && o.Enabled && o.InstanceId != g.InstanceId) othersOn++;
            if (g.IsPhysical && othersOn == 0)
            {
                var warn = MessageBox.Show(OwnerWindow,
                    "This is the last active GPU." + Txt.NL + Txt.NL +
                    "Windows will fall back to the Microsoft Basic Display Adapter (firmware framebuffer): " +
                    "low resolution, no GPU acceleration. On some machines the screen may stay black." + Txt.NL + Txt.NL +
                    "Continue anyway?",
                    "GPU Toggle", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (warn != DialogResult.Yes) return;
            }

            var ask = MessageBox.Show(OwnerWindow,
                "Disable " + Txt.Q + g.Name + Txt.Q + "?" + Txt.NL + Txt.NL +
                "Any screen connected to this GPU will go black (for the iGPU, that's the built-in screen)." + Txt.NL +
                "If you don't click Keep within " + RevertSeconds + " seconds, it will be re-enabled automatically.",
                "GPU Toggle", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask != DialogResult.Yes) return;

            if (!Apply(g, false)) return;

            using (var dlg = new RevertDialog(g.Name, RevertSeconds))
            {
                if (dlg.ShowDialog(OwnerWindow) != DialogResult.OK)
                {
                    SetStatus("Not confirmed - re-enabling " + g.Name + "...");
                    Apply(g, true);
                }
            }
        }
    }
}
