// Base of the pages of the main window. A page owns its controls and logic; the main window only
// switches between pages, shows their status lines and tells them when they are opened or closed.

using System;
using System.Windows.Forms;

namespace GpuToggle
{
    abstract class AppPage : Panel
    {
        protected AppPage()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Surface;
            Visible = false;
        }

        // Status bar text the page wants to show.
        public event Action<string> StatusChanged;

        // Header button text.
        public abstract string Title { get; }
        public virtual int NavWidth { get { return 124; } }

        // Status line shown when the page is opened (null = the page sets it itself).
        public virtual string StatusHint { get { return null; } }

        public virtual void OnActivated() { }
        public virtual void OnDeactivated() { }

        // The main window is closing: stop timers, threads and overlays.
        public virtual void Shutdown() { }

        protected void SetStatus(string s)
        {
            if (StatusChanged != null) StatusChanged(s);
        }

        // Owner for message boxes and dialogs.
        protected IWin32Window OwnerWindow
        {
            get { Form f = FindForm(); return f != null ? (IWin32Window)f : this; }
        }

        protected void SetWaitCursor(bool wait)
        {
            Form f = FindForm();
            if (f != null) f.Cursor = wait ? Cursors.WaitCursor : Cursors.Default;
        }
    }
}
