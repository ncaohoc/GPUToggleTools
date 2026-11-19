// ListView with flat header, tall rows, alternating rows, status pills and load bars.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace GpuToggle
{
    class StyledList : ListView
    {
        public Dictionary<int, string> ColumnKinds = new Dictionary<int, string>();   // "pill", "bar" or "check"
        public Func<string, Color[]> PillStyle;                                       // text -> { background, foreground }

        // "check" cells: text CheckOn / CheckOff draw a tick box, CheckDisabled a greyed one, "" nothing.
        public const string CheckOn = "1", CheckOff = "0", CheckDisabled = "d";
        // A tick box was clicked: row, column.
        public event Action<ListViewItem, int> CheckClicked;

        // Column under a point (client coordinates), -1 if none.
        public int ColumnAt(Point p)
        {
            var hit = HitTest(p);
            if (hit.Item == null || hit.SubItem == null) return -1;
            return hit.Item.SubItems.IndexOf(hit.SubItem);
        }

        public bool IsCheckColumn(int col)
        {
            string k;
            return col >= 0 && ColumnKinds.TryGetValue(col, out k) && k == "check";
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var hit = HitTest(e.Location);
            if (hit.Item == null || hit.SubItem == null) return;
            int col = hit.Item.SubItems.IndexOf(hit.SubItem);
            string t = hit.SubItem.Text;
            if (IsCheckColumn(col) && (t == CheckOn || t == CheckOff) && CheckClicked != null)
            {
                hit.Item.Selected = true;
                CheckClicked(hit.Item, col);
            }
        }

        public StyledList()
        {
            DoubleBuffered = true;
            OwnerDraw = true;
            View = View.Details;
            FullRowSelect = true;
            MultiSelect = false;
            HideSelection = false;
            BorderStyle = BorderStyle.None;
            Font = Theme.Regular;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            var il = new ImageList();
            il.ImageSize = new Size(1, Ui.S(30));
            SmallImageList = il;
            DrawColumnHeader += OnHeader;
            DrawItem += delegate (object s, DrawListViewItemEventArgs e) { };
            DrawSubItem += OnSubItem;
            SelectedIndexChanged += delegate { Invalidate(); };
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            StretchLast();
        }

        public void StretchLast()
        {
            if (Columns.Count == 0) return;
            int used = 0;
            for (int i = 0; i < Columns.Count - 1; i++) used += Columns[i].Width;
            int w = ClientSize.Width - used;
            if (w > Ui.S(80)) Columns[Columns.Count - 1].Width = w;
        }

        void OnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var bg = new SolidBrush(Theme.RowAlt)) e.Graphics.FillRectangle(bg, e.Bounds);
            using (var pen = new Pen(Theme.Border))
            {
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + Ui.S(6), e.Bounds.Right - 1, e.Bounds.Bottom - Ui.S(6));
            }
            var r = new Rectangle(e.Bounds.X + Ui.S(10), e.Bounds.Y, Math.Max(0, e.Bounds.Width - Ui.S(14)), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), Theme.SmallBold, r, Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        void OnSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            var g = e.Graphics;
            var b = e.Bounds;
            if (e.ColumnIndex == 0 && Columns.Count > 0) b.Width = Columns[0].Width;
            Color bg = e.Item.Selected ? Theme.RowSel : (e.ItemIndex % 2 == 1 ? Theme.RowAlt : Theme.Card);
            using (var br = new SolidBrush(bg)) g.FillRectangle(br, b);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);

            string text = e.SubItem.Text ?? "";
            var inner = new Rectangle(b.X + Ui.S(10), b.Y, Math.Max(0, b.Width - Ui.S(14)), b.Height);
            string kind;
            ColumnKinds.TryGetValue(e.ColumnIndex, out kind);

            if (kind == "check") { if (text.Length > 0) DrawCheck(g, inner, text == CheckOn, text == CheckDisabled); return; }
            if (kind == "pill" && text.Length > 0 && text != Txt.NA) { DrawPill(g, inner, text); return; }
            float v;
            if (kind == "bar" && LeadingNumber(text, out v)) { DrawBar(g, inner, text, v); return; }

            Color fg = e.SubItem.ForeColor;
            if (fg.IsEmpty) fg = Theme.Text;
            Font f = e.ColumnIndex == 0 ? Theme.Semibold : Theme.Regular;
            TextRenderer.DrawText(g, text, f, inner, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        void DrawCheck(Graphics g, Rectangle inner, bool on, bool disabled)
        {
            int s = Ui.S(16);
            var r = new Rectangle(inner.X + Ui.S(2), inner.Y + (inner.Height - s) / 2, s, s);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(r, Ui.S(3)))
            {
                if (on)
                    using (var br = new SolidBrush(Theme.Accent)) g.FillPath(br, path);
                else
                {
                    using (var br = new SolidBrush(disabled ? Theme.RowAlt : Theme.Card)) g.FillPath(br, path);
                    using (var pen = new Pen(disabled ? Theme.Border : Theme.Muted, Math.Max(1f, Ui.S(1)))) g.DrawPath(pen, path);
                }
            }
            if (on)
            {
                using (var pen = new Pen(Color.White, Math.Max(2f, Ui.S(2))))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new PointF[] {
                        new PointF(r.X + s * 0.24f, r.Y + s * 0.52f),
                        new PointF(r.X + s * 0.43f, r.Y + s * 0.70f),
                        new PointF(r.X + s * 0.77f, r.Y + s * 0.32f) });
                }
            }
            g.SmoothingMode = old;
        }

        void DrawPill(Graphics g, Rectangle inner, string text)
        {
            Color[] c = PillStyle != null ? PillStyle(text) : new Color[] { Theme.Border, Theme.Text };
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            Size ts = TextRenderer.MeasureText(g, text, Theme.SmallBold, Size.Empty, flags);
            int h = Ui.S(22);
            int w = Math.Min(inner.Width, ts.Width + Ui.S(22));
            if (w <= 0) return;
            var pr = new Rectangle(inner.X, inner.Y + (inner.Height - h) / 2, w, h);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(pr, h / 2f))
            using (var br = new SolidBrush(c[0]))
                g.FillPath(br, path);
            g.SmoothingMode = old;
            TextRenderer.DrawText(g, text, Theme.SmallBold, pr, c[1],
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        void DrawBar(Graphics g, Rectangle inner, string text, float value)
        {
            int tw = Ui.S(46);
            TextRenderer.DrawText(g, text, Theme.Regular, new Rectangle(inner.X, inner.Y, tw, inner.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            int bx = inner.X + tw;
            int bw = inner.Right - bx - Ui.S(4);
            if (bw < Ui.S(12)) return;
            int bh = Ui.S(6);
            var track = new Rectangle(bx, inner.Y + (inner.Height - bh) / 2, bw, bh);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(track, bh / 2f))
            using (var br = new SolidBrush(Theme.Border))
                g.FillPath(br, path);
            int fw = (int)Math.Round(bw * Math.Max(0f, Math.Min(100f, value)) / 100f);
            if (fw > 0)
            {
                var fill = new Rectangle(track.X, track.Y, Math.Max(fw, bh), bh);
                using (var path = Theme.Round(fill, bh / 2f))
                using (var br = new SolidBrush(value >= 90 ? Theme.Orange : Theme.Accent))
                    g.FillPath(br, path);
            }
            g.SmoothingMode = old;
        }

        static bool LeadingNumber(string s, out float v)
        {
            v = 0;
            int i = 0;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            return i > 0 && float.TryParse(s.Substring(0, i), NumberStyles.Float, Txt.Inv, out v);
        }
    }
}
