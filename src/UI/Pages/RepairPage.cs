// Repair page: tools that fix a broken GPU setup, grouped apart from the everyday pages.
//   - Driver cleanup: removes the NVIDIA / AMD driver completely (like DDU), optionally from Safe Mode.
//   - Fix code 43: restarts / reinstalls a GPU that Windows stopped with code 43.
// The work runs on a background thread and writes to the log at the bottom of the page.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace GpuToggle
{
    class RepairPage : AppPage
    {
        ComboBox cmbBrand;
        CheckBox chkRestore, chkFolders, chkWu, chkEgpu;
        Button btnClean, btnSafe, btnFix43, btnCheck43;
        Label lbl43;
        TextBox txtLog;
        bool busy, wuSync, brandChosen, egpuChosen;
        List<GpuInfo> gpus = new List<GpuInfo>();

        // Devices were removed / reinstalled: the GPUs page should rescan.
        public event Action DevicesChanged;

        public override string Title { get { return "Repair"; } }
        public override int NavWidth { get { return 96; } }
        public override string StatusHint
        {
            get
            {
                return SafeBoot.InSafeMode
                    ? "Safe Mode: no GPU driver is loaded, a good time for Driver cleanup. Restart afterwards to go back to normal mode."
                    : "Driver cleanup removes the NVIDIA / AMD driver completely. Fix code 43 repairs a GPU that Windows stopped.";
            }
        }

        public RepairPage()
        {
            // Delete the one-time Safe Mode boot entry once we are back in normal mode.
            ThreadPool.QueueUserWorkItem(delegate { try { SafeBoot.Cleanup(); } catch { } });

            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Top;
            grid.Height = Ui.S(300);
            grid.ColumnCount = 2;
            grid.RowCount = 1;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.BackColor = Theme.Surface;
            grid.Padding = new Padding(0, 0, 0, Ui.S(12));

            var left = Theme.Section("Driver cleanup", "Remove the NVIDIA / AMD driver completely", Theme.CardAround(BuildCleanup()));
            left.Margin = new Padding(0, 0, Ui.S(7), 0);
            var right = Theme.Section("Fix code 43", "GPU stopped by Windows", Theme.CardAround(BuildCode43()));
            right.Margin = new Padding(Ui.S(7), 0, 0, 0);
            grid.Controls.Add(left, 0, 0);
            grid.Controls.Add(right, 1, 0);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.WordWrap = true;
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.BackColor = Theme.Card;
            txtLog.ForeColor = Theme.Text;
            txtLog.Font = new Font("Consolas", 9f);
            var logCard = new Panel();
            logCard.BackColor = Theme.Card;
            logCard.Padding = new Padding(Ui.S(10), Ui.S(8), Ui.S(4), Ui.S(8));
            txtLog.Dock = DockStyle.Fill;
            logCard.Controls.Add(txtLog);
            var logSection = Theme.Section("Log", "What the repair tools found and did", Theme.CardAround(logCard));

            Controls.Add(logSection);
            Controls.Add(grid);

            if (SafeBoot.InSafeMode) Log("Running in Safe Mode.");
            UpdateButtons();
        }

        // ---------------------------------------------------------------- layout

        Panel CardBody(out FlowLayoutPanel flow)
        {
            var p = new Panel();
            p.BackColor = Theme.Card;
            p.Padding = new Padding(Ui.S(14), Ui.S(10), Ui.S(10), Ui.S(6));
            flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            flow.AutoScroll = true;
            flow.BackColor = Theme.Card;
            p.Controls.Add(flow);
            return p;
        }

        CheckBox Check(string text, bool on)
        {
            var c = new CheckBox();
            c.Text = text;
            c.Checked = on;
            c.AutoSize = true;
            c.ForeColor = Theme.Text;
            c.BackColor = Theme.Card;
            c.Margin = new Padding(0, Ui.S(2), 0, Ui.S(2));
            return c;
        }

        Label Note(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.MaximumSize = new Size(Ui.S(520), 0);
            l.ForeColor = Theme.Muted;
            l.BackColor = Theme.Card;
            l.Font = Theme.Small;
            l.Margin = new Padding(0, Ui.S(2), 0, 0);
            return l;
        }

        FlowLayoutPanel Row()
        {
            var r = new FlowLayoutPanel();
            r.AutoSize = true;
            r.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            r.WrapContents = false;
            r.BackColor = Theme.Card;
            r.Margin = new Padding(0, Ui.S(6), 0, 0);
            return r;
        }

        Control BuildCleanup()
        {
            FlowLayoutPanel flow;
            var body = CardBody(out flow);

            var brandRow = Row();
            brandRow.Margin = new Padding(0);
            var lbl = new Label();
            lbl.Text = "Driver:";
            lbl.AutoSize = true;
            lbl.ForeColor = Theme.Muted;
            lbl.Font = Theme.Semibold;
            lbl.Margin = new Padding(0, Ui.S(5), Ui.S(6), 0);
            cmbBrand = new ComboBox();
            cmbBrand.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbBrand.Width = Ui.S(120);
            cmbBrand.Items.AddRange(new object[] { "NVIDIA", "AMD" });
            cmbBrand.SelectedIndex = 0;
            cmbBrand.SelectedIndexChanged += delegate { brandChosen = true; };
            brandRow.Controls.Add(lbl);
            brandRow.Controls.Add(cmbBrand);
            flow.Controls.Add(brandRow);

            chkRestore = Check("Create a restore point first", true);
            chkFolders = Check("Also delete leftover folders, settings, shader caches and uninstall entries", true);
            chkWu = Check("Stop Windows Update from installing drivers (applies now)", WuDrivers.Blocked);
            chkWu.Margin = new Padding(0, Ui.S(2), 0, Ui.S(4));
            chkWu.CheckedChanged += delegate { SetWu(); };
            flow.Controls.Add(chkRestore);
            flow.Controls.Add(chkFolders);
            flow.Controls.Add(chkWu);

            var buttons = Row();
            btnClean = Theme.MakeButton("Remove driver...", Theme.Red, true);
            btnClean.Click += delegate { StartCleanup(); };
            btnSafe = Theme.MakeButton(SafeBoot.InSafeMode ? "Restart Windows" : "Restart to Safe Mode...", Theme.Card, false);
            btnSafe.Click += delegate { RestartSafe(); };
            buttons.Controls.Add(btnClean);
            buttons.Controls.Add(btnSafe);
            flow.Controls.Add(buttons);

            flow.Controls.Add(Note("Best done in Safe Mode and offline. The screen may flicker or drop to a low resolution: " +
                "restart afterwards, then install the new driver."));
            var pinNote = Note("Safe Mode has no network and needs your account password: a PIN usually does not work, and a PIN reset " +
                "is impossible there. Stuck at sign-in? Power icon > Restart goes back to normal mode.");
            pinNote.ForeColor = Theme.Orange;
            flow.Controls.Add(pinNote);
            return body;
        }

        Control BuildCode43()
        {
            FlowLayoutPanel flow;
            var body = CardBody(out flow);

            lbl43 = new Label();
            lbl43.AutoSize = true;
            lbl43.MaximumSize = new Size(Ui.S(520), 0);
            lbl43.Font = Theme.Semibold;
            lbl43.ForeColor = Theme.Text;
            lbl43.BackColor = Theme.Card;
            lbl43.Margin = new Padding(0, Ui.S(5), 0, Ui.S(6));
            lbl43.Text = "Checking...";
            flow.Controls.Add(lbl43);

            chkEgpu = Check("I use an eGPU: also apply the eGPU fixes", false);
            chkEgpu.CheckedChanged += delegate { egpuChosen = true; };
            flow.Controls.Add(chkEgpu);
            var egpuNote = Note("For eGPU users only (Thunderbolt / USB4 / OCuLink enclosure). NVIDIA: sets " + Code43Fixer.EgpuFixValue +
                " = 1 in the driver key (the fix from github.com/AleCyriaco/nvidia-error43-fix), then turns off Fast Startup " +
                "and PCIe power saving. Ticked by itself when the GPU sits behind USB4 / Thunderbolt.");
            egpuNote.Margin = new Padding(Ui.S(18), 0, 0, Ui.S(2));
            flow.Controls.Add(egpuNote);

            var buttons = Row();
            btnFix43 = Theme.MakeButton("Fix code 43", Theme.Accent, true);
            btnFix43.Click += delegate { StartFix43(); };
            btnCheck43 = Theme.MakeButton("Check again", Theme.Card, false);
            btnCheck43.Click += delegate { if (DevicesChanged != null) DevicesChanged(); };
            buttons.Controls.Add(btnFix43);
            buttons.Controls.Add(btnCheck43);
            flow.Controls.Add(buttons);

            flow.Controls.Add(Note("Tries the gentlest fix first and stops once the GPU works: restart the device, " +
                "reinstall it, remove ghost adapters, then the eGPU fixes. If it still fails, remove the driver and install it again."));
            var habit = Note("eGPU habit that avoids code 43: Disable + unplug the eGPU before shutting down; " +
                "plug it in + Enable it only after Windows has started.");
            habit.ForeColor = Theme.Orange;
            flow.Controls.Add(habit);
            return body;
        }

        // ---------------------------------------------------------------- state

        public void SetGpus(List<GpuInfo> list)
        {
            gpus = list;
            if (!brandChosen)
            {
                bool nv = false, amd = false;
                foreach (var g in list)
                {
                    if (!g.IsPhysical) continue;
                    if (g.VenId == PciVendor.Nvidia) nv = true;
                    if (g.VenId == PciVendor.Amd) amd = true;
                }
                if (amd && !nv) { cmbBrand.SelectedIndex = 1; brandChosen = false; }
            }
            var bad = new List<string>();
            bool behindTunnel = false;
            foreach (var g in list)
                if (g.IsPhysical && g.ErrorCode == Code43Fixer.Code)
                {
                    bad.Add(g.Name);
                    try { if (Pcie.Tunnel(g.InstanceId) != null) behindTunnel = true; } catch { }
                }
            if (behindTunnel && !egpuChosen && !chkEgpu.Checked) { chkEgpu.Checked = true; egpuChosen = false; }
            if (bad.Count == 0)
            {
                lbl43.Text = "No GPU reports code 43.";
                lbl43.ForeColor = Theme.Green;
            }
            else
            {
                lbl43.Text = "Code 43: " + string.Join(", ", bad.ToArray());
                lbl43.ForeColor = Theme.Red;
            }
            UpdateButtons();
        }

        bool Has43
        {
            get { foreach (var g in gpus) if (g.IsPhysical && g.ErrorCode == Code43Fixer.Code) return true; return false; }
        }

        void UpdateButtons()
        {
            btnClean.Enabled = !busy;
            btnSafe.Enabled = !busy;
            cmbBrand.Enabled = !busy;
            btnFix43.Enabled = !busy && Has43;
            btnCheck43.Enabled = !busy;
        }

        void SetBusy(bool b)
        {
            busy = b;
            SetWaitCursor(b);
            UpdateButtons();
        }

        void Log(string s)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action<string>)Log, s); return; }
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Txt.NL);
        }

        // Runs work on a background thread, then done on the UI thread.
        void RunWorker(ThreadStart work, MethodInvoker done)
        {
            SetBusy(true);
            var t = new Thread(delegate ()
            {
                try { work(); }
                catch (Exception ex) { Log("Error: " + ex.Message); }
                if (!IsDisposed)
                    BeginInvoke((MethodInvoker)delegate
                    {
                        SetBusy(false);
                        if (done != null) done();
                    });
            });
            t.IsBackground = true;
            t.Start();
        }

        void Rescan()
        {
            if (DevicesChanged != null) DevicesChanged();
        }

        // ---------------------------------------------------------------- actions

        void SetWu()
        {
            if (wuSync) return;
            try
            {
                WuDrivers.SetBlocked(chkWu.Checked);
                Log(chkWu.Checked ? "Windows Update will no longer install drivers." : "Windows Update may install drivers again.");
            }
            catch (Exception ex)
            {
                Log("Windows Update setting failed: " + ex.Message);
                wuSync = true;
                chkWu.Checked = WuDrivers.Blocked;
                wuSync = false;
            }
        }

        void StartCleanup()
        {
            string brand = (string)cmbBrand.SelectedItem;
            bool restore = chkRestore.Checked, folders = chkFolders.Checked;
            CleanupPlan plan = null;
            Log("Looking for the " + brand + " driver...");
            RunWorker(delegate
            {
                plan = DriverCleaner.Plan(brand);
                DriverCleaner.LogPlan(plan, Log);
            }, delegate
            {
                if (plan == null) return;
                if (plan.Empty) { SetStatus("No " + brand + " driver found."); Log("Nothing to remove."); return; }
                ConfirmCleanup(plan, restore, folders);
            });
        }

        void ConfirmCleanup(CleanupPlan plan, bool restore, bool folders)
        {
            string text = "Remove the " + plan.Brand + " driver completely?" + Txt.NL + Txt.NL +
                "Found: " + plan.Summary + " (the full list is in the log)." + Txt.NL + Txt.NL +
                "Screens on the " + plan.Brand + " GPU may flicker or drop to a low resolution until you install a driver again.";
            if (!SafeBoot.InSafeMode)
                text += Txt.NL + Txt.NL + "Tip: it is cleanest from Safe Mode (Restart to Safe Mode...), but it also works now.";
            if (!WuDrivers.Blocked)
                text += Txt.NL + Txt.NL + "Windows Update may put an old driver back right away; tick " + Txt.Q + "Stop Windows Update from installing drivers" + Txt.Q + " to prevent it.";
            text += Txt.NL + Txt.NL + "Continue?";
            if (MessageBox.Show(OwnerWindow, text, "GPU Toggle", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                Log("Cancelled.");
                return;
            }
            SetStatus("Removing the " + plan.Brand + " driver...");
            RunWorker(delegate { DriverCleaner.Execute(plan, restore, folders, Log); }, delegate
            {
                SetStatus(plan.Brand + " driver removed. Restart Windows, then install the new driver.");
                Rescan();
                if (MessageBox.Show(OwnerWindow, "The " + plan.Brand + " driver was removed." + Txt.NL + Txt.NL + "Restart Windows now?",
                    "GPU Toggle", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    SafeBoot.Restart();
            });
        }

        void RestartSafe()
        {
            if (SafeBoot.InSafeMode)
            {
                if (MessageBox.Show(OwnerWindow, "Restart Windows now? It starts in normal mode again.", "GPU Toggle",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    SafeBoot.Restart();
                return;
            }
            var ask = MessageBox.Show(OwnerWindow,
                "Restart into Safe Mode once?" + Txt.NL + Txt.NL +
                "Save your work first: Windows restarts right away." + Txt.NL +
                "In Safe Mode, open GPU Toggle again (" + Application.ExecutablePath + "), go to Repair and click Remove driver..." + Txt.NL +
                "The restart after that goes back to normal mode by itself." + Txt.NL + Txt.NL +
                "IMPORTANT - sign-in:" + Txt.NL +
                "- Safe Mode has no network and usually does NOT accept a PIN / Windows Hello: you need the account PASSWORD." + Txt.NL +
                "- If Windows asks you to set up or reset the PIN (e.g. it sees changed hardware), that cannot be done in Safe Mode." + Txt.NL +
                "- Stuck at the sign-in screen? Click the power icon there > Restart: Windows starts in normal mode again." + Txt.NL +
                "Not sure you know the password? Click No and run Remove driver... from normal mode instead." + Txt.NL + Txt.NL +
                "If BitLocker is on, have your recovery key at hand.",
                "GPU Toggle", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (ask != DialogResult.Yes) return;
            string err = SafeBoot.Prepare();
            if (err != null)
            {
                Log("Safe Mode: " + err);
                MessageBox.Show(OwnerWindow, "Could not set up the Safe Mode restart:" + Txt.NL + Txt.NL + err, "GPU Toggle", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Log("Restarting into Safe Mode...");
            SafeBoot.Restart();
        }

        void StartFix43()
        {
            var ask = MessageBox.Show(OwnerWindow,
                "Try to fix code 43 now?" + Txt.NL + Txt.NL +
                "The GPU will be restarted and, if needed, reinstalled. Screens on it may go black for a few seconds." +
                (chkEgpu.Checked ? Txt.NL + Txt.NL + "If that is not enough, the eGPU fixes follow: for NVIDIA cards " + Code43Fixer.EgpuFixValue +
                    " = 1 is written to the driver key, then Fast Startup and PCIe Link State Power Management are turned off." : ""),
                "GPU Toggle", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask != DialogResult.Yes) return;
            bool tweaks = chkEgpu.Checked;
            bool ok = false;
            SetStatus("Fixing code 43...");
            RunWorker(delegate { ok = Code43Fixer.Run(tweaks, Log); }, delegate
            {
                SetStatus(ok ? "Code 43 fixed." : "Code 43 is still there: see the log for what to try next.");
                Rescan();
            });
        }
    }
}
