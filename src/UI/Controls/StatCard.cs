// Small card with a caption, a big value and a muted detail line.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace GpuToggle
{
    class StatCard : Control
    {
        public string Caption = "", Value = Txt.NA, Detail = "";
        public Color ValueColor = Theme.Text;

        public StatCard()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            Margin = new Padding(0, 0, Ui.S(10), 0);
        }

        public void Set(string value, string detail, Color color)
        {
            if (value == Value && detail == Detail && color == ValueColor) return;
            Value = value; Detail = detail; ValueColor = color;
            Invalidate();
        }

        // The big value: a smaller size when it does not fit a narrow card, cut with "..." only as a last resort.
        void DrawValue(Graphics g, Rectangle r, TextFormatFlags tf)
        {
            Font full = Theme.Title;
            var mf = tf & ~TextFormatFlags.EndEllipsis;   // measuring with EndEllipsis and no size returns a cut width
            if (TextRenderer.MeasureText(g, Value, full, Size.Empty, mf).Width <= r.Width)
            {
                TextRenderer.DrawText(g, Value, full, r, ValueColor, tf);
                return;
            }
            float size = full.Size;
            Font f = null;
            try
            {
                do
                {
                    if (f != null) f.Dispose();
                    size -= 1f;
                    f = new Font(full.FontFamily, size, full.Style);
                } while (size > full.Size * 0.6f && TextRenderer.MeasureText(g, Value, f, Size.Empty, mf).Width > r.Width);
                int fullH = TextRenderer.MeasureText(g, "Ag", full).Height, h = TextRenderer.MeasureText(g, "Ag", f).Height;
                var rr = new Rectangle(r.X, r.Y + Math.Max(0, (fullH - h) / 2), r.Width, r.Height);
                TextRenderer.DrawText(g, Value, f, rr, ValueColor, tf);
            }
            finally { if (f != null) f.Dispose(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Card);
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            var tf = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int x = Ui.S(12), w = Width - Ui.S(20);
            TextRenderer.DrawText(g, Caption.ToUpperInvariant(), Theme.SmallBold, new Rectangle(x, Ui.S(8), w, Ui.S(18)), Theme.Muted, tf);
            DrawValue(g, new Rectangle(x, Ui.S(26), w, Ui.S(30)), tf);
            int y = Ui.S(58), lh = TextRenderer.MeasureText("Ag", Theme.Small).Height;
            foreach (var line in (Detail ?? "").Split((char)10))
            {
                if (y + lh > Height - Ui.S(2)) break;
                TextRenderer.DrawText(g, line.TrimEnd((char)13), Theme.Small, new Rectangle(x, y, w, lh), Theme.Muted, tf);
                y += lh;
            }
        }
    }
}
