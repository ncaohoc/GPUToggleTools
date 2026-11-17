// System-wide hotkey (works while a game has the focus) on a hidden window of its own.

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GpuToggle
{
    class GlobalHotkey : NativeWindow, IDisposable
    {
        const int WM_HOTKEY = 0x0312;
        public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, NoRepeat = 0x4000;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        readonly int id;
        public bool Registered { get; private set; }   // false when another app already owns the key
        public event Action Pressed;

        public GlobalHotkey(int id, uint modifiers, Keys key)
        {
            this.id = id;
            CreateHandle(new CreateParams());
            Registered = RegisterHotKey(Handle, id, modifiers, (uint)key);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == id && Pressed != null) Pressed();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            if (Registered) UnregisterHotKey(Handle, id);
            Registered = false;
            DestroyHandle();
        }
    }
}
