using System;
using System.Collections.Generic;
using System.Linq;
using Vistoria;
using Xunit;

public class RulesTests
{
    static readonly DateTime Now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    static readonly string[] EventKeys =
        { "bsod", "unexpected_shutdown", "whea_error", "whea_warning", "disk_bad_block", "ntfs_corruption", "disk_io", "gpu_tdr", "clock_backwards", "log_cleared", "boots" };

    // A clean, fully collected machine: Windows installed a year ago on a disk with 1,000 h.
    static Evidence Healthy()
    {
        var ev = new Evidence();
        ev.Run.StartedUtc = Now;
        ev.Run.IsAdmin = true;
        ev.Machine.OsInstallDate = Now.AddDays(-365);
        ev.Machine.Ram.Add(new RamModule { Slot = "A", PartNumber = "KF556C36-16", SpeedMHz = 5600, CapacityBytes = 16UL << 30 });
        ev.Machine.Ram.Add(new RamModule { Slot = "B", PartNumber = "KF556C36-16", SpeedMHz = 5600, CapacityBytes = 16UL << 30 });
        ev.Disks.Add(new DiskInfo
        {
            DeviceId = "0", Model = "SSD", BusType = "NVMe", MediaType = "SSD", Health = "Saudável", IsSystem = true, SizeBytes = 1000000000000,
            Nvme = new NvmeHealth { AvailableSpare = 100, AvailableSpareThreshold = 10, PercentageUsed = 3, PowerCycles = 400, PowerOnHours = 1000, UnsafeShutdowns = 5, TemperatureC = 35 },
        });
        ev.Events.Logs.Add(new LogCoverage { Log = "System", Oldest = Now.AddDays(-200), Newest = Now, Records = 30000 });
        foreach (var k in EventKeys) ev.Events.Categories.Add(new EventCategory { Key = k, Name = k });
        ev.Devices.Add(new DeviceRecord { Class = "Processor", Name = "Intel Core i7", Present = true });
        return ev;
    }

    static Check Get(Evidence ev, string id) => Rules.Evaluate(ev).Single(c => c.Id == id);

    static void Hits(Evidence ev, string key, int count, string detail = null, int daysAgo = 10)
    {
        var cat = ev.Events.Categories.Single(c => c.Key == key);
        cat.Count = count;
        for (int i = 0; i < count; i++)
            cat.Recent.Add(new EventHit { TimeUtc = Now.AddDays(-daysAgo - i), Log = "System", Detail = detail });
        cat.CountLast90Days = cat.Recent.Count(h => h.TimeUtc > Now.AddDays(-Rules.RecentDays));
        cat.First = cat.Recent.Min(h => h.TimeUtc);
        cat.Last = cat.Recent.Max(h => h.TimeUtc);
    }

    [Fact]
    public void One_crash_seen_in_event_minidump_and_memory_dmp_is_counted_once()
    {
        var ev = Healthy();
        Hits(ev, "bsod", 1, "0x0000001a (0x41792, 0x0, 0x0, 0x0)");
        ev.Dumps.Add(new DumpFile { Path = @"C:\Windows\Minidump\112626-1.dmp", WrittenUtc = Now.AddDays(-10), BugCheck = 0x1A });
        ev.Dumps.Add(new DumpFile { Path = @"C:\Windows\MEMORY.DMP", WrittenUtc = Now.AddDays(-10), BugCheck = 0x1A });
        var c = Get(ev, "stability.bsod");
        Assert.Equal(Status.Warning, c.Status); // hinted once, not "repeated"
        Assert.StartsWith("1 tela azul", c.Detail);
    }

    [Fact]
    public void Empty_dump_folder_without_the_event_log_is_not_ok()
    {
        var ev = Healthy();
        ev.Events.Categories.RemoveAll(c => c.Key == "bsod");
        ev.Errors["events.bsod.System"] = "EventLogException: log corrompido";
        var c = Get(ev, "stability.bsod");
        Assert.Equal(Status.NotChecked, c.Status);
        Assert.Contains("log corrompido", c.Detail);
    }

    [Fact]
    public void Nothing_wrong_but_disk_unread_is_inconclusive_not_approved()
    {
        var ev = Healthy();
        ev.Disks[0].Nvme = null;
        var (label, level, complete) = Rules.Verdict(Rules.Evaluate(ev));
        Assert.Equal("Inconclusivo", label);
        Assert.Equal(Status.NotChecked, level);
        Assert.False(complete);
    }

