// Theme: drawing helpers and the styled building blocks (buttons, cards, sections, toolbars).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GpuToggle
{
    static partial class Theme
    {
        public static Color Darken(Color c, int amount)
        {
            return Color.FromArgb(c.A, Math.Max(0, c.R - amount), Math.Max(0, c.G - amount), Math.Max(0, c.B - amount));
        }

        public static GraphicsPath Round(Rectangle r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d < 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Dictionary<Button, object[]> styles = new Dictionary<Button, object[]>();   // button -> { color, primary }

        // Colors a MakeButton button from the current palette; map translates its stored color
        // (e.g. Accent) when the palette changes.
        static void StyleButton(Button b, Dictionary<Color, Color> map)
        {
            object[] st = styles[b];
            Color color = (Color)st[0], nc;
            if (map != null && map.TryGetValue(color, out nc)) { color = nc; st[0] = nc; }
            bool primary = (bool)st[1];
            Color bg = primary ? color : Card;
            Color fg = primary ? Color.White : Text;
            b.BackColor = b.Enabled ? bg : Border;
            b.ForeColor = b.Enabled ? fg : Muted;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = primary ? Darken(bg, 18) : (Dark ? RowAlt : Surface);
            b.FlatAppearance.MouseDownBackColor = primary ? Darken(bg, 34) : Border;
        }

        // Changes a MakeButton button's fill color (e.g. Start recording green <-> red).
        public static void SetButtonColor(Button b, Color color)
        {
            object[] st;
            if (!styles.TryGetValue(b, out st)) { b.BackColor = color; return; }
            st[0] = color;
            StyleButton(b, null);
        }

        // primary = filled colored button, otherwise white button with a border
        public static Button MakeButton(string text, Color color, bool primary)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Font = Semibold;
            b.Cursor = Cursors.Hand;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(Ui.S(92), Ui.S(34));
            b.Padding = new Padding(Ui.S(10), 0, Ui.S(10), 0);
            b.Margin = new Padding(0, 0, Ui.S(8), Ui.S(8));
            styles[b] = new object[] { color, primary };
            StyleButton(b, null);
            b.EnabledChanged += delegate { StyleButton(b, null); };
            b.Disposed += delegate { styles.Remove(b); };
            return b;
        }

        // 1px bordered white card around a control
        public static Panel CardAround(Control inner)
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = Border;
            p.Padding = new Padding(1);
            inner.Dock = DockStyle.Fill;
            p.Controls.Add(inner);
            return p;
        }

        // Section with a bold title + muted subtitle above a body control
        public static Panel Section(string title, string subtitle, Control body)
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = Surface;
            var head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = Ui.S(30);
            head.BackColor = Surface;
            head.Paint += delegate (object s, PaintEventArgs e)
            {
                var flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(e.Graphics, title, Semibold, new Point(0, Ui.S(5)), Text, flags);
                int w = TextRenderer.MeasureText(e.Graphics, title, Semibold, Size.Empty, flags).Width;
                TextRenderer.DrawText(e.Graphics, subtitle, Small, new Point(w + Ui.S(10), Ui.S(7)), Muted, flags);
            };
            body.Dock = DockStyle.Fill;
            p.Controls.Add(body);
            p.Controls.Add(head);
            return p;
        }

        // Keeps a label and its field on one line when a toolbar wraps.
        public static FlowLayoutPanel Group(params Control[] items)
        {
            var g = new FlowLayoutPanel();
            g.AutoSize = true;
            g.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            g.WrapContents = false;
            g.Margin = new Padding(0);
            g.Padding = new Padding(0);
            g.BackColor = Surface;
            g.Controls.AddRange(items);
            return g;
        }

        public static FlowLayoutPanel Toolbar()
        {
            var f = new FlowLayoutPanel();
            f.Dock = DockStyle.Top;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = true;
            f.BackColor = Surface;
            f.Padding = new Padding(0, 0, 0, Ui.S(4));
            return f;
        }
    }
}
