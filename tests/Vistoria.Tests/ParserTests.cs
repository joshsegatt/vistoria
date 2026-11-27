using System;
using System.Collections.Generic;
using Vistoria;
using Vistoria.Collectors;
using Xunit;

public class ParserTests
{
    [Fact]
    public void AtaSmart_reads_id_values_and_48bit_raw()
    {
        var data = new byte[512];
        // entry 0 at offset 2: id 5, current 100, worst 99, raw 0x0000_0102_0304
        data[2] = 5; data[5] = 100; data[6] = 99;
        data[7] = 0x04; data[8] = 0x03; data[9] = 0x02; data[10] = 0x01;
        // entry 1 at offset 14: id 9, raw 0xFFFF_0000_1234 (high bytes set: some vendors pack extra data)
        data[14] = 9; data[19] = 0x34; data[20] = 0x12; data[23] = 0xFF; data[24] = 0xFF;

        var attrs = Disks.ParseAtaSmart(data);

        Assert.Equal(2, attrs.Count);
        Assert.Equal(5, attrs[0].Id);
        Assert.Equal("Reallocated Sectors", attrs[0].Name);
        Assert.Equal(100, attrs[0].Current);
        Assert.Equal(99, attrs[0].Worst);
        Assert.Equal(0x01020304UL, attrs[0].Raw);
        Assert.Equal(0xFFFF00001234UL, attrs[1].Raw);
        // Power-on hours only trusts the low 32 bits.
        Assert.Equal(0x1234UL, Rules.PowerOnHours(new DiskInfo { Smart = attrs }));
    }

    [Fact]
    public void AtaSmart_ignores_empty_slots_and_short_buffers()
    {
        Assert.Empty(Disks.ParseAtaSmart(new byte[512]));
        Assert.Empty(Disks.ParseAtaSmart(new byte[10]));
    }

    [Fact]
    public void NvmeHealth_follows_spec_offsets()
    {
        var log = new byte[512];
        log[0] = 0x04;
        BitConverter.GetBytes((ushort)313).CopyTo(log, 1); // 313 K = 40 °C
        log[3] = 100; log[4] = 10; log[5] = 4;
        BitConverter.GetBytes(30001807UL).CopyTo(log, 48);
        BitConverter.GetBytes(472UL).CopyTo(log, 112);
        BitConverter.GetBytes(9583UL).CopyTo(log, 128);
        BitConverter.GetBytes(192UL).CopyTo(log, 144);
        BitConverter.GetBytes(3UL).CopyTo(log, 160);

        var n = Disks.ParseNvmeHealth(log);

        Assert.Equal(4, n.CriticalWarning);
        Assert.Equal(40, n.TemperatureC);
        Assert.Equal(100, n.AvailableSpare);
        Assert.Equal(10, n.AvailableSpareThreshold);
        Assert.Equal(4, n.PercentageUsed);
        Assert.Equal(30001807UL, n.DataUnitsWritten);
        Assert.Equal(472UL, n.PowerCycles);
        Assert.Equal(9583UL, n.PowerOnHours);
        Assert.Equal(192UL, n.UnsafeShutdowns);
        Assert.Equal(3UL, n.MediaErrors);
    }

    [Fact]
    public void NvmeHealth_rejects_truncated_log() =>
        Assert.Throws<ArgumentException>(() => Disks.ParseNvmeHealth(new byte[100]));

    [Fact]
    public void DumpHeader_64_and_32_bit()
    {
        var h64 = new byte[0x60];
        System.Text.Encoding.ASCII.GetBytes("PAGEDU64").CopyTo(h64, 0);
        BitConverter.GetBytes(0x124u).CopyTo(h64, 0x38);
        Assert.Equal(0x124u, Dumps.ParseBugCheck(h64));

        var h32 = new byte[0x60];
        System.Text.Encoding.ASCII.GetBytes("PAGEDUMP").CopyTo(h32, 0);
        BitConverter.GetBytes(0x14Cu).CopyTo(h32, 0x20); // MachineImageType (i386): must not be read as the code
        BitConverter.GetBytes(0x1Au).CopyTo(h32, 0x28);
        Assert.Equal(0x1Au, Dumps.ParseBugCheck(h32));

        Assert.Null(Dumps.ParseBugCheck(new byte[0x60]));                  // not a kernel dump
        Assert.Null(Dumps.ParseBugCheck(System.Text.Encoding.ASCII.GetBytes("PAGEDU64"))); // too short
    }

    [Theory]
    [InlineData("0x0000001a (0x0000000000041792, 0xffff, 0x0, 0x0)", 0x1Au)]
    [InlineData("0x00000124", 0x124u)]
    [InlineData("  0x1000007e (0x1, 0x2)", 0x1000007Eu)]
    public void BugCheck_text_from_event_1001(string text, uint expected) =>
        Assert.Equal(expected, Dumps.ParseBugCheckText(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não é um código")]
    public void BugCheck_text_garbage_is_null(string text) => Assert.Null(Dumps.ParseBugCheckText(text));

    [Fact]
    public void BugCheck_extended_flag_maps_to_same_name_and_hint()
    {
        Assert.Equal("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", Dumps.BugCheckName(0x1000007E));
        Assert.Equal("memória RAM", Dumps.BugCheckHint(0x1A));
        Assert.Null(Dumps.BugCheckHint(0x3B));
        Assert.Equal("0xDEAD", Dumps.BugCheckName(0xDEAD));
    }

    [Fact]
    public void Json_escapes_and_formats_invariantly()
    {
        var json = Json.Write(new Dictionary<string, object>
        {
            ["s"] = "aspas \" barra \\ quebra\n tab\t ctrl\u0001",
            ["d"] = new DateTime(2026, 11, 27, 3, 4, 5, DateTimeKind.Utc),
            ["n"] = 1234.5,
            ["u"] = ulong.MaxValue,
            ["b"] = true,
            ["e"] = Status.NotChecked,
            ["l"] = new[] { 1, 2 },
            ["x"] = null,
        });
        Assert.Contains("\"aspas \\\" barra \\\\ quebra\\n tab\\t ctrl\\u0001\"", json);
        Assert.Contains("\"2026-11-27T03:04:05Z\"", json);
        Assert.Contains("1234.5", json);
        Assert.Contains("18446744073709551615", json);
        Assert.Contains("\"NotChecked\"", json);
        Assert.Contains("\"x\": null", json);
    }

    [Fact]
    public void Json_writes_public_fields_of_evidence()
    {
        var json = Json.Write(new Evidence());
        Assert.Contains("\"Run\":", json);
        Assert.Contains("\"Checks\": []", json);
    }

    [Theory]
    [InlineData("simples", "simples")]
    [InlineData("Loja do Zé", "\"Loja do Zé\"")]
    [InlineData("", "\"\"")]
    [InlineData("tem \"aspas\"", "\"tem \\\"aspas\\\"\"")]
    [InlineData(@"C:\pasta com espaço\", @"""C:\pasta com espaço\\""")]
    public void Relaunch_arguments_survive_CommandLineToArgv(string arg, string expected) =>
        Assert.Equal(expected, Program.Quote(arg));
}