    [Fact]
    public void Windows_reliability_counters_alone_do_not_make_a_disk_ok()
    {
        var ev = Healthy();
        ev.Disks[0].Nvme = null;
        ev.Disks[0].PowerOnHours = 500;
        ev.Disks[0].WearPercent = 2;
        var c = Get(ev, "disk.health.0");
        Assert.Equal(Status.NotChecked, c.Status);
        Assert.Contains("500 h ligado", c.Detail);
    }

    [Fact]
    public void Bad_block_on_a_pendrive_is_only_info()
    {
        var ev = Healthy();
        ev.Disks.Add(new DiskInfo { DeviceId = "2", BusType = "USB", Health = "Saudável" });
        Hits(ev, "disk_bad_block", 3, @"\Device\Harddisk2\DR2");
        var c = Get(ev, "disk.events");
        Assert.Equal(Status.Info, c.Status);
        Assert.Contains("removível", c.Detail);
    }

    [Fact]
    public void Internal_bad_block_buried_under_pendrive_noise_is_still_critical()
    {
        var ev = Healthy();
        ev.Disks.Add(new DiskInfo { DeviceId = "2", BusType = "USB", Health = "Saudável" });
        Hits(ev, "disk_bad_block", 25, @"\Device\Harddisk2\DR2"); // the newest 25 are all the pendrive
        Get(ev, "disk.events"); // sanity: fixture evaluates
        ev.Events.Categories.Single(x => x.Key == "disk_bad_block").Targets.AddRange(new[] { @"\Device\Harddisk2\DR2", @"\Device\Harddisk0\DR0" });
        Assert.Equal(Status.Critical, Get(ev, "disk.events").Status);
    }

    [Fact]
    public void The_pendrive_vistoria_runs_from_does_not_make_a_clean_machine_inconclusive()
    {
        var ev = Healthy();
        ev.Disks.Add(new DiskInfo { DeviceId = "1", Model = "SanDisk Cruzer", BusType = "USB", MediaType = "Não informado", Health = "Saudável" });
        var checks = Rules.Evaluate(ev);
        Assert.Equal(Status.Info, checks.Single(c => c.Id == "disk.health.1").Status);
        Assert.Equal("Aprovado", Rules.Verdict(checks).Label);
    }

    [Fact]
    public void Emmc_system_disk_on_the_sd_bus_without_smart_stays_not_checked()
    {
        var ev = Healthy();
        ev.Disks[0].Nvme = null;
        ev.Disks[0].BusType = "SD";
        Assert.Equal(Status.NotChecked, Get(ev, "disk.health.0").Status);
    }

    [Fact]
    public void Sata_ssd_wear_from_smart_is_shown_as_estimate_without_changing_status()
    {
        var ev = Healthy();
        ev.Disks[0].Nvme = null;
        ev.Disks[0].BusType = "SATA";
        ev.Disks[0].Smart.Add(new SmartAttribute { Id = 9, Current = 99, Raw = 1000 });
        ev.Disks[0].Smart.Add(new SmartAttribute { Id = 231, Current = 20, Raw = 20 }); // 20% life left, if 231 means that
        var c = Get(ev, "disk.health.0");
        Assert.Equal(Status.Ok, c.Status);
        Assert.Contains("desgaste estimado 80% (SMART 231", c.Detail);
    }

    [Fact]
    public void Failing_pendrive_does_not_fail_the_machine()
    {
        var ev = Healthy();
        ev.Disks.Add(new DiskInfo { DeviceId = "1", BusType = "USB", Health = "Com falha" });
        var checks = Rules.Evaluate(ev);
        Assert.Equal(Status.Info, checks.Single(c => c.Id == "disk.health.1").Status);
        Assert.Contains("COM FALHA", checks.Single(c => c.Id == "disk.health.1").Detail);
        Assert.Equal("Aprovado", Rules.Verdict(checks).Label);
    }

    [Fact]
    public void Log_cleared_with_security_log_unread_is_not_ok()
    {
        var ev = Healthy();
        ev.Errors["events.log_cleared.Security"] = "Acesso negado: rode como administrador";
        var c = Get(ev, "tamper.log_cleared");
        Assert.Equal(Status.NotChecked, c.Status);
        Assert.Contains("Acesso negado", c.Detail);
    }

