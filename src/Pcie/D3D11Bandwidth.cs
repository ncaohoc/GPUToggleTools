// PCIe bandwidth test: times D3D11 copies between system memory and VRAM with GPU timestamps.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace GpuToggle
{
    // Raw vtable calls, so we only need the handful of D3D11 methods used here.
    static class D3D11Bandwidth
    {
        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels,
            uint sdkVersion, out IntPtr device, out int level, out IntPtr context);

        [StructLayout(LayoutKind.Sequential)]
        struct BUFFER_DESC { public uint ByteWidth, Usage, BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride; }
        [StructLayout(LayoutKind.Sequential)]
        struct QUERY_DESC { public uint Query, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)]
        struct MAPPED { public IntPtr pData; public uint RowPitch, DepthPitch; }
        [StructLayout(LayoutKind.Sequential)]
        struct DISJOINT { public ulong Frequency; public int Disjoint; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBufferFn(IntPtr self, ref BUFFER_DESC d, IntPtr init, out IntPtr buf);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateQueryFn(IntPtr self, ref QUERY_DESC d, out IntPtr q);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr res, uint sub, int type, uint flags, out MAPPED m);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr res, uint sub);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void AsyncFn(IntPtr self, IntPtr async);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDataFn(IntPtr self, IntPtr async, IntPtr data, uint size, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyFn(IntPtr self, IntPtr dst, IntPtr src);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FlushFn(IntPtr self);

        // ID3D11Device / ID3D11DeviceContext vtable slots
        const int DEV_CreateBuffer = 3, DEV_CreateQuery = 24;
        const int CTX_Map = 14, CTX_Unmap = 15, CTX_Begin = 27, CTX_End = 28, CTX_GetData = 29, CTX_CopyResource = 47, CTX_Flush = 111;

        const uint BufferBytes = 64u << 20;     // 64 MB per copy

        static T Vt<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size), typeof(T)) as T;
        }

        class Ctx
        {
            public IntPtr Dev, Ctx_;
            public CreateBufferFn CreateBuffer;
            public CreateQueryFn CreateQuery;
            public MapFn Map;
            public UnmapFn Unmap;
            public AsyncFn Begin, End;
            public GetDataFn GetData;
            public CopyFn Copy;
            public FlushFn Flush;
            public List<IntPtr> Owned = new List<IntPtr>();

            public IntPtr Buffer(uint usage, uint bind, uint cpu)
            {
                var d = new BUFFER_DESC();
                d.ByteWidth = BufferBytes; d.Usage = usage; d.BindFlags = bind; d.CPUAccessFlags = cpu;
                IntPtr b;
                int hr = CreateBuffer(Dev, ref d, IntPtr.Zero, out b);
                if (hr != 0 || b == IntPtr.Zero) throw new Exception("CreateBuffer failed (0x" + hr.ToString("X8") + ")");
                Owned.Add(b);
                return b;
            }

            public IntPtr Query(uint type)
            {
                var d = new QUERY_DESC();
                d.Query = type;
                IntPtr q;
                int hr = CreateQuery(Dev, ref d, out q);
                if (hr != 0 || q == IntPtr.Zero) throw new Exception("CreateQuery failed (0x" + hr.ToString("X8") + ")");
                Owned.Add(q);
                return q;
            }

            // Spins until the query result is ready (S_OK). Returns false on timeout.
            public bool Wait(IntPtr q, IntPtr data, uint size)
            {
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    int hr = GetData(Ctx_, q, data, size, 0);
                    if (hr == 0) return true;
                    if (hr < 0 || sw.ElapsedMilliseconds > 60000) return false;
                    Thread.Sleep(0);
                }
            }

            public void Idle(IntPtr evt)
            {
                End(Ctx_, evt);
                Flush(Ctx_);
                if (!Wait(evt, IntPtr.Zero, 0)) throw new Exception("GPU did not respond");
            }
        }

        // Times reps copies (alternating between two destinations) with GPU timestamps. Returns GB/s.
        static double Run(Ctx c, IntPtr[] dst, IntPtr[] src, int reps, IntPtr qDisjoint, IntPtr qStart, IntPtr qEnd, IntPtr evt)
        {
            c.Idle(evt);
            var sw = Stopwatch.StartNew();
            c.Begin(c.Ctx_, qDisjoint);
            c.End(c.Ctx_, qStart);
            for (int i = 0; i < reps; i++) c.Copy(c.Ctx_, dst[i % dst.Length], src[i % src.Length]);
            c.End(c.Ctx_, qEnd);
            c.End(c.Ctx_, qDisjoint);
            c.Flush(c.Ctx_);

            IntPtr buf = Marshal.AllocHGlobal(32);
            try
            {
                if (!c.Wait(qDisjoint, buf, (uint)Marshal.SizeOf(typeof(DISJOINT)))) throw new Exception("GPU did not respond");
                double wall = sw.Elapsed.TotalSeconds;
                var dj = (DISJOINT)Marshal.PtrToStructure(buf, typeof(DISJOINT));
                double seconds = wall;
                if (dj.Disjoint == 0 && dj.Frequency > 0 && c.Wait(qStart, buf, 8))
                {
                    ulong t0 = (ulong)Marshal.ReadInt64(buf);
                    if (c.Wait(qEnd, buf, 8))
                    {
                        ulong t1 = (ulong)Marshal.ReadInt64(buf);
                        if (t1 > t0) seconds = (t1 - t0) / (double)dj.Frequency;
                    }
                }
                return (double)BufferBytes * reps / seconds / 1e9;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // Calibrates the number of copies so one run takes about half a second, then keeps the best of 3 runs.
        static double Measure(Ctx c, IntPtr[] dst, IntPtr[] src, IntPtr qd, IntPtr qs, IntPtr qe, IntPtr evt)
        {
            double rate = Run(c, dst, src, 2, qd, qs, qe, evt);
            int reps = (int)Math.Ceiling(0.5 * rate * 1e9 / BufferBytes);
            reps = Math.Max(4, Math.Min(256, reps));
            double best = 0;
            for (int i = 0; i < 3; i++) best = Math.Max(best, Run(c, dst, src, reps, qd, qs, qe, evt));
            return best;
        }

        public static BandwidthResult Test(GpuInfo g, Action<string> progress)
        {
            var r = new BandwidthResult();
            try { r.Chain = Pcie.Chain(g.InstanceId); } catch { }
            r.Integrated = r.Chain.Count == 0 || r.Chain[0] == null;
            try { r.Tunnel = Pcie.Tunnel(g.InstanceId); } catch { }

            IntPtr adapter = Dxgi.FindAdapter(g.VenId, g.DevId);
            if (adapter == IntPtr.Zero) { r.Error = "This GPU is not available to DirectX (is it enabled and is the driver installed?)."; return r; }

            var c = new Ctx();
            try
            {
                int level;
                int hr = D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out c.Dev, out level, out c.Ctx_);
                if (hr != 0 || c.Dev == IntPtr.Zero) { r.Error = "Could not create a Direct3D 11 device (0x" + hr.ToString("X8") + ")."; return r; }

                c.CreateBuffer = Vt<CreateBufferFn>(c.Dev, DEV_CreateBuffer);
                c.CreateQuery = Vt<CreateQueryFn>(c.Dev, DEV_CreateQuery);
                c.Map = Vt<MapFn>(c.Ctx_, CTX_Map);
                c.Unmap = Vt<UnmapFn>(c.Ctx_, CTX_Unmap);
                c.Begin = Vt<AsyncFn>(c.Ctx_, CTX_Begin);
                c.End = Vt<AsyncFn>(c.Ctx_, CTX_End);
                c.GetData = Vt<GetDataFn>(c.Ctx_, CTX_GetData);
                c.Copy = Vt<CopyFn>(c.Ctx_, CTX_CopyResource);
                c.Flush = Vt<FlushFn>(c.Ctx_, CTX_Flush);

                progress("Allocating buffers...");
                const uint DEFAULT = 0, STAGING = 3, BIND_VB = 0x1, CPU_WRITE = 0x10000, CPU_READ = 0x20000;
                IntPtr up = c.Buffer(STAGING, 0, CPU_WRITE);                 // system memory, CPU writes
                IntPtr[] vram = { c.Buffer(DEFAULT, BIND_VB, 0), c.Buffer(DEFAULT, BIND_VB, 0) };
                IntPtr[] down = { c.Buffer(STAGING, 0, CPU_READ), c.Buffer(STAGING, 0, CPU_READ) };
                IntPtr qd = c.Query(3), qs = c.Query(2), qe = c.Query(2), evt = c.Query(0);   // disjoint, timestamp x2, event

                // Fill the upload buffer with non-trivial data.
                MAPPED m;
                hr = c.Map(c.Ctx_, up, 0, 2, 0, out m);   // D3D11_MAP_WRITE
                if (hr != 0) throw new Exception("Map failed (0x" + hr.ToString("X8") + ")");
                var chunk = new byte[1 << 20];
                new Random(1).NextBytes(chunk);
                for (long off = 0; off < BufferBytes; off += chunk.Length) Marshal.Copy(chunk, 0, new IntPtr(m.pData.ToInt64() + off), chunk.Length);
                c.Unmap(c.Ctx_, up, 0);

                // While traffic flows: re-read the link (GPUs lower its speed when idle) and, on NVIDIA,
                // record the NVML counter peaks.
                int phase = 1;   // 1 = upload, 2 = download, 0 = done
                IntPtr nv = IntPtr.Zero;
                try { nv = Nvml.Find(g.VenId, g.DevId); } catch { }
                var sampler = new Thread(delegate ()
                {
                    Thread.Sleep(150);
                    try { r.ChainLoad = Pcie.Chain(g.InstanceId); } catch { }
                    while (nv != IntPtr.Zero && phase != 0)
                    {
                        double rx, tx;
                        try
                        {
                            if (!Nvml.Throughput(nv, out rx, out tx)) break;
                            if (phase == 1) r.NvmlRxPeak = Math.Max(r.NvmlRxPeak, rx);
                            else r.NvmlTxPeak = Math.Max(r.NvmlTxPeak, tx);
                        }
                        catch { break; }
                        Thread.Sleep(30);
                    }
                });
                sampler.IsBackground = true;
                sampler.Start();
                try
                {
                    progress("Measuring host -> GPU...");
                    r.UploadGBs = Measure(c, vram, new IntPtr[] { up }, qd, qs, qe, evt);
                    phase = 2;
                    progress("Measuring GPU -> host...");
                    r.DownloadGBs = Measure(c, down, new IntPtr[] { vram[0] }, qd, qs, qe, evt);
                }
                finally { phase = 0; sampler.Join(2000); }
            }
            catch (Exception ex) { r.Error = ex.Message; }
            finally
            {
                foreach (var p in c.Owned) Marshal.Release(p);
                if (c.Ctx_ != IntPtr.Zero) Marshal.Release(c.Ctx_);
                if (c.Dev != IntPtr.Zero) Marshal.Release(c.Dev);
                Marshal.Release(adapter);
            }
            return r;
        }
    }
}
