using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace Vistoria.Collectors
{
    static class Battery
    {
        // Same ACPI data powercfg /batteryreport reads, without spawning a process or writing a file.
        public static void Collect(Evidence ev) => Guard.Try(ev, "battery", () =>
        {
            // Many laptops implement capacity but not BatteryCycleCount: each class is optional on its own.
            var full = Optional("SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity")
                .ToDictionary(b => Wmi.Str(b, "InstanceName") ?? "", b => Wmi.Num<uint>(b, "FullChargedCapacity") ?? 0);
            var cycles = Optional("SELECT InstanceName, CycleCount FROM BatteryCycleCount")
                .ToDictionary(b => Wmi.Str(b, "InstanceName") ?? "", b => Wmi.Num<uint>(b, "CycleCount"));

            // No BatteryStaticData = no ACPI battery driver loaded: a desktop.
            foreach (var s in Optional("SELECT * FROM BatteryStaticData"))
            {
                var key = Wmi.Str(s, "InstanceName") ?? "";
                full.TryGetValue(key, out var fullCap);
                cycles.TryGetValue(key, out var cyc);
                ev.Batteries.Add(new BatteryInfo
                {
                    Name = Wmi.Str(s, "DeviceName"),
                    Manufacturer = Wmi.Str(s, "ManufactureName"),
                    Serial = Wmi.Str(s, "SerialNumber"),
                    DesignCapacity_mWh = Wmi.Num<uint>(s, "DesignedCapacity") ?? 0,
                    FullChargeCapacity_mWh = fullCap,
                    CycleCount = cyc > 0 ? cyc : null, // 0 = firmware does not report it
                });
            }

            // A laptop whose firmware skips the ACPI data must read "not verified", not "no battery".
            // ACPI\ only: a UPS on USB also shows up in Win32_Battery (as HID\...) and is not the machine's battery.
            if (ev.Batteries.Count == 0 && Wmi.Query(Wmi.Cim, "SELECT PNPDeviceID FROM Win32_Battery")
                    .Any(b => (Wmi.Str(b, "PNPDeviceID") ?? "").StartsWith(@"ACPI\", System.StringComparison.OrdinalIgnoreCase)))
                ev.Errors["battery"] = "o Windows vê uma bateria, mas o firmware não expõe a capacidade (BatteryStaticData)";
        });

        static List<ManagementBaseObject> Optional(string wql)
        {
            try { return Wmi.Query(Wmi.Root, wql); }
            catch (ManagementException e) when (e.ErrorCode == ManagementStatus.NotSupported || e.ErrorCode == ManagementStatus.InvalidClass)
            {
                return new List<ManagementBaseObject>();
            }
        }
    }
}
