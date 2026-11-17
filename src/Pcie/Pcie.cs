// Reads PCIe link info from the device tree (cfgmgr32) and spots USB4 / Thunderbolt eGPU tunnels.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace GpuToggle
{
    static class Pcie
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DEVPROPKEY { public Guid fmtid; public uint pid; }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[] buffer, ref uint size, uint flags);

        static readonly Guid PciKeys = new Guid("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
        static readonly Guid DevKeys = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");   // DEVPKEY_Device_*

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int len, uint flags);

        static byte[] Prop(uint dev, Guid fmt, uint pid)
        {
            var k = new DEVPROPKEY();
            k.fmtid = fmt;
            k.pid = pid;
            uint type, size = 0;
            CM_Get_DevNode_PropertyW(dev, ref k, out type, null, ref size, 0);
            if (size == 0) return null;
            var buf = new byte[size];
            if (CM_Get_DevNode_PropertyW(dev, ref k, out type, buf, ref size, 0) != 0) return null;
            return buf;
        }

        static int U32(uint dev, uint pid)
        {
            byte[] b = Prop(dev, PciKeys, pid);
            return b != null && b.Length >= 4 ? BitConverter.ToInt32(b, 0) : 0;
        }

        static string Name(uint dev)
        {
            byte[] b = Prop(dev, DevKeys, 14);                 // FriendlyName
            if (b == null) b = Prop(dev, DevKeys, 2);          // DeviceDesc
            return b == null ? "PCI device" : Encoding.Unicode.GetString(b).TrimEnd((char)0);
        }

        static PcieLink Read(uint dev)
        {
            var l = new PcieLink();
            l.CurSpeed = U32(dev, 9);
            l.CurWidth = U32(dev, 10);
            l.MaxSpeed = U32(dev, 11);
            l.MaxWidth = U32(dev, 12);
            if (l.CurWidth <= 0 && l.MaxWidth <= 0) return null;
            l.Device = Name(dev);
            return l;
        }

        // Links from the GPU up to the root port. [0] is the GPU's own link (null if it has none, e.g. an iGPU).
        public static List<PcieLink> Chain(string instanceId)
        {
            var chain = new List<PcieLink>();
            uint dev;
            if (CM_Locate_DevNodeW(out dev, instanceId, 0) != 0) return chain;
            chain.Add(Read(dev));
            for (int i = 0; i < 12; i++)
            {
                uint parent;
                if (CM_Get_Parent(out parent, dev, 0) != 0) break;
                dev = parent;
                var l = Read(dev);
                if (l != null) chain.Add(l);
            }
            return chain;
        }

        // Windows only reports link width/speed on endpoints, not on switch / root ports, so instead we look for
        // a USB4 / Thunderbolt eGPU controller above the GPU. Its PCIe tunnel is usually the real limit.
        static readonly string[] TunnelIds = {
            "VEN_1B21&DEV_2461", "VEN_1B21&DEV_2463", "VEN_1B21&DEV_2464",      // ASMedia ASM2464 (USB4)
            "VEN_8086&DEV_15E7", "VEN_8086&DEV_15EA", "VEN_8086&DEV_15EF",      // Intel Titan Ridge (TB3)
            "VEN_8086&DEV_1133", "VEN_8086&DEV_1136", "VEN_8086&DEV_0B26",      // Intel Maple / Goshen Ridge (TB4)
            "VEN_8086&DEV_1578", "VEN_8086&DEV_1576", "VEN_8086&DEV_15D3", "VEN_8086&DEV_15DA" };   // Alpine Ridge (TB3)

        public static string Tunnel(string instanceId)
        {
            uint dev;
            if (CM_Locate_DevNodeW(out dev, instanceId, 0) != 0) return null;
            for (int i = 0; i < 12; i++)
            {
                uint parent;
                if (CM_Get_Parent(out parent, dev, 0) != 0) break;
                dev = parent;
                var sb = new StringBuilder(512);
                if (CM_Get_Device_IDW(dev, sb, sb.Capacity, 0) != 0) continue;
                string id = sb.ToString().ToUpperInvariant();
                string name = Name(dev);
                foreach (var t in TunnelIds)
                    if (id.Contains(t)) return (t.StartsWith("VEN_1B21", StringComparison.Ordinal) ? "USB4 (ASMedia ASM2464)" : "Thunderbolt (Intel)");
                if (name.IndexOf("Thunderbolt", StringComparison.OrdinalIgnoreCase) >= 0) return "Thunderbolt";
                if (name.IndexOf("USB4", StringComparison.OrdinalIgnoreCase) >= 0) return "USB4";
            }
            return null;
        }
    }
}
