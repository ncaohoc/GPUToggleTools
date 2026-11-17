// The OSD window: layered, click-through, topmost, never takes focus.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GpuToggle
{
    // Layered, click-through, topmost tool window drawn with per-pixel alpha (UpdateLayeredWindow).
    // It never takes focus. Shows over windowed and borderless games; exclusive fullscreen can hide it.
    class OsdOverlay : Form
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, int key, ref BLEND blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        Font font;
        int fontSize = -1;
        float cw, lh;

        public OsdOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "GPU Toggle OSD";
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_LAYERED | WS_EX_TRANSPARENT (click-through) | WS_EX_TOOLWINDOW (no Alt+Tab) | WS_EX_NOACTIVATE | WS_EX_TOPMOST
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; }   // WM_NCHITTEST -> HTTRANSPARENT
            base.WndProc(ref m);
        }

        void EnsureFont(int size)
        {
            if (font != null && size == fontSize) return;
            if (font != null) font.Dispose();
            fontSize = size;
            float px = (size == 0 ? 13f : (size == 2 ? 21f : 16f)) * Ui.Scale;
            font = new Font("Consolas", px, FontStyle.Bold, GraphicsUnit.Pixel);
            using (var b = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(b))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                cw = g.MeasureString("MMMMMMMMMM", font, PointF.Empty, StringFormat.GenericTypographic).Width / 10f;
            }
            lh = font.GetHeight() * 1.12f;
        }

        public void Render(List<List<OsdSeg>> lines, int corner, int size, int alpha, IntPtr target)
        {
            if (IsDisposed || !IsHandleCreated || lines.Count == 0) return;
            EnsureFont(size);
            int maxChars = 0;
            foreach (var line in lines)
            {
                int n = 0;
                foreach (var seg in line) n += seg.Text.Length;
                maxChars = Math.Max(maxChars, n);
            }
            int pad = (int)Math.Round(lh * 0.45f);
            int w = (int)Math.Ceiling(maxChars * cw) + pad * 2, h = (int)Math.Ceiling(lines.Count * lh) + pad * 2 - (int)(lh * 0.12f);

            using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    if (alpha > 0)
                        using (var path = Theme.Round(new Rectangle(0, 0, w - 1, h - 1), pad * 0.8f))
                        using (var br = new SolidBrush(Color.FromArgb(alpha * 255 / 100, 8, 12, 20)))
                            g.FillPath(br, path);
                    var sf = StringFormat.GenericTypographic;
                    using (var shadow = new SolidBrush(Color.FromArgb(220, 0, 0, 0)))
                    {
                        float y = pad;
                        foreach (var line in lines)
                        {
                            float x = pad;
                            foreach (var seg in line)
                            {
                                if (seg.Graph != null) DrawGraph(g, seg, x, y);
                                else if (seg.Text.Trim().Length > 0)
                                {
                                    g.DrawString(seg.Text, font, shadow, x + 1, y + 1, sf);
                                    using (var br = new SolidBrush(seg.Color)) g.DrawString(seg.Text, font, br, x, y, sf);
                                }
                                x += seg.Text.Length * cw;
                            }
                            y += lh;
                        }
                    }
                }

                Rectangle scr;
                try { scr = (target != IntPtr.Zero ? Screen.FromHandle(target) : Screen.PrimaryScreen).Bounds; }
                catch { scr = Screen.PrimaryScreen.Bounds; }
                int m = Ui.S(10);
                var dst = new POINT();
                dst.X = corner == 1 || corner == 3 ? scr.Right - w - m : scr.Left + m;
                dst.Y = corner >= 2 ? scr.Bottom - h - m : scr.Top + m;

                IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc), hBmp = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                    old = SelectObject(memDc, hBmp);
                    var sz = new SIZE();
                    sz.W = w; sz.H = h;
                    var src = new POINT();
                    var blend = new BLEND();
                    blend.Alpha = 255;
                    blend.Format = 1;   // AC_SRC_ALPHA
                    UpdateLayeredWindow(Handle, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, 2);   // ULW_ALPHA
                }
                finally
                {
                    if (old != IntPtr.Zero) SelectObject(memDc, old);
                    if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                    DeleteDC(memDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
            // Stay above the game even if it (or another app) made itself topmost later.
            SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);   // HWND_TOPMOST, NOSIZE | NOMOVE | NOACTIVATE
        }

        // Sparkline inside one text row: area + line, scaled 0..max, with the last ~10 s of FPS.
        void DrawGraph(Graphics g, OsdSeg seg, float x, float y)
        {
            float gx = x + cw * 0.5f, gw = seg.Text.Length * cw - cw, gy = y + lh * 0.14f, gh = lh * 0.68f;
            float max = Math.Max(1e-6f, seg.Floor);
            foreach (float f in seg.Graph) max = Math.Max(max, f);
            if (seg.Graph2 != null) foreach (float f in seg.Graph2) max = Math.Max(max, f);
            max *= 1.1f;
            if (seg.Fixed > 0) max = seg.Fixed;
            using (var bg = new SolidBrush(Color.FromArgb(50, 255, 255, 255)))
                g.FillRectangle(bg, gx, gy, gw, gh);
            if (seg.Graph2 != null) DrawSeries(g, seg.Graph2, seg.Color2, max, gx, gy, gw, gh);
            DrawSeries(g, seg.Graph, seg.Color, max, gx, gy, gw, gh);
        }

        void DrawSeries(Graphics g, float[] v, Color color, float max, float gx, float gy, float gw, float gh)
        {
            var pts = new List<PointF>();
            for (int k = 0; k < v.Length; k++)
            {
                if (v[k] < 0) continue;
                pts.Add(new PointF(gx + gw * k / (v.Length - 1), gy + gh - gh * Math.Min(1f, v[k] / max)));
            }
            if (pts.Count < 2) return;
            var area = new List<PointF>(pts);
            area.Add(new PointF(pts[pts.Count - 1].X, gy + gh));
            area.Add(new PointF(pts[0].X, gy + gh));
            using (var fill = new SolidBrush(Color.FromArgb(60, color)))
                g.FillPolygon(fill, area.ToArray());
            using (var pen = new Pen(color, Math.Max(1f, lh / 14f)))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, pts.ToArray());
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && font != null) font.Dispose();
            base.Dispose(disposing);
        }
    }
}