    [Fact]
    public void Old_hardware_history_without_recent_errors_is_a_warning_not_a_fail()
    {
        var ev = Healthy();
        Hits(ev, "whea_error", 1, "Machine Check Exception", daysAgo: 400);
        Hits(ev, "bsod", 2, "0x0000001a (0x41792, 0x0, 0x0, 0x0)", daysAgo: 400);
        Assert.Equal(Status.Warning, Get(ev, "hardware.whea").Status);
        Assert.Equal(Status.Warning, Get(ev, "stability.bsod").Status);
    }

    [Fact]
    public void Unexpected_shutdown_count_uses_full_count_not_capped_list()
    {
        var ev = Healthy();
        Hits(ev, "unexpected_shutdown", 1);
        var cat = ev.Events.Categories.Single(x => x.Key == "unexpected_shutdown");
        cat.Count = 300; cat.CountLast90Days = 120; // Recent is capped at 25 by the collector
        var c = Get(ev, "stability.power");
        Assert.Equal(Status.Warning, c.Status);
        Assert.Contains("120 nos últimos 90 dias", c.Detail);
    }

    [Fact]
    public void Clean_machine_is_approved_and_complete()
    {
        var checks = Rules.Evaluate(Healthy());
        var (label, level, complete) = Rules.Verdict(checks);
        Assert.Equal("Aprovado", label);
        Assert.Equal(Status.Ok, level);
        Assert.True(complete);
        Assert.DoesNotContain(checks, c => c.Status == Status.Warning || c.Status == Status.Critical || c.Status == Status.NotChecked);
        Assert.All(checks, c => Assert.NotEmpty(c.Sources)); // every conclusion cites where it came from
    }

    [Fact]
    public void Disk_older_than_windows_flags_reinstall()
    {
        var ev = Healthy();
        ev.Machine.OsInstallDate = Now.AddDays(-10);
        ev.Disks[0].Nvme.PowerOnHours = 18000;
        var c = Get(ev, "history.disk_vs_install");
        Assert.Equal(Status.Warning, c.Status);
        Assert.Contains("reinstalado", c.Detail);
    }

    [Fact]
    public void Previous_install_from_upgrade_counts_as_original_install()
    {
        var ev = Healthy();
        ev.Machine.OsInstallDate = Now.AddDays(-10); // feature upgrade reset it
        ev.Machine.PreviousInstalls.Add(new OsInstall { InstallDate = Now.AddDays(-900), Product = "Windows 10 Pro", Build = "19045" });
        ev.Disks[0].Nvme.PowerOnHours = 18000; // < 900 days of calendar hours
        Assert.Equal(Status.Ok, Get(ev, "history.disk_vs_install").Status);
    }

    [Fact]
    public void Nvme_media_errors_fail_the_inspection()
    {
        var ev = Healthy();
        ev.Disks[0].Nvme.MediaErrors = 2;
        var checks = Rules.Evaluate(ev);
        Assert.Equal(Status.Critical, checks.Single(c => c.Id == "disk.health.0").Status);
        Assert.Equal("Reprovado", Rules.Verdict(checks).Label);
    }

    [Theory]
    [InlineData(70, Status.Warning)]
    [InlineData(95, Status.Critical)]
    public void Nvme_wear(byte used, Status expected)
    {
        var ev = Healthy();
        ev.Disks[0].Nvme.PercentageUsed = used;
        Assert.Equal(expected, Get(ev, "disk.health.0").Status);
    }

    [Fact]
    public void Nvme_critical_warning_bits_are_explained()
    {
        var ev = Healthy();
        ev.Disks[0].Nvme.CriticalWarning = 0x08;
        var c = Get(ev, "disk.health.0");
        Assert.Equal(Status.Critical, c.Status);
        Assert.Contains("somente leitura", c.Detail);
    }

    [Fact]
    public void Ata_pending_sectors_are_critical_and_few_reallocated_are_a_warning()
    {
        var ev = Healthy();
        ev.Disks[0].Nvme = null;
        ev.Disks[0].BusType = "SATA";
        ev.Disks[0].Smart = new List<SmartAttribute> { new SmartAttribute { Id = 9, Raw = 1000 }, new SmartAttribute { Id = 5, Raw = 3 } };
        Assert.Equal(Status.Warning, Get(ev, "disk.health.0").Status);

        ev.Disks[0].Smart.Add(new SmartAttribute { Id = 197, Raw = 8 });
        Assert.Equal(Status.Critical, Get(ev, "disk.health.0").Status);
    }

