// Collects GPU stats every 2 s on a worker thread and hands the result to the UI thread.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace GpuToggle
{
    class StatsPoller
    {
        readonly Control owner;
        readonly Func<List<GpuInfo>> gpus;
        readonly Func<List<string>> appPaths;
        readonly System.Windows.Forms.Timer timer;
        bool busy;

        public event Action<StatsResult> Updated;   // raised on the UI thread

        public StatsPoller(Control owner, Func<List<GpuInfo>> gpus, Func<List<string>> appPaths)
        {
            this.owner = owner;
            this.gpus = gpus;
            this.appPaths = appPaths;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { Poll(); };
        }

        public void Start() { Poll(); timer.Start(); }
        public void Stop() { timer.Stop(); }

        public void Poll()
        {
            if (busy) return;
            busy = true;
            var snapshot = new List<GpuInfo>(gpus());
            var paths = appPaths();
            ThreadPool.QueueUserWorkItem(delegate
            {
                StatsResult r;
                try { r = Stats.Collect(snapshot, paths); } catch { r = new StatsResult(); }
                try
                {
                    owner.BeginInvoke((MethodInvoker)delegate
                    {
                        busy = false;
                        if (!owner.IsDisposed && Updated != null) Updated(r);
                    });
                }
                catch { busy = false; }
            });
        }
    }
}
