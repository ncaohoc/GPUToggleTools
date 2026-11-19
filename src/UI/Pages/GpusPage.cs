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
        const int C_EGPU = 2, C_AUTO = 3, C_STATUS = 4, C_LOAD = 5, C_TEMP = 6, C_POWER = 7, C_FAN = 8, C_VRAM = 9, C_DISP = 10;

        StyledList list, dispList;
        Button btnEnable, btnDisable, btnRefresh, btnPcie, btnSafeOff, btnSafeRestart;
        CheckBox chkAll, chkLogon, chkShutdown;
        List<GpuInfo> gpus = new List<GpuInfo>();
        HashSet<string> present;   // instance IDs seen by the last scan (null before the first one)
        bool busy, syncing;
        bool active;   // this page is the one shown (Visible is false while the window is hidden in the tray)
        string lastDispSig = "";

        // After every rescan, with all adapters (also disabled and virtual ones).
        public event Action<List<GpuInfo>> GpusChanged;
        // "PCIe bandwidth..." was clicked for this GPU.
        public event Action<GpuInfo> BandwidthTestRequested;
        // Something happened that matters while the window may be hidden (eGPU enabled by itself): text, is it an error.
        public event Action<string, bool> Notify;
        // "Start with Windows" was turned on / off.
        public event Action<bool> RunAtLogonChanged;
        // The eGPU is disabled and Windows is about to shut down (the end-session hook can skip it).
        public event Action SafePowerStarting;

        public override string Title { get { return "GPUs"; } }
        public override string StatusHint
        {
            get { return list.Items.Count + " GPU(s) found. Double-click a row to toggle. Stats refresh every 2 s."; }
        }

        public override void OnActivated() { active = true; }
        public override void OnDeactivated() { active = false; }

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

            var egpuBar = BuildEgpuBar();

            list = new StyledList();
            list.Columns.Add("GPU", Ui.S(220));
            list.Columns.Add("Vendor", Ui.S(72));
            list.Columns.Add("eGPU", Ui.S(62));
            list.Columns.Add("Auto", Ui.S(62));
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
            list.ColumnKinds[C_EGPU] = "check";
            list.ColumnKinds[C_AUTO] = "check";
            list.PillStyle = Pills.GpuStatus;
            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            list.CheckClicked += delegate (ListViewItem it, int col) { ToggleMark((GpuInfo)it.Tag, col == C_EGPU ? EgpuSettings.Egpu : EgpuSettings.Auto); };
            // Double-clicking a tick box only ticks it; elsewhere on the row it enables / disables the GPU.
            list.DoubleClick += delegate
            {
                if (list.IsCheckColumn(list.ColumnAt(list.PointToClient(Cursor.Position)))) return;
                ToggleSelected();
            };
            var colTip = new ToolTip();
            colTip.SetToolTip(list, "eGPU: Safe shut down / restart disables it first." + Txt.NL +
                "Auto: enable it by itself when GPU Toggle starts and when it is plugged in.");

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
            Controls.Add(egpuBar);
            Controls.Add(bar);
        }

        CheckBox BarCheck(string text, string tip)
        {
            var c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.ForeColor = Theme.Text;
            c.Margin = new Padding(0, Ui.S(8), Ui.S(14), 0);
            if (tip != null) new ToolTip().SetToolTip(c, tip);
            return c;
        }

        // Second toolbar row: start with Windows, disable on shut down, safe shut down / restart.
        // The per-GPU marks are the eGPU / Auto tick boxes in the table.
        FlowLayoutPanel BuildEgpuBar()
        {
            var bar = Theme.Toolbar();
            var lbl = new Label();
            lbl.Text = "eGPU:";
            lbl.AutoSize = true;
            lbl.Font = Theme.Semibold;
            lbl.ForeColor = Theme.Muted;
            lbl.Margin = new Padding(0, Ui.S(9), Ui.S(8), 0);

            chkLogon = BarCheck("Start with Windows",
                "Start GPU Toggle hidden in the notification area right after you sign in (after the PIN)." + Txt.NL +
                "Windows asks for admin rights only once, when this is turned on.");
            chkShutdown = BarCheck("Disable eGPU on shut down",
                "When Windows shuts down or restarts (Start menu too) while GPU Toggle is running, disable the eGPU first.");
            chkLogon.Checked = LogonTask.Enabled;
            chkLogon.CheckedChanged += delegate { SetRunAtLogon(); };
            chkShutdown.Checked = EgpuSettings.DisableOnShutdown;
            chkShutdown.CheckedChanged += delegate { EgpuSettings.DisableOnShutdown = chkShutdown.Checked; };

            btnSafeOff = Theme.MakeButton("Safe shut down", Theme.Card, false);
            btnSafeOff.Click += delegate { SafePower(false); };
            btnSafeRestart = Theme.MakeButton("Safe restart", Theme.Card, false);
            btnSafeRestart.Click += delegate { SafePower(true); };
            var tip = new ToolTip();
            tip.SetToolTip(btnSafeOff, "Disable the eGPU(s), then shut down. Unplug the eGPU once the PC is off.");
            tip.SetToolTip(btnSafeRestart, "Disable the eGPU(s), then restart.");

            bar.Controls.Add(lbl);
            bar.Controls.Add(chkLogon);
            bar.Controls.Add(chkShutdown);
            bar.Controls.Add(btnSafeOff);
            bar.Controls.Add(btnSafeRestart);
            return bar;
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

            // Marked eGPUs that are disabled and just showed up (app start, or plugged in): enable them.
            // A GPU that was already there is left alone, so disabling the eGPU by hand still works.
            var arrived = new List<GpuInfo>();
            var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in gpus)
            {
                now.Add(g.InstanceId);
                if (g.IsPhysical && g.ErrorCode == 22 && (present == null || !present.Contains(g.InstanceId))
                    && (EgpuSettings.Flags(g) & EgpuSettings.Auto) != 0)
                    arrived.Add(g);
            }
            present = now;

            list.BeginUpdate();
            list.Items.Clear();
            foreach (var g in gpus)
            {
                if (!g.IsPhysical && !chkAll.Checked) continue;
                var it = new ListViewItem(g.Name);
                it.UseItemStyleForSubItems = false;
                it.SubItems.Add(g.Vendor);
                string egpu, auto;
                MarkCells(g, out egpu, out auto);
                it.SubItems.Add(egpu);
                it.SubItems.Add(auto);
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

            if (active) SetStatus(StatusHint);
            UpdateButtons();
            if (GpusChanged != null) GpusChanged(gpus);
            if (arrived.Count > 0 && !busy) AutoEnable(arrived);
        }

        void AutoEnable(List<GpuInfo> toEnable)
        {
            busy = true; UpdateButtons();
            var done = new List<string>();
            var failed = new List<string>();
            foreach (var g in toEnable)
            {
                SetStatus("eGPU: enabling " + g.Name + "...");
                string output;
                bool ok;
                try { ok = GpuDevices.SetEnabled(g, true, out output); }
                catch (Exception ex) { ok = false; output = ex.Message; }
                if (ok) done.Add(g.Name); else failed.Add(g.Name + " (" + Shell.LastLine(output) + ")");
            }
            busy = false;
            RefreshList();
            string msg = done.Count > 0 ? "eGPU enabled automatically: " + string.Join(", ", done.ToArray()) + "." : "";
            if (failed.Count > 0) msg = (msg + " Could not enable the eGPU: " + string.Join(", ", failed.ToArray())).Trim();
            SetStatus(msg);
            if (Notify != null) Notify(msg, failed.Count > 0);
        }

        // ---------------------------------------------------------------- eGPU options

        // Tick box states of a row: virtual adapters get none, Auto is greyed out until the GPU is marked as eGPU.
        static void MarkCells(GpuInfo g, out string egpu, out string auto)
        {
            if (!g.IsPhysical) { egpu = ""; auto = ""; return; }
            int f = EgpuSettings.Flags(g);
            bool isEgpu = (f & EgpuSettings.Egpu) != 0;
            egpu = isEgpu ? StyledList.CheckOn : StyledList.CheckOff;
            auto = !isEgpu ? StyledList.CheckDisabled : ((f & EgpuSettings.Auto) != 0 ? StyledList.CheckOn : StyledList.CheckOff);
        }

        // A tick box in the eGPU / Auto column was clicked.
        void ToggleMark(GpuInfo g, int flag)
        {
            if (busy || g == null || !g.IsPhysical) return;
            int f = EgpuSettings.Flags(g);
            if (flag == EgpuSettings.Egpu)
                // Marking a GPU as eGPU turns Auto on too (untick Auto to keep enabling it by hand).
                f = (f & EgpuSettings.Egpu) != 0 ? 0 : EgpuSettings.Egpu | EgpuSettings.Auto;
            else if ((f & EgpuSettings.Egpu) != 0)
                f ^= EgpuSettings.Auto;
            else return;
            EgpuSettings.SetFlags(g, f);
            foreach (ListViewItem it in list.Items)
            {
                string egpu, auto;
                MarkCells((GpuInfo)it.Tag, out egpu, out auto);
                it.SubItems[C_EGPU].Text = egpu;
                it.SubItems[C_AUTO].Text = auto;
            }
            list.Invalidate();
            SetStatus(f == 0 ? g.Name + " is no longer marked as eGPU." :
                g.Name + " marked as eGPU" + ((f & EgpuSettings.Auto) != 0 ? ": enabled automatically when GPU Toggle starts or when it is plugged in." : "."));
        }

        void SetRunAtLogon()
        {
            if (syncing) return;
            bool on = chkLogon.Checked;
            string err;
            SetWaitCursor(true);
            bool ok = on ? LogonTask.Create(out err) : LogonTask.Delete(out err);
            SetWaitCursor(false);
            if (!ok)
            {
                syncing = true; chkLogon.Checked = !on; syncing = false;
                MessageBox.Show(OwnerWindow, "Could not " + (on ? "create" : "remove") + " the sign-in task:" + Txt.NL + Txt.NL + err,
                    "GPU Toggle", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            SetStatus(on ? "GPU Toggle now starts hidden in the notification area after you sign in, without a UAC prompt. Closing the window keeps it there."
                         : "GPU Toggle no longer starts with Windows.");
            if (RunAtLogonChanged != null) RunAtLogonChanged(on);
        }

        static string Cap(string s) { return Char.ToUpper(s[0]) + s.Substring(1); }

        // Disables the marked eGPUs, then shuts down / restarts Windows (after asking).
        public void SafePower(bool restart)
        {
            if (busy) return;
            try { gpus = GpuDevices.Scan(); } catch { }
            var marked = Egpu.Marked(gpus);
            string what = restart ? "restart" : "shut down";
            if (marked.Count == 0)
            {
                var r = MessageBox.Show(OwnerWindow,
                    "No plugged-in GPU is marked as eGPU." + Txt.NL + Txt.NL +
                    "Select the eGPU on the GPUs page and tick its eGPU box in the table." + Txt.NL + Txt.NL +
                    Cap(what) + " anyway?",
                    "GPU Toggle", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
            }
            else
            {
                var names = new List<string>();
                foreach (var g in marked) names.Add(g.Name + (g.Enabled ? "" : " (already disabled)"));
                var r = MessageBox.Show(OwnerWindow,
                    "Disable the eGPU, then " + what + " Windows?" + Txt.NL + Txt.NL + "  " + string.Join(Txt.NL + "  ", names.ToArray()) + Txt.NL + Txt.NL +
                    "Save your work first: open apps will be closed." + (restart ? "" : " Unplug the eGPU once the PC is off."),
                    "GPU Toggle", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return;

                busy = true; UpdateButtons();
                SetWaitCursor(true);
                SetStatus("Disabling the eGPU before " + what + "...");
                Application.DoEvents();
                string log;
                bool ok = Egpu.DisableAll(gpus, out log);
                SetWaitCursor(false);
                busy = false;
                RefreshList();
                if (!ok)
                {
                    var r2 = MessageBox.Show(OwnerWindow, "Could not disable the eGPU:" + Txt.NL + Txt.NL + log.Trim() + Txt.NL + Txt.NL +
                        Cap(what) + " anyway?",
                        "GPU Toggle", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (r2 != DialogResult.Yes) return;
                }
            }
            if (SafePowerStarting != null) SafePowerStarting();
            string err;
            if (!Egpu.PowerOff(restart, out err))
                MessageBox.Show(OwnerWindow, "Could not " + what + ":" + Txt.NL + Txt.NL + err, "GPU Toggle", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            btnSafeOff.Enabled = !busy;
            btnSafeRestart.Enabled = !busy;
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