    [Fact]
    public void Many_unsafe_shutdowns_relative_to_power_cycles_is_a_warning()
    {
        var ev = Healthy();
        ev.Disks[0].Nvme.UnsafeShutdowns = 192;
        ev.Disks[0].Nvme.PowerCycles = 472;
        Assert.Equal(Status.Warning, Get(ev, "disk.health.0").Status);
    }

    [Fact]
    public void Disk_without_smart_is_not_checked_never_ok()
    {
        var ev = Healthy();
        ev.Disks[0].Nvme = null;
        ev.Errors["disks.nvme.0"] = "Acesso negado: rode como administrador";
        var checks = Rules.Evaluate(ev);
        var c = checks.Single(x => x.Id == "disk.health.0");
        Assert.Equal(Status.NotChecked, c.Status);
        Assert.Contains("Acesso negado", c.Detail);
        Assert.False(Rules.Verdict(checks).Complete);
        Assert.Equal(Status.NotChecked, checks.Single(x => x.Id == "history.disk_vs_install").Status);
    }

    [Fact]
    public void Windows_failing_health_status_is_critical_even_without_smart()
    {
        var ev = Healthy();
        ev.Disks[0].Nvme = null;
        ev.Disks[0].Health = "Com falha";
        Assert.Equal(Status.Critical, Get(ev, "disk.health.0").Status);
    }

    [Theory]
    [InlineData(55, null, Status.Critical)]
    [InlineData(75, null, Status.Warning)]
    [InlineData(95, 1200u, Status.Warning)]
    [InlineData(95, 300u, Status.Ok)]
    public void Battery_health(int pct, uint? cycles, Status expected)
    {
        var ev = Healthy();
        ev.Batteries.Add(new BatteryInfo { DesignCapacity_mWh = 50000, FullChargeCapacity_mWh = (uint)(500 * pct), CycleCount = cycles });
        Assert.Equal(expected, Get(ev, "battery.0").Status);
    }

    [Fact]
    public void Battery_without_capacity_is_not_checked()
    {
        var ev = Healthy();
        ev.Batteries.Add(new BatteryInfo { DesignCapacity_mWh = 0, FullChargeCapacity_mWh = 40000 });
        Assert.Equal(Status.NotChecked, Get(ev, "battery.0").Status);
    }

    [Fact]
    public void No_battery_on_desktop_is_info_not_a_problem() =>
        Assert.Equal(Status.Info, Get(Healthy(), "battery").Status);

    [Fact]
    public void Cleared_event_log_is_flagged_as_tampering()
    {
        var ev = Healthy();
        Hits(ev, "log_cleared", 1, daysAgo: 2);
        var c = Get(ev, "tamper.log_cleared");
        Assert.Equal(Status.Warning, c.Status);
        Assert.Equal("Adulteração", c.Area);
    }

    [Fact]
    public void Clock_set_back_is_flagged()
    {
        var ev = Healthy();
        Hits(ev, "clock_backwards", 1, "de 2026-11-01 para 2025-01-01");
        Assert.Equal(Status.Warning, Get(ev, "tamper.clock").Status);
    }

    [Fact]
    public void Repeated_hardware_bugcheck_is_critical()
    {
        var ev = Healthy();
        Hits(ev, "bsod", 2, "0x0000001a (0x41792, 0x0, 0x0, 0x0)");
        var c = Get(ev, "stability.bsod");
        Assert.Equal(Status.Critical, c.Status);
        Assert.Contains("MEMORY_MANAGEMENT", c.Detail);
        Assert.Contains("memória RAM", c.Detail);
    }

    [Fact]
    public void Single_old_software_bugcheck_is_only_info()
    {
        var ev = Healthy();
        Hits(ev, "bsod", 1, "0x0000003b (0xc0000005, 0x0, 0x0, 0x0)", daysAgo: 150);
        Assert.Equal(Status.Info, Get(ev, "stability.bsod").Status);
    }

