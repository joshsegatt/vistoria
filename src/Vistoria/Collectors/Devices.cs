using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Vistoria.Collectors
{
    // Windows keeps a record of every device it ever installed, present or not, with first-install,
    // last-arrival and last-removal dates. That is how a swapped CPU, GPU or disk shows up.
    static class Devices
    {
        static readonly string[] Classes = { "Processor", "Display", "DiskDrive" };

        public static void Collect(Evidence ev) => Guard.Try(ev, "devices", () =>
        {
            const uint DIGCF_ALLCLASSES = 0x4; // no DIGCF_PRESENT: we want the ones that are gone too
            var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    var cls = GetString(set, ref data, Class);
                    if (Array.IndexOf(Classes, cls) < 0) continue;
                    var id = GetString(set, ref data, InstanceId) ?? "";
                    // Pendrives, virtual and software devices say nothing about the machine's own parts.
                    if (id.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)
                        || id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("SCSI\\DISK&VEN_MSFT", StringComparison.OrdinalIgnoreCase))
                        continue;
                    ev.Devices.Add(new DeviceRecord
                    {
                        Class = cls,
                        Name = GetString(set, ref data, FriendlyName) ?? GetString(set, ref data, DeviceDesc),
                        InstanceId = id,
                        Present = GetBool(set, ref data, IsPresent) ?? true,
                        FirstInstall = GetDate(set, ref data, FirstInstallDate),
                        LastArrival = GetDate(set, ref data, LastArrivalDate),
                        LastRemoval = GetDate(set, ref data, LastRemovalDate),
                    });
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        });

        static readonly Guid DevGeneral = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
        static readonly Guid DevDates = new Guid("83da6326-97a6-4088-9453-a1923f573b29");
        static readonly DEVPROPKEY DeviceDesc = new DEVPROPKEY(DevGeneral, 2);
        static readonly DEVPROPKEY Class = new DEVPROPKEY(DevGeneral, 9);
        static readonly DEVPROPKEY FriendlyName = new DEVPROPKEY(DevGeneral, 14);
        static readonly DEVPROPKEY InstanceId = new DEVPROPKEY(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
        static readonly DEVPROPKEY IsPresent = new DEVPROPKEY(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 5);
        static readonly DEVPROPKEY FirstInstallDate = new DEVPROPKEY(DevDates, 101);
        static readonly DEVPROPKEY LastArrivalDate = new DEVPROPKEY(DevDates, 102);
        static readonly DEVPROPKEY LastRemovalDate = new DEVPROPKEY(DevDates, 103);

        const uint TypeFiletime = 0x10, TypeBoolean = 0x11, TypeString = 0x12;

        static byte[] Get(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY key, uint expectedType)
        {
            var buf = new byte[1024];
            if (!SetupDiGetDevicePropertyW(set, ref d, ref key, out var type, buf, (uint)buf.Length, out var size, 0))
            {
                if (Marshal.GetLastWin32Error() != 122 /* ERROR_INSUFFICIENT_BUFFER */) return null;
                buf = new byte[size];
                if (!SetupDiGetDevicePropertyW(set, ref d, ref key, out type, buf, size, out size, 0)) return null;
            }
            if (type != expectedType) return null;
            Array.Resize(ref buf, (int)size);
            return buf;
        }

        static string GetString(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY k)
        {
            var b = Get(set, ref d, k, TypeString);
            return b == null ? null : Encoding.Unicode.GetString(b).TrimEnd('\0').Trim(); // CPU names come space-padded
        }

        static bool? GetBool(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY k)
        {
            var b = Get(set, ref d, k, TypeBoolean);
            return b == null || b.Length < 1 ? (bool?)null : b[0] != 0;
        }

        static DateTime? GetDate(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY k)
        {
            var b = Get(set, ref d, k, TypeFiletime);
            if (b == null || b.Length < 8) return null;
            var ft = BitConverter.ToInt64(b, 0);
            return ft > 0 ? DateTime.FromFileTimeUtc(ft) : (DateTime?)null;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct DEVPROPKEY
        {
            public Guid fmtid; public uint pid;
            public DEVPROPKEY(Guid g, uint p) { fmtid = g; pid = p; }
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[] buffer, uint size, out uint required, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
