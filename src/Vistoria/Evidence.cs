using System;
using System.Collections.Generic;

namespace Vistoria
{
    // Everything the collectors read, kept raw so evidencias.json can be re-checked later.
    // All timestamps are UTC.
    public class Evidence
    {
        public RunInfo Run = new RunInfo();
        public MachineInfo Machine = new MachineInfo();
        public List<DiskInfo> Disks = new List<DiskInfo>();
        public List<BatteryInfo> Batteries = new List<BatteryInfo>();
        public EventsInfo Events = new EventsInfo();
        public List<DeviceRecord> Devices = new List<DeviceRecord>();
        public List<DumpFile> Dumps = new List<DumpFile>();
        // Collector name -> why it could not read its source. Present here = "not verified", never "OK".
        public Dictionary<string, string> Errors = new Dictionary<string, string>();
        public List<Check> Checks = new List<Check>();
    }

    public class RunInfo
    {
        public string Tool = "Vistoria";
        public string Version;
        public DateTime StartedUtc;
        public DateTime FinishedUtc;
        public string ComputerName;
        public string OperatorUser;
        public bool IsAdmin;
        public string Shop;
        public string Technician;
    }

    public class MachineInfo
    {
        public string Manufacturer, Model, SystemSerial;
        public string BiosVendor, BiosVersion, BiosSerial;
        public DateTime? BiosDate;
        public string BoardManufacturer, BoardProduct, BoardSerial;
        public string Cpu;
        public int CpuCores, CpuThreads;
        public List<RamModule> Ram = new List<RamModule>();
        public List<string> Gpus = new List<string>();
        public string OsName, OsVersion, OsBuild;
        public DateTime? OsInstallDate;      // CurrentVersion\InstallDate: resets on every feature upgrade
        public List<OsInstall> PreviousInstalls = new List<OsInstall>(); // SYSTEM\Setup\Source OS (...)
        public DateTime? LastBoot;
        public bool WindowsOldPresent;
    }

    public class RamModule
    {
        public string Slot, Manufacturer, PartNumber, Serial;
        public ulong CapacityBytes;
        public uint SpeedMHz;
    }

    public class OsInstall
    {
        public string Product, Build;
        public DateTime? InstallDate;
        public string RegistryKey;
    }

    public class DiskInfo
    {
        public string DeviceId;   // N in \\.\PhysicalDriveN
        public string Model, Serial, Firmware, BusType, MediaType, Health;
        public ulong SizeBytes;
        public bool IsSystem;     // holds the Windows partition

        // Storage reliability counters (Windows Storage API, SATA and NVMe).
        public ulong? PowerOnHours;
        public uint? TemperatureC, TemperatureMaxC, WearPercent;
        public ulong? ReadErrorsUncorrected, WriteErrorsUncorrected;

        public bool? SmartPredictFailure;                              // ATA: drive's own failure prediction
        public List<SmartAttribute> Smart = new List<SmartAttribute>(); // ATA raw attributes
        public NvmeHealth Nvme;                                         // NVMe SMART / Health log (page 0x02)
    }

    public class SmartAttribute
    {
        public byte Id;
        public string Name;
        public byte Current, Worst;
        public ulong Raw;
    }

    public class NvmeHealth
    {
        public byte CriticalWarning;
        public int TemperatureC;
        public byte AvailableSpare, AvailableSpareThreshold, PercentageUsed;
        public ulong DataUnitsRead, DataUnitsWritten; // units of 512,000 bytes
        public ulong PowerCycles, PowerOnHours, UnsafeShutdowns, MediaErrors, ErrorLogEntries;
    }

    public class BatteryInfo
    {
        public string Name, Manufacturer, Serial;
        public uint DesignCapacity_mWh, FullChargeCapacity_mWh;
        public uint? CycleCount;
    }

    public class EventsInfo
    {
        public List<LogCoverage> Logs = new List<LogCoverage>();
        public List<EventCategory> Categories = new List<EventCategory>();
    }

    public class LogCoverage
    {
        public string Log;
        public DateTime? Oldest, Newest;
        public long? Records;
    }

    public class EventCategory
    {
        public string Key, Name;
        public int Count;
        public int CountLast90Days;     // relative to RunInfo.StartedUtc
        public DateTime? First, Last;
        public List<EventHit> Recent = new List<EventHit>(); // newest first, capped
    }

    public class EventHit
    {
        public DateTime TimeUtc;
        public string Log, Provider, Detail;
        public int Id;
    }

    public class DeviceRecord
    {
        public string Class, Name, InstanceId;
        public bool Present;
        public DateTime? FirstInstall, LastArrival, LastRemoval;
    }

    public class DumpFile
    {
        public string Path;
        public long SizeBytes;
        public DateTime WrittenUtc;
        public uint? BugCheck;
        public string BugCheckName;
    }

    public enum Status { Ok, Info, Warning, Critical, NotChecked }

    public class Check
    {
        public string Id, Area, Title, Detail;
        public Status Status;
        public List<string> Sources = new List<string>();
    }
}