    [Fact]
    public void Bugcheck_from_minidump_counts_even_without_event()
    {
        var ev = Healthy();
        ev.Dumps.Add(new DumpFile { Path = @"C:\Windows\Minidump\a.dmp", WrittenUtc = Now.AddDays(-3), BugCheck = 0x124 });
        ev.Dumps.Add(new DumpFile { Path = @"C:\Windows\Minidump\b.dmp", WrittenUtc = Now.AddDays(-2), BugCheck = 0x124 });
        Assert.Equal(Status.Critical, Get(ev, "stability.bsod").Status);
    }

    [Fact]
    public void Live_kernel_gpu_reports_are_not_counted_as_blue_screens()
    {
        var ev = Healthy();
        ev.Dumps.Add(new DumpFile { Path = @"C:\Windows\LiveKernelReports\WATCHDOG\x.dmp", WrittenUtc = Now.AddDays(-3), BugCheck = 0x141 });
        Assert.Equal(Status.Ok, Get(ev, "stability.bsod").Status);
        Assert.Equal(Status.Info, Get(ev, "hardware.gpu").Status);
    }

    [Fact]
    public void Uncorrected_whea_error_is_critical()
    {
        var ev = Healthy();
        Hits(ev, "whea_error", 1, "Machine Check Exception");
        Assert.Equal(Status.Critical, Get(ev, "hardware.whea").Status);
    }

    [Fact]
    public void Frequent_unexpected_shutdowns_are_a_warning()
    {
        var ev = Healthy();
        Hits(ev, "unexpected_shutdown", 6);
        Assert.Equal(Status.Warning, Get(ev, "stability.power").Status);
    }

    [Fact]
    public void Bad_block_in_log_is_critical()
    {
        var ev = Healthy();
        Hits(ev, "disk_bad_block", 1, @"\Device\Harddisk0\DR0");
        Assert.Equal(Status.Critical, Get(ev, "disk.events").Status);
    }

    [Fact]
    public void Short_event_history_is_a_warning()
    {
        var ev = Healthy();
        ev.Events.Logs[0].Oldest = Now.AddDays(-5);
        Assert.Equal(Status.Warning, Get(ev, "history.coverage").Status);
    }

    [Fact]
    public void Swapped_cpu_shows_in_parts_history()
    {
        var ev = Healthy();
        ev.Devices.Add(new DeviceRecord { Class = "Processor", Name = "Intel Pentium G4560", Present = false, FirstInstall = Now.AddDays(-300), LastRemoval = Now.AddDays(-20) });
        ev.Devices.Add(new DeviceRecord { Class = "Processor", Name = "Intel Pentium G4560", Present = false }); // one per logical core
        ev.Devices.Add(new DeviceRecord { Class = "Display", Name = "Microsoft Basic Display Adapter", Present = false });
        var c = Get(ev, "parts.removed");
        Assert.Equal(Status.Info, c.Status);
        Assert.Contains("Pentium G4560", c.Detail);
        Assert.Single(c.Detail.Split(new[] { "Pentium" }, StringSplitOptions.None).Skip(1)); // deduped
        Assert.DoesNotContain("Basic Display", c.Detail);
    }

    [Fact]
    public void Mixed_ram_is_info()
    {
        var ev = Healthy();
        ev.Machine.Ram[1].PartNumber = "OUTRO";
        Assert.Equal(Status.Info, Get(ev, "hardware.ram").Status);
    }

    [Fact]
    public void Missing_event_categories_are_not_checked()
    {
        var ev = Healthy();
        ev.Events.Categories.Clear();
        ev.Errors["events.bsod.System"] = "Acesso negado: rode como administrador";
        ev.Errors["dumps.minidump"] = "Acesso negado: rode como administrador";
        var checks = Rules.Evaluate(ev);
        Assert.Equal(Status.NotChecked, checks.Single(c => c.Id == "stability.bsod").Status);
        Assert.False(Rules.Verdict(checks).Complete);
    }

    [Fact]
    public void Running_without_admin_is_reported_in_coverage()
    {
        var ev = Healthy();
        ev.Run.IsAdmin = false;
        var c = Get(ev, "run.coverage");
        Assert.Equal(Status.NotChecked, c.Status);
        Assert.Contains("sem administrador", c.Detail);
    }

    [Fact]
    public void Report_escapes_machine_supplied_text()
    {
        var ev = Healthy();
        ev.Disks[0].Model = "<script>alert(1)</script>";
        ev.Checks = Rules.Evaluate(ev);
        var html = Report.Render(ev, "abc");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
