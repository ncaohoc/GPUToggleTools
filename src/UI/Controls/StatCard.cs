// Small card with a caption, a big value and a muted detail line.

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

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Card);
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            var tf = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int x = Ui.S(12), w = Width - Ui.S(20);
            TextRenderer.DrawText(g, Caption.ToUpperInvariant(), Theme.SmallBold, new Rectangle(x, Ui.S(8), w, Ui.S(18)), Theme.Muted, tf);
            TextRenderer.DrawText(g, Value, Theme.Title, new Rectangle(x, Ui.S(26), w, Ui.S(30)), ValueColor, tf);
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
