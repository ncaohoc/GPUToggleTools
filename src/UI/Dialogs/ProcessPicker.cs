// Lists running apps that have a window, so a game that is already open can be added.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace GpuToggle
{
    class ProcessPicker : Form
    {
        StyledList plist;
        public List<string> SelectedPaths = new List<string>();

        public ProcessPicker()
        {
            Text = "Add running app";
            Font = Theme.Regular;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Theme.Hook(this);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(Ui.S(760), Ui.S(460));
            Padding = new Padding(Ui.S(14));

            plist = new StyledList();
            plist.MultiSelect = true;
            plist.Columns.Add("App", Ui.S(200));
            plist.Columns.Add("Window", Ui.S(220));
            plist.Columns.Add("Path", Ui.S(300));
            plist.DoubleClick += delegate { Accept(); };

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.BackColor = Theme.Surface;
            bar.Padding = new Padding(0, Ui.S(10), 0, 0);
            var cancel = Theme.MakeButton("Cancel", Theme.Card, false);
            var add = Theme.MakeButton("Add", Theme.Accent, true);
            cancel.DialogResult = DialogResult.Cancel;
            add.Click += delegate { Accept(); };
            bar.Controls.Add(cancel);
            bar.Controls.Add(add);
            CancelButton = cancel;

            Controls.Add(Theme.Section("Running apps", "Only apps with a window are shown", Theme.CardAround(plist)));
            Controls.Add(bar);
            Shown += delegate { Fill(); };
        }

        void Fill()
        {
            var rows = new List<string[]>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero) continue;
                    string file = p.MainModule.FileName;
                    if (string.IsNullOrEmpty(file) || !seen.Add(file)) continue;
                    rows.Add(new string[] { GpuPrefs.AppName(file), p.MainWindowTitle, file });
                }
                catch { }
                finally { p.Dispose(); }
            }
            rows.Sort(delegate (string[] a, string[] b) { return string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase); });
            plist.BeginUpdate();
            foreach (var r in rows) plist.Items.Add(new ListViewItem(r));
            plist.EndUpdate();
            plist.StretchLast();
        }

        void Accept()
        {
            SelectedPaths.Clear();
            foreach (ListViewItem it in plist.SelectedItems) SelectedPaths.Add(it.SubItems[2].Text);
            if (SelectedPaths.Count == 0) return;
            DialogResult = DialogResult.OK;
        }
    }
}
