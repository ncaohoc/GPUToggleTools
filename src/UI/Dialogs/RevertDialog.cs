// Countdown after disabling a GPU: re-enables it unless the user clicks Keep.

using System.Drawing;
using System.Windows.Forms;

namespace GpuToggle
{
    class RevertDialog : Form
    {
        int left;
        string gpuName;
        Label lbl;
        System.Windows.Forms.Timer timer;

        public RevertDialog(string gpuName, int seconds)
        {
            this.gpuName = gpuName;
            left = seconds;

            Text = "Keep this change?";
            Font = Theme.Regular;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            Theme.Hook(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(Ui.S(460), Ui.S(156));

            var accent = new Panel();
            accent.Dock = DockStyle.Top;
            accent.Height = Ui.S(4);
            accent.BackColor = Theme.Orange;

            lbl = new Label();
            lbl.Location = new Point(Ui.S(18), Ui.S(20));
            lbl.Size = new Size(Ui.S(424), Ui.S(72));
            lbl.ForeColor = Theme.Text;

            var keep = Theme.MakeButton("Keep", Theme.Green, true);
            keep.DialogResult = DialogResult.OK;
            keep.AutoSize = false;
            keep.Size = new Size(Ui.S(110), Ui.S(34));
            keep.Location = new Point(Ui.S(214), Ui.S(104));

            var revert = Theme.MakeButton("Revert now", Theme.Card, false);
            revert.DialogResult = DialogResult.Cancel;
            revert.AutoSize = false;
            revert.Size = new Size(Ui.S(110), Ui.S(34));
            revert.Location = new Point(Ui.S(332), Ui.S(104));

            Controls.Add(lbl);
            Controls.Add(keep);
            Controls.Add(revert);
            Controls.Add(accent);
            AcceptButton = keep;
            CancelButton = revert;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate
            {
                left--;
                UpdateText();
                if (left <= 0) { timer.Stop(); DialogResult = DialogResult.Cancel; }
            };
            Shown += delegate { Activate(); timer.Start(); };
            FormClosed += delegate { timer.Stop(); };
            UpdateText();
        }

        void UpdateText()
        {
            lbl.Text = Txt.Q + gpuName + Txt.Q + " is now disabled." + Txt.NL + Txt.NL + "Keep this setting? Reverting in " + left + " s...";
        }
    }
}
