using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Vistoria.Collectors
{
    static class Machine
    {
        public static void Collect(Evidence ev)
        {
            var m = ev.Machine;

            Guard.Try(ev, "machine.identity", () =>
            {
                var cs = Wmi.Query(Wmi.Cim, "SELECT Manufacturer, Model FROM Win32_ComputerSystem").FirstOrDefault();
                if (cs != null) { m.Manufacturer = Wmi.Str(cs, "Manufacturer"); m.Model = Wmi.Str(cs, "Model"); }

                var prod = Wmi.Query(Wmi.Cim, "SELECT IdentifyingNumber FROM Win32_ComputerSystemProduct").FirstOrDefault();
                if (prod != null) m.SystemSerial = Wmi.Str(prod, "IdentifyingNumber");

                var bios = Wmi.Query(Wmi.Cim, "SELECT Manufacturer, SMBIOSBIOSVersion, SerialNumber, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
                if (bios != null)
                {
                    m.BiosVendor = Wmi.Str(bios, "Manufacturer");
                    m.BiosVersion = Wmi.Str(bios, "SMBIOSBIOSVersion");
                    m.BiosSerial = Wmi.Str(bios, "SerialNumber");
                    m.BiosDate = Wmi.Date(bios, "ReleaseDate");
                }

                var board = Wmi.Query(Wmi.Cim, "SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard").FirstOrDefault();
                if (board != null)
                {
                    m.BoardManufacturer = Wmi.Str(board, "Manufacturer");
                    m.BoardProduct = Wmi.Str(board, "Product");
                    m.BoardSerial = Wmi.Str(board, "SerialNumber");
                }
            });

            Guard.Try(ev, "machine.hardware", () =>
            {
                var cpus = Wmi.Query(Wmi.Cim, "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                if (cpus.Count > 0)
                {
                    m.Cpu = string.Join(" + ", cpus.Select(c => Wmi.Str(c, "Name")).Distinct());
                    m.CpuCores = cpus.Sum(c => (int)(Wmi.Num<uint>(c, "NumberOfCores") ?? 0));
                    m.CpuThreads = cpus.Sum(c => (int)(Wmi.Num<uint>(c, "NumberOfLogicalProcessors") ?? 0));
                }

                foreach (var r in Wmi.Query(Wmi.Cim, "SELECT * FROM Win32_PhysicalMemory"))
                    m.Ram.Add(new RamModule
                    {
                        Slot = Wmi.Str(r, "DeviceLocator"),
                        Manufacturer = Wmi.Str(r, "Manufacturer"),
                        PartNumber = Wmi.Str(r, "PartNumber"),
                        Serial = Wmi.Str(r, "SerialNumber"),
                        CapacityBytes = Wmi.Num<ulong>(r, "Capacity") ?? 0,
                        // ConfiguredClockSpeed is the real running speed; Speed is the module's rating.
                        SpeedMHz = Wmi.Num<uint>(r, "ConfiguredClockSpeed") ?? Wmi.Num<uint>(r, "Speed") ?? 0,
                    });

                foreach (var g in Wmi.Query(Wmi.Cim, "SELECT Name FROM Win32_VideoController"))
                    m.Gpus.Add(Wmi.Str(g, "Name"));
            });

            Guard.Try(ev, "machine.os", () =>
            {
                var os = Wmi.Query(Wmi.Cim, "SELECT Caption, Version, BuildNumber, LastBootUpTime FROM Win32_OperatingSystem").FirstOrDefault();
                if (os != null)
                {
                    m.OsName = Wmi.Str(os, "Caption");
                    m.OsVersion = Wmi.Str(os, "Version");
                    m.OsBuild = Wmi.Str(os, "BuildNumber");
                    m.LastBoot = Wmi.Date(os, "LastBootUpTime");
                }
                var sysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                m.WindowsOldPresent = Directory.Exists(Path.Combine(sysDrive, "Windows.old"));
            });

            Guard.Try(ev, "machine.install_history", () => ReadInstallHistory(m));
        }

        static void ReadInstallHistory(MachineInfo m)
        {
            // Registry64 so a 32-bit host still sees the real SOFTWARE hive.
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (var cv = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    m.OsInstallDate = UnixDate(cv?.GetValue("InstallDate"));

                // Each feature upgrade leaves the previous install here as "Source OS (Updated on ...)".
                using (var setup = hklm.OpenSubKey(@"SYSTEM\Setup"))
                {
                    if (setup != null)
                        foreach (var name in setup.GetSubKeyNames().Where(n => n.StartsWith("Source OS", StringComparison.OrdinalIgnoreCase)))
                            using (var k = setup.OpenSubKey(name))
                                m.PreviousInstalls.Add(new OsInstall
                                {
                                    Product = k?.GetValue("ProductName") as string,
                                    Build = k?.GetValue("CurrentBuild") as string,
                                    InstallDate = UnixDate(k?.GetValue("InstallDate")),
                                    RegistryKey = @"HKLM\SYSTEM\Setup\" + name,
                                });
                }
            }
            m.PreviousInstalls.Sort((a, b) => Nullable.Compare(a.InstallDate, b.InstallDate));
        }

        static DateTime? UnixDate(object v) =>
            v is int i && i != 0 ? DateTimeOffset.FromUnixTimeSeconds((uint)i).UtcDateTime : (DateTime?)null;
    }
}
