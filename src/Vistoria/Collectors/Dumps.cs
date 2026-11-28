using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Vistoria.Collectors
{
    static class Dumps
    {
        public static void Collect(Evidence ev)
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Guard.Try(ev, "dumps.minidump", () => Add(ev, Directory.Exists(Path.Combine(win, "Minidump"))
                ? Directory.GetFiles(Path.Combine(win, "Minidump"), "*.dmp") : new string[0]));
            Guard.Try(ev, "dumps.memory", () => { var f = Path.Combine(win, "MEMORY.DMP"); if (File.Exists(f)) Add(ev, new[] { f }); });
            Guard.Try(ev, "dumps.livekernel", () => Add(ev, Directory.Exists(Path.Combine(win, "LiveKernelReports"))
                ? Directory.GetFiles(Path.Combine(win, "LiveKernelReports"), "*.dmp", SearchOption.AllDirectories) : new string[0]));
        }

        static void Add(Evidence ev, string[] files)
        {
            foreach (var f in files)
            {
                var d = new DumpFile { Path = f };
                // Read-only, shared, 0x60 bytes: the header is all we need.
                try
                {
                    var fi = new FileInfo(f);
                    d.SizeBytes = fi.Length;
                    d.WrittenUtc = fi.LastWriteTimeUtc;
                    using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var head = new byte[0x60];
                        if (s.Read(head, 0, head.Length) == head.Length) d.BugCheck = ParseBugCheck(head);
                    }
                }
                catch (FileNotFoundException) { continue; } // deleted between listing and reading
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { } // locked or ACL'd: the file still counts

                d.BugCheckName = d.BugCheck is uint code ? BugCheckName(code) : null;
                ev.Dumps.Add(d);
            }
        }

        // Kernel dump header: "PAGE" + "DU64" (DUMP_HEADER64, BugCheckCode at 0x38) or "PAGE" + "DUMP"
        // (DUMP_HEADER32: 4-byte pointers, MachineImageType at 0x20, NumberProcessors 0x24, BugCheckCode 0x28).
        public static uint? ParseBugCheck(byte[] h)
        {
            if (h.Length < 0x40 || Encoding.ASCII.GetString(h, 0, 4) != "PAGE") return null;
            var kind = Encoding.ASCII.GetString(h, 4, 4);
            if (kind == "DU64") return BitConverter.ToUInt32(h, 0x38);
            if (kind == "DUMP") return BitConverter.ToUInt32(h, 0x28);
            return null;
        }

        // "0x0000001a (0x0000000000041792, ...)" from System event 1001, or the bare hex.
        public static uint? ParseBugCheckText(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            var end = s.IndexOfAny(new[] { ' ', '(' });
            var hex = end > 0 ? s.Substring(0, end) : s;
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
            return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : (uint?)null;
        }

        // 0x1000007E style codes are the same bugcheck with a "has extra params" flag.
        public static uint Normalize(uint code) => code & ~0x10000000u;

        public static string BugCheckName(uint code) =>
            Names.TryGetValue(Normalize(code), out var n) ? n.Name : $"0x{code:X}";

        // Hint = the part most often behind this bugcheck. Software bugchecks get null on purpose.
        public static string BugCheckHint(uint code) =>
            Names.TryGetValue(Normalize(code), out var n) ? n.Hint : null;

        static readonly Dictionary<uint, (string Name, string Hint)> Names = new Dictionary<uint, (string, string)>
        {
            [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", null),
            [0x19] = ("BAD_POOL_HEADER", "memória RAM"),
            [0x1A] = ("MEMORY_MANAGEMENT", "memória RAM"),
            [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", null),
            [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", null),
            [0x4E] = ("PFN_LIST_CORRUPT", "memória RAM"),
            [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", null),
            [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", "disco"),
            [0x77] = ("KERNEL_STACK_INPAGE_ERROR", "disco"),
            [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", null),
            [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", "processador ou memória"),
            [0x9C] = ("MACHINE_CHECK_EXCEPTION", "processador ou placa-mãe"),
            [0x9F] = ("DRIVER_POWER_STATE_FAILURE", null),
            [0xC2] = ("BAD_POOL_CALLER", null),
            [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", null),
            [0xEF] = ("CRITICAL_PROCESS_DIED", null),
            [0xF4] = ("CRITICAL_OBJECT_TERMINATION", "disco"),
            [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "processador"),
            [0x116] = ("VIDEO_TDR_FAILURE", "placa de vídeo"),
            [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "placa de vídeo"),
            [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "placa de vídeo"),
            [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "hardware (CPU, RAM, placa-mãe)"),
            [0x133] = ("DPC_WATCHDOG_VIOLATION", null),
            [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", null),
            [0x141] = ("VIDEO_ENGINE_TIMEOUT_DETECTED", "placa de vídeo"),
            [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "disco"),
            [0x1A8] = ("BUGCODE_ID_DRIVER", null),
        };
    }
}
