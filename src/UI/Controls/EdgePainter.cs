// Repaints the parts of combo boxes and number boxes that Windows draws in light colors whatever the
// control's colors are: the combo border and drop-down button, the number box border and its spin buttons.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace GpuToggle
{
    class EdgePainter : NativeWindow
    {
        readonly Control c;

        EdgePainter(Control c)
        {
            this.c = c;
            if (c.IsHandleCreated) AssignHandle(c.Handle);
            c.HandleCreated += delegate { AssignHandle(c.Handle); };
            c.HandleDestroyed += delegate { ReleaseHandle(); };
        }

        // Once per control: the combo box itself, the number box and its spin buttons.
        static readonly System.Collections.Generic.HashSet<Control> done = new System.Collections.Generic.HashSet<Control>();

        public static void Attach(Control c)
        {
            if (!(c is ComboBox) && !(c is NumericUpDown)) return;
            if (!done.Add(c)) return;
            c.Disposed += delegate { done.Remove(c); };
            var cb = c as ComboBox;
            if (cb != null) { if (cb.FlatStyle == FlatStyle.Flat) new EdgePainter(cb); return; }
            var nu = c as NumericUpDown;
            if (nu != null)
            {
                new EdgePainter(nu);
                if (nu.Controls.Count > 0) new EdgePainter(nu.Controls[0]);   // the spin buttons
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x0F) return;   // WM_PAINT
            try
            {
                if (c is ComboBox) PaintCombo();
                else if (c is NumericUpDown) PaintBorder();
                else PaintSpin();
            }
            catch { }
        }

        void PaintCombo()
        {
            int w = c.Width, h = c.Height;
            int bw = SystemInformation.HorizontalScrollBarArrowWidth;
            using (var g = Graphics.FromHwnd(c.Handle))
            using (var border = new Pen(Theme.Border))
            using (var inner = new Pen(c.BackColor))
            using (var bg = new SolidBrush(c.BackColor))
            {
                var btn = new Rectangle(w - bw - 2, 1, bw + 1, h - 2);
                g.FillRectangle(bg, btn);
                g.DrawRectangle(inner, 1, 1, w - 3, h - 3);
                g.DrawRectangle(border, 0, 0, w - 1, h - 1);
                Arrow(g, btn, false);
            }
        }

        void PaintBorder()
        {
            using (var g = Graphics.FromHwnd(c.Handle))
            using (var border = new Pen(Theme.Border))
                g.DrawRectangle(border, 0, 0, c.Width - 1, c.Height - 1);
        }

        void PaintSpin()
        {
            int w = c.Width, h = c.Height;
            using (var g = Graphics.FromHwnd(c.Handle))
            using (var bg = new SolidBrush(Theme.Card))
            using (var line = new Pen(Theme.Border))
            {
                g.FillRectangle(bg, 0, 0, w, h);
                g.DrawLine(line, 0, 0, 0, h);
                g.DrawLine(line, 0, h / 2, w, h / 2);
                Arrow(g, new Rectangle(0, 0, w, h / 2), true);
                Arrow(g, new Rectangle(0, h / 2, w, h - h / 2), false);
            }
        }

        static void Arrow(Graphics g, Rectangle r, bool up)
        {
            int s = Math.Max(2, Math.Min(Ui.S(4), Math.Min(r.Width, r.Height) / 3));
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            Point[] p = up
                ? new[] { new Point(cx - s, cy + s / 2), new Point(cx + s, cy + s / 2), new Point(cx, cy - s / 2 - 1) }
                : new[] { new Point(cx - s, cy - s / 2), new Point(cx + s, cy - s / 2), new Point(cx, cy + s / 2 + 1) };
            using (var br = new SolidBrush(Theme.Muted)) g.FillPolygon(br, p);
        }
    }
}
