// Apps / Games page: which GPU each game or app should use (stored where Windows Graphics settings
// keeps it), launching it to test, and which GPU it really runs on.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace GpuToggle
{
    class AppsPage : AppPage
    {
        // Apps list columns
        const int A_PREF = 1, A_RUN = 2;

        StyledList appsList;
        Button btnAddApp, btnAddRunning, btnRemoveApp, btnLaunch, btnGfxSettings, btnPrefAuto, btnPrefSave, btnPrefPerf;
        Label lblPrefInfo;
        string savingGpu, perfGpu;

        public override string Title { get { return "Apps / Games"; } }

        public override void OnActivated() { RefreshApps(); }

        // Paths of the listed apps (the stats poller looks up which GPU each one runs on).
        public List<string> AppPaths()
        {
            var l = new List<string>();
            foreach (ListViewItem it in appsList.Items) l.Add(((AppEntry)it.Tag).Path);
            return l;
        }

        public AppsPage()
        {
            // Info card: which GPU Windows uses for each preference
            var info = new Panel();
            info.Dock = DockStyle.Top;
            info.Height = Ui.S(84);
            info.BackColor = Theme.Card;
            info.Padding = new Padding(Ui.S(16), Ui.S(10), Ui.S(16), Ui.S(10));
            info.Paint += delegate (object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, info.Width - 1, info.Height - 1);
            };
            lblPrefInfo = new Label();
            lblPrefInfo.Dock = DockStyle.Fill;
            lblPrefInfo.ForeColor = Theme.Text;
            lblPrefInfo.BackColor = Theme.Card;
            info.Controls.Add(lblPrefInfo);

            var gap = new Panel();
            gap.Dock = DockStyle.Top;
            gap.Height = Ui.S(12);
            gap.BackColor = Theme.Surface;

            var row1 = Theme.Toolbar();
            btnAddApp = Theme.MakeButton("Add app...", Theme.Accent, true);
            btnAddRunning = Theme.MakeButton("Add running app...", Theme.Card, false);
            btnRemoveApp = Theme.MakeButton("Remove", Theme.Card, false);
            btnLaunch = Theme.MakeButton("Launch to test", Theme.Green, true);
            btnGfxSettings = Theme.MakeButton("Windows Graphics settings", Theme.Card, false);
            btnAddApp.Click += delegate { AddAppFromFile(); };
            btnAddRunning.Click += delegate { AddRunningApp(); };
            btnRemoveApp.Click += delegate { RemoveSelectedApps(); };
            btnLaunch.Click += delegate { LaunchSelectedApp(); };
            btnGfxSettings.Click += delegate
            {
                try { Process.Start("ms-settings:display-advancedgraphics"); }
                catch (Exception ex) { SetStatus("Could not open Windows settings: " + ex.Message); }
            };
            row1.Controls.Add(btnAddApp);
            row1.Controls.Add(btnAddRunning);
            row1.Controls.Add(btnRemoveApp);
            row1.Controls.Add(btnLaunch);
            row1.Controls.Add(btnGfxSettings);

            var row2 = Theme.Toolbar();
            var lblRun = new Label();
            lblRun.Text = "Run selected app on:";
            lblRun.AutoSize = true;
            lblRun.ForeColor = Theme.Muted;
            lblRun.Font = Theme.Semibold;
            lblRun.Margin = new Padding(0, Ui.S(8), Ui.S(10), 0);
            btnPrefAuto = Theme.MakeButton("Let Windows decide", Theme.Card, false);
            btnPrefSave = Theme.MakeButton("Power saving", Theme.Card, false);
            btnPrefPerf = Theme.MakeButton("High performance", Theme.Card, false);
            btnPrefAuto.Click += delegate { SetSelectedPref(0); };
            btnPrefSave.Click += delegate { SetSelectedPref(1); };
            btnPrefPerf.Click += delegate { SetSelectedPref(2); };
            row2.Controls.Add(lblRun);
            row2.Controls.Add(btnPrefAuto);
            row2.Controls.Add(btnPrefSave);
            row2.Controls.Add(btnPrefPerf);

            appsList = new StyledList();
            appsList.MultiSelect = true;
            appsList.Columns.Add("App", Ui.S(240));
            appsList.Columns.Add("GPU preference", Ui.S(300));
            appsList.Columns.Add("Running on", Ui.S(280));
            appsList.Columns.Add("Path", Ui.S(400));
            appsList.ColumnKinds[A_PREF] = "pill";
            appsList.PillStyle = Pills.GpuPreference;
            appsList.SelectedIndexChanged += delegate { UpdateAppButtons(); };
            appsList.DoubleClick += delegate { LaunchSelectedApp(); };
            appsList.AllowDrop = true;
            appsList.DragEnter += delegate (object s, DragEventArgs e)
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            appsList.DragDrop += delegate (object s, DragEventArgs e)
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) AddApps(files);
            };

            var section = Theme.Section("Apps & games", "Drop .exe files here. Double-click to launch. Stored in Windows Graphics settings.",
                Theme.CardAround(appsList));

            Controls.Add(section);
            Controls.Add(row2);
            Controls.Add(row1);
            Controls.Add(gap);
            Controls.Add(info);
        }

        public void ApplyStats(StatsResult r)
        {
            appsList.BeginUpdate();
            foreach (ListViewItem it in appsList.Items)
            {
                var a = (AppEntry)it.Tag;
                string txt;
                if (!r.AppRunning.TryGetValue(a.Path, out txt)) continue;
                it.SubItems[A_RUN].Text = txt;
                it.SubItems[A_RUN].ForeColor = txt.StartsWith("Not running", StringComparison.Ordinal) || txt.StartsWith("Running -", StringComparison.Ordinal)
                    ? Theme.Muted : Theme.Text;
            }
            appsList.EndUpdate();
        }

        string PrefLabel(int pref)
        {
            string gpu = pref == 1 ? savingGpu : (pref == 2 ? perfGpu : null);
            return GpuPrefs.PrefName(pref) + (string.IsNullOrEmpty(gpu) ? "" : "  -  " + gpu);
        }

        List<AppEntry> SelectedApps()
        {
            var l = new List<AppEntry>();
            foreach (ListViewItem it in appsList.SelectedItems) l.Add((AppEntry)it.Tag);
            return l;
        }

        public void RefreshApps()
        {
            try { savingGpu = Dxgi.PreferredAdapterName(1); } catch { savingGpu = null; }
            try { perfGpu = Dxgi.PreferredAdapterName(2); } catch { perfGpu = null; }
            lblPrefInfo.Text =
                "Power saving GPU:   " + (savingGpu ?? "unknown") + Txt.NL +
                "High performance GPU:   " + (perfGpu ?? "unknown") + Txt.NL +
                "A new GPU preference takes effect the next time the app is started.";
            btnPrefSave.Text = "Power saving" + (string.IsNullOrEmpty(savingGpu) ? "" : " (" + savingGpu + ")");
            btnPrefPerf.Text = "High performance" + (string.IsNullOrEmpty(perfGpu) ? "" : " (" + perfGpu + ")");

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in SelectedApps()) keep.Add(a.Path);
            var running = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem it in appsList.Items) running[((AppEntry)it.Tag).Path] = it.SubItems[A_RUN].Text;

            List<AppEntry> apps;
            try { apps = GpuPrefs.Load(); }
            catch (Exception ex) { SetStatus("Could not read GPU preferences: " + ex.Message); return; }

            appsList.BeginUpdate();
            appsList.Items.Clear();
            foreach (var a in apps)
            {
                var it = new ListViewItem(a.Name);
                it.UseItemStyleForSubItems = false;
                it.SubItems.Add(PrefLabel(a.Pref));
                string run;
                if (!running.TryGetValue(a.Path, out run)) run = Txt.NA;
                it.SubItems.Add(run).ForeColor = Theme.Muted;
                it.SubItems.Add(a.Path).ForeColor = Theme.Muted;
                it.Tag = a;
                appsList.Items.Add(it);
                if (keep.Contains(a.Path)) it.Selected = true;
            }
            appsList.EndUpdate();
            appsList.StretchLast();
            UpdateAppButtons();
            if (Visible)
                SetStatus(apps.Count == 0
                    ? "No apps yet. Click Add app... or drop a game .exe on the list."
                    : apps.Count + " app(s). Select one, pick a GPU, then Launch to test.");
        }

        void UpdateAppButtons()
        {
            int n = appsList.SelectedItems.Count;
            btnRemoveApp.Enabled = n > 0;
            btnLaunch.Enabled = n == 1;
            btnPrefAuto.Enabled = n > 0;
            btnPrefSave.Enabled = n > 0;
            btnPrefPerf.Enabled = n > 0;
        }

        void AddApps(IEnumerable<string> files)
        {
            int added = 0;
            string last = null;
            foreach (var f in files)
            {
                if (string.IsNullOrEmpty(f) || !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                try { GpuPrefs.Add(f); added++; last = f; }
                catch (Exception ex) { SetStatus("Could not add " + f + ": " + ex.Message); }
            }
            if (added == 0) { SetStatus("Only .exe files can be added."); return; }
            RefreshApps();
            foreach (ListViewItem it in appsList.Items)
                it.Selected = string.Equals(((AppEntry)it.Tag).Path, last, StringComparison.OrdinalIgnoreCase);
            SetStatus("Added " + added + " app(s). Now pick a GPU for it.");
        }

        void AddAppFromFile()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Choose a game or app";
                dlg.Filter = "Applications (*.exe)|*.exe";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(OwnerWindow) == DialogResult.OK) AddApps(dlg.FileNames);
            }
        }

        void AddRunningApp()
        {
            using (var dlg = new ProcessPicker())
            {
                if (dlg.ShowDialog(OwnerWindow) == DialogResult.OK) AddApps(dlg.SelectedPaths);
            }
        }

        void RemoveSelectedApps()
        {
            var apps = SelectedApps();
            if (apps.Count == 0) return;
            foreach (var a in apps)
            {
                try { GpuPrefs.Remove(a.Path); }
                catch (Exception ex) { SetStatus("Could not remove " + a.Name + ": " + ex.Message); return; }
            }
            RefreshApps();
            SetStatus("Removed " + apps.Count + " app(s). They go back to Windows' default GPU choice.");
        }

        void SetSelectedPref(int pref)
        {
            var apps = SelectedApps();
            if (apps.Count == 0) return;
            foreach (var a in apps)
            {
                try { GpuPrefs.Set(a.Path, pref); }
                catch (Exception ex) { SetStatus("Could not save: " + ex.Message); return; }
            }
            RefreshApps();
            SetStatus((apps.Count == 1 ? apps[0].Name : apps.Count + " apps") + " set to " + PrefLabel(pref) +
                ". Restart the app if it is already running.");
        }

        void LaunchSelectedApp()
        {
            var apps = SelectedApps();
            if (apps.Count != 1) return;
            var a = apps[0];
            if (!File.Exists(a.Path))
            {
                MessageBox.Show(OwnerWindow, "File not found:" + Txt.NL + a.Path, "GPU Toggle", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                // Launch through Explorer so the game runs as a normal (non-admin) process.
                Process.Start("explorer.exe", Txt.Q + a.Path + Txt.Q);
                SetStatus("Launched " + a.Name + ". Watch the Running on column to see which GPU it uses.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(OwnerWindow, "Could not launch:" + Txt.NL + ex.Message, "GPU Toggle", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
