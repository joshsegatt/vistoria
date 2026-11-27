using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vistoria.Collectors
{
    static class Disks
    {
        public static void Collect(Evidence ev)
        {
            Guard.Try(ev, "disks.inventory", () =>
            {
                foreach (var d in Wmi.Query(Wmi.Storage, "SELECT DeviceId, FriendlyName, SerialNumber, FirmwareVersion, BusType, MediaType, Size, HealthStatus FROM MSFT_PhysicalDisk"))
                    ev.Disks.Add(new DiskInfo
                    {
                        DeviceId = Wmi.Str(d, "DeviceId"),
                        Model = Wmi.Str(d, "FriendlyName"),
                        Serial = Wmi.Str(d, "SerialNumber"),
                        Firmware = Wmi.Str(d, "FirmwareVersion"),
                        BusType = BusName(Wmi.Num<ushort>(d, "BusType")),
                        MediaType = MediaName(Wmi.Num<ushort>(d, "MediaType")),
                        SizeBytes = Wmi.Num<ulong>(d, "Size") ?? 0,
                        Health = HealthName(Wmi.Num<ushort>(d, "HealthStatus")),
                    });
            });
            if (ev.Disks.Count == 0) return;

            Guard.Try(ev, "disks.system", () =>
            {
                var sys = "\"" + Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\') + "\"";
                foreach (var a in Wmi.Query(Wmi.Cim, "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
                {
                    if (Wmi.Str(a, "Dependent")?.Contains(sys) != true) continue;
                    var m = Regex.Match(Wmi.Str(a, "Antecedent") ?? "", @"Disk #(\d+)");
                    foreach (var d in ev.Disks.Where(x => m.Success && x.DeviceId == m.Groups[1].Value)) d.IsSystem = true;
                }
            });

            Guard.Try(ev, "disks.reliability", () =>
            {
                foreach (var r in Wmi.Query(Wmi.Storage, "SELECT * FROM MSFT_StorageReliabilityCounter"))
                {
                    var disk = ev.Disks.FirstOrDefault(x => x.DeviceId == Wmi.Str(r, "DeviceId"));
                    if (disk == null) continue;
                    disk.PowerOnHours = Wmi.Num<uint>(r, "PowerOnHours");
                    disk.TemperatureC = NonZero(Wmi.Num<byte>(r, "Temperature"));
                    disk.TemperatureMaxC = NonZero(Wmi.Num<byte>(r, "TemperatureMax"));
                    disk.WearPercent = Wmi.Num<byte>(r, "Wear");
                    disk.ReadErrorsUncorrected = Wmi.Num<ulong>(r, "ReadErrorsUncorrected");
                    disk.WriteErrorsUncorrected = Wmi.Num<ulong>(r, "WriteErrorsUncorrected");
                }
            });

            Guard.Try(ev, "disks.smart_ata", () =>
            {
                try { ReadAtaSmart(ev.Disks); }
                catch (System.Management.ManagementException e) when (e.ErrorCode == System.Management.ManagementStatus.NotSupported)
                {
                    // NVMe-only machine: nothing to read. Any other disk means its SMART is hidden from us.
                    if (ev.Disks.Any(d => d.BusType != "NVMe"))
                        ev.Errors["disks.smart_ata"] = "SMART ATA indisponível (controladora RAID/RST, máquina virtual ou adaptador USB)";
                }
            });

            foreach (var disk in ev.Disks.Where(d => d.BusType == "NVMe"))
                Guard.Try(ev, "disks.nvme." + disk.DeviceId, () => disk.Nvme = ParseNvmeHealth(QueryNvmeHealthLog(disk.DeviceId)));
        }

        static void ReadAtaSmart(List<DiskInfo> disks)
        {
            // root\wmi keys SMART by PnP instance; Win32_DiskDrive maps that instance to PhysicalDriveN.
            var pnpByIndex = Wmi.Query(Wmi.Cim, "SELECT Index, PNPDeviceID FROM Win32_DiskDrive")
                .ToDictionary(d => Wmi.Num<uint>(d, "Index")?.ToString() ?? "", d => Wmi.Str(d, "PNPDeviceID") ?? "");
            DiskInfo Find(string instance) => disks.FirstOrDefault(x =>
                pnpByIndex.TryGetValue(x.DeviceId ?? "", out var pnp) && pnp.Length > 0
                && instance.StartsWith(pnp, StringComparison.OrdinalIgnoreCase));

            foreach (var s in Wmi.Query(Wmi.Root, "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData"))
            {
                var disk = Find(Wmi.Str(s, "InstanceName") ?? "");
                if (disk != null && s["VendorSpecific"] is byte[] raw) disk.Smart = ParseAtaSmart(raw);
            }
            foreach (var s in Wmi.Query(Wmi.Root, "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus"))
            {
                var disk = Find(Wmi.Str(s, "InstanceName") ?? "");
                if (disk != null) disk.SmartPredictFailure = s["PredictFailure"] as bool?;
            }
        }

        // ATA SMART READ DATA: 2-byte revision, then 30 entries of 12 bytes:
        // id, flags(2), current, worst, raw(6, little endian), reserved.
        public static List<SmartAttribute> ParseAtaSmart(byte[] data)
        {
            var list = new List<SmartAttribute>();
            for (int off = 2; off + 12 <= data.Length && off < 2 + 30 * 12; off += 12)
            {
                byte id = data[off];
                if (id == 0) continue;
                ulong raw = 0;
                for (int i = 5; i >= 0; i--) raw = (raw << 8) | data[off + 5 + i];
                list.Add(new SmartAttribute
                {
                    Id = id,
                    Name = AtaNames.TryGetValue(id, out var n) ? n : null,
                    Current = data[off + 3],
                    Worst = data[off + 4],
                    Raw = raw,
                });
            }
            return list;
        }

        static readonly Dictionary<byte, string> AtaNames = new Dictionary<byte, string>
        {
            [1] = "Read Error Rate", [5] = "Reallocated Sectors", [9] = "Power-On Hours", [10] = "Spin Retry Count",
            [12] = "Power Cycle Count", [171] = "Program Fail Count", [172] = "Erase Fail Count",
            [174] = "Unexpected Power Loss", [177] = "Wear Leveling Count", [184] = "End-to-End Error",
            [187] = "Reported Uncorrectable", [188] = "Command Timeout", [190] = "Airflow Temperature",
            [192] = "Power-off Retract / Unsafe Shutdown", [194] = "Temperature", [196] = "Reallocation Events",
            [197] = "Current Pending Sectors", [198] = "Offline Uncorrectable", [199] = "UDMA CRC Errors",
            [231] = "SSD Life Left", [233] = "Media Wearout Indicator", [241] = "Total LBAs Written",
            [242] = "Total LBAs Read",
        };

        // NVMe SMART / Health Information log page (0x02), NVMe base spec figure "SMART / Health Information".
        public static NvmeHealth ParseNvmeHealth(byte[] log)
        {
            if (log == null || log.Length < 192) throw new ArgumentException("NVMe health log menor que 192 bytes");
            // 128-bit counters: the low 64 bits cover any real drive (2^64 units of 512 KB).
            ulong U(int off) => BitConverter.ToUInt64(log, off);
            return new NvmeHealth
            {
                CriticalWarning = log[0],
                TemperatureC = BitConverter.ToUInt16(log, 1) - 273,
                AvailableSpare = log[3],
                AvailableSpareThreshold = log[4],
                PercentageUsed = log[5],
                DataUnitsRead = U(32),
                DataUnitsWritten = U(48),
                PowerCycles = U(112),
                PowerOnHours = U(128),
                UnsafeShutdowns = U(144),
                MediaErrors = U(160),
                ErrorLogEntries = U(176),
            };
        }

        // IOCTL_STORAGE_QUERY_PROPERTY with StorageDeviceProtocolSpecificProperty: the inbox stornvme
        // driver returns the health log without any vendor tool. The handle has no read/write access:
        // query IOCTLs do not need it, and the disk cannot be touched through it.
        static byte[] QueryNvmeHealthLog(string deviceId)
        {
            const int StorageDeviceProtocolSpecificProperty = 50, PropertyStandardQuery = 0;
            const int ProtocolTypeNvme = 3, NVMeDataTypeLogPage = 2, LogPageHealth = 0x02, LogSize = 512;
            const int SpecificDataSize = 40; // STORAGE_PROTOCOL_SPECIFIC_DATA: 10 x uint32
            const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

            var buf = new byte[8 + SpecificDataSize + LogSize];
            void Put(int off, int v) => BitConverter.GetBytes(v).CopyTo(buf, off);
            Put(0, StorageDeviceProtocolSpecificProperty);
            Put(4, PropertyStandardQuery);
            Put(8, ProtocolTypeNvme);
            Put(12, NVMeDataTypeLogPage);
            Put(16, LogPageHealth);
            Put(20, 0);                // sub value
            Put(24, SpecificDataSize); // ProtocolDataOffset, relative to the specific-data block
            Put(28, LogSize);          // ProtocolDataLength

            using (var h = CreateFile(@"\\.\PhysicalDrive" + deviceId, 0, 3 /* share read|write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, buf.Length, buf, buf.Length, out _, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            // Reply is STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version, Size, then the specific-data block
            // whose ProtocolDataOffset/Length locate the log.
            int dataOffset = BitConverter.ToInt32(buf, 8 + 16), dataLength = BitConverter.ToInt32(buf, 8 + 20);
            if (dataLength < 192 || 8 + dataOffset + dataLength > buf.Length) throw new InvalidOperationException("Resposta NVMe inválida");
            var log = new byte[dataLength];
            Array.Copy(buf, 8 + dataOffset, log, 0, dataLength);
            return log;
        }

        static uint? NonZero(byte? v) => v > 0 ? v.Value : (uint?)null;

        static string BusName(ushort? b)
        {
            switch (b)
            {
                case 3: return "ATA"; case 7: return "USB"; case 8: return "RAID"; case 10: return "SAS";
                case 11: return "SATA"; case 12: return "SD"; case 17: return "NVMe"; case 1: return "SCSI";
                case 15: return "Virtual"; case 16: return "Storage Spaces";
                default: return b?.ToString() ?? "?";
            }
        }

        static string MediaName(ushort? m) => m == 3 ? "HDD" : m == 4 ? "SSD" : m == 5 ? "SCM" : "Não informado";

        static string HealthName(ushort? h) => h == 0 ? "Saudável" : h == 1 ? "Alerta" : h == 2 ? "Com falha" : "Desconhecido";

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
    }
}
