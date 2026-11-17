// Theme: applies the palette to windows that are already open (dark title bar, live light / dark switch).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GpuToggle
{
    static partial class Theme
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Dark title bar (Windows 10 20H1+ / 11). Lists keep the default theme: the Explorer list theme
        // draws column divider lines through the owner-drawn rows.
        public static void Native(Control c)
        {
            if (!c.IsHandleCreated) return;
            try
            {
                if (c is Form)
                {
                    int on = Dark ? 1 : 0;
                    if (DwmSetWindowAttribute(c.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(c.Handle, 19, ref on, 4);
                    // Windows 11: in dark mode paint the caption like the header (overrides "accent color on
                    // title bars"); in light mode give it back to Windows (0xFFFFFFFF = DWMWA_COLOR_DEFAULT).
                    int cap = Dark ? ColorTranslator.ToWin32(Header) : -1, txt = Dark ? ColorTranslator.ToWin32(Color.White) : -1;
                    DwmSetWindowAttribute(c.Handle, 35, ref cap, 4);
                    DwmSetWindowAttribute(c.Handle, 36, ref txt, 4);
                }
            }
            catch { }
        }

        // Call once in every window's constructor: themes the title bar and the native controls.
        public static void Hook(Form f)
        {
            f.HandleCreated += delegate { Native(f); };
            f.Load += delegate { Walk(f, null); };
        }

        // Recolors a window. All old colors are read first and only then replaced: controls and rows
        // without their own color report their parent's, which would already be the new one (and a new
        // color can equal an unrelated old one, e.g. dark Text == light Border).
        static void Walk(Control root, Dictionary<Color, Color> map)
        {
            var colors = new List<KeyValuePair<object, Color[]>>();
            if (map != null) Collect(root, colors);
            Color nc;
            foreach (var kv in colors)
            {
                var c = kv.Key as Control;
                var si = kv.Key as ListViewItem.ListViewSubItem;
                if (map.TryGetValue(kv.Value[0], out nc)) { if (c != null) c.BackColor = nc; else si.BackColor = nc; }
                if (map.TryGetValue(kv.Value[1], out nc)) { if (c != null) c.ForeColor = nc; else si.ForeColor = nc; }
            }
            Style(root, map);
        }

        static void Collect(Control c, List<KeyValuePair<object, Color[]>> colors)
        {
            if (!(c is Button)) colors.Add(new KeyValuePair<object, Color[]>(c, new Color[] { c.BackColor, c.ForeColor }));
            var lv = c as ListView;
            if (lv != null)
                foreach (ListViewItem it in lv.Items)
                    foreach (ListViewItem.ListViewSubItem si in it.SubItems)
                        colors.Add(new KeyValuePair<object, Color[]>(si, new Color[] { si.BackColor, si.ForeColor }));
            foreach (Control ch in c.Controls) Collect(ch, colors);
        }

        static void Style(Control c, Dictionary<Color, Color> map)
        {
            var b = c as Button;
            if (b != null && styles.ContainsKey(b)) StyleButton(b, map);
            var cb = c as ComboBox;
            if (cb != null) { cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Card; cb.ForeColor = Text; }
            var nu = c as NumericUpDown;
            if (nu != null) { nu.BackColor = Card; nu.ForeColor = Text; nu.BorderStyle = BorderStyle.FixedSingle; }
            var tb = c as TextBox;
            if (tb != null) { tb.BackColor = Card; tb.ForeColor = Text; }
            if (c is ListView) { c.BackColor = Card; c.ForeColor = Text; }
            Native(c);
            foreach (Control ch in c.Controls) Style(ch, map);
            c.Invalidate();
        }

        // Switches the palette and recolors every open window in place (no restart, recordings keep running).
        public static void Retheme(bool dark)
        {
            if (dark == Dark) return;
            Color[] old = Palette();
            SetDark(dark);
            Color[] now = Palette();
            var map = new Dictionary<Color, Color>();
            for (int i = 0; i < old.Length; i++) if (!map.ContainsKey(old[i])) map[old[i]] = now[i];
            if (Changed != null) Changed();
            foreach (Form f in Application.OpenForms) { Walk(f, map); Native(f); f.Refresh(); }
        }
    }
}
