// Light / dark pill switch of the header: sun on the left, moon on the right.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GpuToggle
{
    class ThemeSwitch : Control
    {
        public bool On;              // true = dark
        float pos;                   // knob position 0 (left) .. 1 (right), animated
        bool hover;
        System.Windows.Forms.Timer anim;
        public event EventHandler Toggled;

        public ThemeSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(Ui.S(66), Ui.S(32));
            Cursor = Cursors.Hand;
            anim = new System.Windows.Forms.Timer();
            anim.Interval = 15;
            anim.Tick += delegate
            {
                float target = On ? 1f : 0f;
                pos += (target - pos) * 0.35f;
                if (Math.Abs(target - pos) < 0.02f) { pos = target; anim.Stop(); }
                Invalidate();
            };
            Disposed += delegate { anim.Dispose(); };
        }

        // Sets the state without animation (initial value / theme changed elsewhere).
        public void SetState(bool dark)
        {
            On = dark;
            pos = dark ? 1f : 0f;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            On = !On;
            anim.Start();
            if (Toggled != null) Toggled(this, EventArgs.Empty);
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Header);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Round(track, track.Height / 2f))
            {
                using (var br = new SolidBrush(hover ? Theme.HeaderSel : Theme.HeaderHover)) g.FillPath(br, path);
                using (var pen = new Pen(Theme.HeaderSel)) g.DrawPath(pen, path);
            }

            int pad = Ui.S(3);
            int d = Height - 1 - pad * 2;                      // knob diameter
            float travel = Width - 1 - pad * 2 - d;
            var knob = new RectangleF(pad + travel * pos, pad, d, d);
            float cyAll = pad + d / 2f;
            var sunC = new PointF(pad + d / 2f, cyAll);
            var moonC = new PointF(Width - 1 - pad - d / 2f, cyAll);

            // Knob: warm for the sun, cool for the moon, blending while it slides.
            Color kc = Blend(Color.FromArgb(253, 224, 71), Color.FromArgb(203, 213, 225), pos);
            using (var br = new SolidBrush(kc)) g.FillEllipse(br, knob);

            // Icons: the one under the knob is dark, the other one muted on the track.
            Color onKnob = Color.FromArgb(30, 41, 59);
            DrawSun(g, sunC, d, pos < 0.5f ? onKnob : Theme.HeaderMuted);
            DrawMoon(g, moonC, d, pos >= 0.5f ? onKnob : Theme.HeaderMuted, pos >= 0.5f ? kc : (hover ? Theme.HeaderSel : Theme.HeaderHover));
        }

        static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        static void DrawSun(Graphics g, PointF c, int d, Color col)
        {
            float r = d * 0.17f, r1 = d * 0.27f, r2 = d * 0.38f;
            using (var br = new SolidBrush(col)) g.FillEllipse(br, c.X - r, c.Y - r, r * 2, r * 2);
            using (var pen = new Pen(col, Math.Max(1.2f, d * 0.065f)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    g.DrawLine(pen, c.X + (float)Math.Cos(a) * r1, c.Y + (float)Math.Sin(a) * r1,
                        c.X + (float)Math.Cos(a) * r2, c.Y + (float)Math.Sin(a) * r2);
                }
            }
        }

        // Crescent: a disc with an offset disc cut out of it.
        static void DrawMoon(Graphics g, PointF c, int d, Color col, Color bg)
        {
            float r = d * 0.30f;
            using (var disc = new GraphicsPath())
            using (var cut = new GraphicsPath())
            {
                disc.AddEllipse(c.X - r, c.Y - r, r * 2, r * 2);
                cut.AddEllipse(c.X - r + r * 0.75f, c.Y - r - r * 0.45f, r * 2, r * 2);
                using (var reg = new Region(disc))
                {
                    reg.Exclude(cut);
                    using (var br = new SolidBrush(col)) g.FillRegion(br, reg);
                }
            }
        }
    }
}
