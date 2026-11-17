// FPS from the present events Windows already logs (ETW). Nothing is injected into the game.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace GpuToggle
{
    class FrameStats
    {
        public double Fps, FrameMs;
        public double Low1;      // "1 % low" FPS over the last 10 s, 0 until there are enough frames
        public string Api;
        public float[] Graph;    // FPS over a 0.5 s window, every 0.25 s for the last ~9.5 s (oldest first, -1 = no data)
    }

    static class FrameCounter
    {
        const string SessionName = "GPUToggle-OSD";
        const long Sec = 10000000;          // event timestamps are FILETIME (100 ns)
        const long History = 10 * Sec;

        static Guid DxgiGuid = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");      // Microsoft-Windows-DXGI (D3D10/11/12)
        static Guid D3d9Guid = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");      // Microsoft-Windows-D3D9
        static Guid KrnlGuid = new Guid("802EC45A-1E99-4B83-9920-87C98277BA9D");      // Microsoft-Windows-DxgKrnl (any API, e.g. Vulkan / OpenGL)

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int StartTraceW(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int ControlTraceW(ulong handle, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll")] static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint code, byte level, ulong anyKeyword, ulong allKeyword, uint timeout, IntPtr parameters);
        [DllImport("advapi32.dll")] static extern ulong OpenTraceW(IntPtr logfile);
        [DllImport("advapi32.dll")] static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] static extern int CloseTrace(ulong handle);

        delegate void RecordCallback(IntPtr record);

        class Frames
        {
            public List<long> Api = new List<long>();                  // DXGI / D3D9 Present_Start
            public List<long>[] Krnl = { new List<long>(), new List<long>(), new List<long>(), new List<long>() };   // DxgKrnl event groups
            public string ApiName = "DirectX";
            public long Last;
            public int Adds;
        }

        static readonly object Lock = new object();
        static Dictionary<uint, Frames> byPid = new Dictionary<uint, Frames>();
        static ulong session, trace;
        static Thread thread;
        static RecordCallback callback;     // kept alive while ETW calls it
        static IntPtr logfile, loggerName;
        static volatile bool running;
        public static string Error;
        public static bool Running { get { return running; } }

        const int PropsSize = 120 + 1024;   // EVENT_TRACE_PROPERTIES + room for the session name

        static IntPtr NewProps()
        {
            IntPtr p = Marshal.AllocHGlobal(PropsSize);
            for (int i = 0; i < PropsSize; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, PropsSize);        // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);               // Wnode.ClientContext = QPC timestamps
            Marshal.WriteInt32(p, 44, 0x00020000);      // WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(p, 48, 64);              // BufferSize (KB)
            Marshal.WriteInt32(p, 52, 8);               // MinimumBuffers
            Marshal.WriteInt32(p, 56, 64);              // MaximumBuffers
            Marshal.WriteInt32(p, 64, 0x100 | 0x10);    // EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_USE_MS_FLUSH_TIMER
            Marshal.WriteInt32(p, 68, 200);             // FlushTimer = 200 ms, so frames arrive with little delay
            Marshal.WriteInt32(p, 116, 120);            // LoggerNameOffset
            return p;
        }

        public static bool Start()
        {
            if (running) return true;
            Error = null;
            IntPtr props = NewProps();
            try
            {
                int r = StartTraceW(out session, SessionName, props);
                if (r == 183)   // ERROR_ALREADY_EXISTS: left over from a previous run that did not exit cleanly
                {
                    Marshal.FreeHGlobal(props); props = NewProps();
                    ControlTraceW(0, SessionName, props, 1);
                    Marshal.FreeHGlobal(props); props = NewProps();
                    r = StartTraceW(out session, SessionName, props);
                }
                if (r != 0)
                {
                    Error = r == 5 ? "FPS needs administrator rights." : "Could not start the ETW session for FPS (error " + r + ").";
                    return false;
                }
            }
            finally { Marshal.FreeHGlobal(props); }

            EnableTraceEx2(session, ref DxgiGuid, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
            EnableTraceEx2(session, ref D3d9Guid, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
            EnableTraceEx2(session, ref KrnlGuid, 1, 4, 0x8000001, 0, 0, IntPtr.Zero);   // Base | Present keywords

            // EVENT_TRACE_LOGFILEW (448 bytes on x64)
            logfile = Marshal.AllocHGlobal(448);
            for (int i = 0; i < 448; i++) Marshal.WriteByte(logfile, i, 0);
            loggerName = Marshal.StringToHGlobalUni(SessionName);
            Marshal.WriteIntPtr(logfile, 8, loggerName);                    // LoggerName
            Marshal.WriteInt32(logfile, 28, 0x100 | 0x10000000);            // PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD
            callback = OnEvent;
            Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(callback));   // EventRecordCallback
            trace = OpenTraceW(logfile);
            if (trace == ulong.MaxValue)
            {
                Error = "Could not open the ETW session for FPS.";
                StopSession();
                FreeLogfile();
                return false;
            }
            running = true;
            thread = new Thread(delegate ()
            {
                try { ProcessTrace(new ulong[] { trace }, 1, IntPtr.Zero, IntPtr.Zero); } catch { }
                running = false;
            });
            thread.IsBackground = true;
            thread.Name = "OSD frame counter";
            thread.Start();
            return true;
        }

        public static void Stop()
        {
            if (thread == null) return;
            StopSession();
            try { CloseTrace(trace); } catch { }
            thread.Join(3000);
            thread = null;
            running = false;
            FreeLogfile();
            lock (Lock) byPid.Clear();
        }

        static void StopSession()
        {
            IntPtr props = NewProps();
            try { ControlTraceW(session, null, props, 1); } catch { }   // EVENT_TRACE_CONTROL_STOP
            finally { Marshal.FreeHGlobal(props); }
        }

        static void FreeLogfile()
        {
            if (logfile != IntPtr.Zero) { Marshal.FreeHGlobal(logfile); logfile = IntPtr.Zero; }
            if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
        }

        // EVENT_RECORD: EventHeader.ProcessId @12, TimeStamp @16, ProviderId @24, EventDescriptor.Id @40
        static void OnEvent(IntPtr rec)
        {
            try
            {
                uint provider = (uint)Marshal.ReadInt32(rec, 24);   // Data1 of the provider GUID
                int id = (ushort)Marshal.ReadInt16(rec, 40);
                int kind;
                string api = null;
                if (provider == 0xCA11C036u) { if (id != 42 && id != 55) return; kind = 0; api = "DXGI"; }        // Present_Start, PresentMultiplaneOverlay_Start
                else if (provider == 0x783ACA0Au) { if (id != 1) return; kind = 0; api = "D3D9"; }                // Present_Start
                else if (provider == 0x802EC45Au)
                {
                    if (id == 166 || id == 168 || id == 252) kind = 1;      // Blit / Flip / FlipMultiPlaneOverlay
                    else if (id == 171) kind = 2;                            // PresentHistory_Start
                    else if (id == 215) kind = 3;                            // PresentHistoryDetailed_Start
                    else if (id == 184) kind = 4;                            // Present_Info
                    else return;
                }
                else return;
                uint pid = (uint)Marshal.ReadInt32(rec, 12);
                long ts = Marshal.ReadInt64(rec, 16);
                lock (Lock)
                {
                    Frames f;
                    if (!byPid.TryGetValue(pid, out f)) { f = new Frames(); byPid[pid] = f; }
                    if (api != null) f.ApiName = api;
                    Insert(kind == 0 ? f.Api : f.Krnl[kind - 1], ts);
                    if (ts > f.Last) f.Last = ts;
                    if (++f.Adds >= 512) { f.Adds = 0; Trim(f, f.Last - History); }
                }
            }
            catch { }
        }

        static void Insert(List<long> l, long ts)
        {
            int n = l.Count;
            if (n == 0 || ts >= l[n - 1]) { l.Add(ts); return; }
            int k = l.BinarySearch(ts);     // buffers from different CPUs can arrive slightly out of order
            l.Insert(k < 0 ? ~k : k, ts);
        }

        static void Trim(Frames f, long from)
        {
            Cut(f.Api, from);
            foreach (var k in f.Krnl) Cut(k, from);
        }

        static void Cut(List<long> l, long from)
        {
            int k = 0;
            while (k < l.Count && l[k] < from) k++;
            if (k > 0) l.RemoveRange(0, k);
        }

        static int Recent(List<long> l, long now)
        {
            int c = 0;
            for (int i = l.Count - 1; i >= 0 && now - l[i] <= 2 * Sec; i--) c++;
            return c;
        }

        // Frame rate of a process right now, or null if it is not presenting frames.
        public static FrameStats Get(uint pid)
        {
            long now = DateTime.UtcNow.ToFileTimeUtc();
            lock (Lock)
            {
                List<uint> dead = null;
                foreach (var kv in byPid)
                    if (now - kv.Value.Last > 15 * Sec) { if (dead == null) dead = new List<uint>(); dead.Add(kv.Key); }
                if (dead != null) foreach (var d in dead) byPid.Remove(d);

                Frames f;
                if (pid == 0 || !byPid.TryGetValue(pid, out f)) return null;
                Trim(f, now - History);
                List<long> l = f.Api;
                string api = f.ApiName;
                if (Recent(l, now) < 2)
                {
                    // No DirectX presents (Vulkan, OpenGL...): use the kernel group that saw the most presents.
                    l = null;
                    int best = 1;
                    foreach (var k in f.Krnl) { int c = Recent(k, now); if (c > best) { best = c; l = k; } }
                    api = "Vulkan / OpenGL";
                    if (l == null) return null;
                }
                return Compute(l, api);
            }
        }

        static FrameStats Compute(List<long> l, string api)
        {
            int n = l.Count;
            long last = l[n - 1];
            int i = n - 1;
            while (i > 0 && last - l[i - 1] <= Sec) i--;
            if (n - 1 - i < 1 || last == l[i]) return null;
            var s = new FrameStats();
            s.Api = api;
            s.Fps = (n - 1 - i) * (double)Sec / (last - l[i]);
            s.FrameMs = 1000.0 / s.Fps;
            if (n >= 30)
            {
                var ft = new long[n - 1];
                for (int k = 0; k < n - 1; k++) ft[k] = l[k + 1] - l[k];
                Array.Sort(ft);
                long p99 = ft[(int)Math.Floor((ft.Length - 1) * 0.99)];
                if (p99 > 0) s.Low1 = Math.Min(s.Fps, Sec / (double)p99);
            }
            s.Graph = BuildGraph(l, last);
            return s;
        }

        public const int GraphPoints = 38;

        static float[] BuildGraph(List<long> l, long end)
        {
            const long step = Sec / 4, win = Sec / 2;
            var g = new float[GraphPoints];
            int lo = 0, hi = 0;
            for (int k = 0; k < GraphPoints; k++)
            {
                long e = end - (GraphPoints - 1 - k) * step, b = e - win;
                if (b < l[0] - step) { g[k] = -1; continue; }
                while (lo < l.Count && l[lo] <= b) lo++;
                while (hi < l.Count && l[hi] <= e) hi++;
                g[k] = (hi - lo) * (float)Sec / win;
            }
            return g;
        }
    }
}
