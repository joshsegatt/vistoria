using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vistoria.Collectors;

namespace Vistoria
{
    // Pure: Evidence in, checks out. No I/O, so every rule is unit-tested with fixtures.
    public static class Rules
    {
        // Thresholds in one place. Sources: SMART/NVMe conventions used by CrystalDiskInfo and smartmontools,
        // and the usual 80% battery-health line laptop vendors use for warranty.
        public const int ReallocatedCritical = 50;
        public const int NvmeUsedWarning = 70, NvmeUsedCritical = 90;
        public const int BatteryWarningPct = 80, BatteryCriticalPct = 60, BatteryCyclesWarning = 1000;
        public const int UnsafeShutdownMin = 20; public const double UnsafeShutdownRatio = 0.10;
        public const int RecentDays = 90, UnexpectedShutdownWarning = 5, BsodRecentWarning = 3;
        public const int WheaWarningCount = 10, DiskIoWarning = 5, GpuTdrWarning = 3, ShortHistoryDays = 30;
        public const int DiskOlderMarginDays = 30;

        static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

        public static List<Check> Evaluate(Evidence ev)
        {
            var now = ev.Run.StartedUtc == default ? DateTime.UtcNow : ev.Run.StartedUtc;
            var c = new List<Check>();
            foreach (var d in ev.Disks) c.Add(DiskHealth(ev, d));
            if (ev.Disks.Count == 0) c.Add(Make("disk.health", "Disco", Status.NotChecked, "Saúde dos discos",
                "Nenhum disco lido. " + Why(ev, "disks.inventory"), "MSFT_PhysicalDisk"));
            c.Add(DiskEvents(ev));
            c.Add(DiskVsInstall(ev, now));
            c.Add(InstallHistory(ev, now));
            c.Add(Coverage(ev, now));
            c.Add(Bsod(ev, now));
            c.Add(UnexpectedShutdowns(ev, now));
            c.Add(Whea(ev));
            c.Add(Gpu(ev));
            c.Add(LogCleared(ev));
            c.Add(Clock(ev));
            c.AddRange(Batteries(ev));
            c.Add(RemovedParts(ev));
            c.Add(Ram(ev));
            c.Add(Collection(ev));
            return c;
        }

        public static (string Label, Status Level, bool Complete) Verdict(IEnumerable<Check> checks)
        {
            var list = checks.ToList();
            bool complete = !list.Any(x => x.Status == Status.NotChecked && (x.Area == "Disco" || x.Area == "Estabilidade"));
            if (list.Any(x => x.Status == Status.Critical)) return ("Reprovado", Status.Critical, complete);
            if (list.Any(x => x.Status == Status.Warning)) return ("Aprovado com apontamentos", Status.Warning, complete);
            // Nothing wrong found, but the disk or stability sources were not read: never print a green "Aprovado" on that.
            if (!complete) return ("Inconclusivo", Status.NotChecked, false);
            return ("Aprovado", Status.Ok, true);
        }

        static Check DiskHealth(Evidence ev, DiskInfo d)
        {
            var crit = new List<string>(); var warn = new List<string>(); var facts = new List<string>(); var src = new List<string>();
            string name = $"Disco {d.DeviceId}: {d.Model ?? "?"} ({d.BusType}, {d.MediaType}, {Size(d.SizeBytes)}){(d.IsSystem ? " [sistema]" : "")}";

            if (d.Health == "Com falha") crit.Add("o Windows marca o disco como COM FALHA");
            else if (d.Health == "Alerta") warn.Add("o Windows marca o disco em ALERTA");
            src.Add($"MSFT_PhysicalDisk.HealthStatus = {d.Health}");

            if (d.SmartPredictFailure == true) { crit.Add("o próprio disco prevê falha (SMART)"); src.Add("MSStorageDriver_FailurePredictStatus"); }

            if (d.Smart.Count > 0)
            {
                src.Add("SMART ATA (MSStorageDriver_FailurePredictData)");
                ulong A(byte id) => d.Smart.FirstOrDefault(a => a.Id == id)?.Raw ?? 0;
                var realloc = A(5); var pending = A(197); var uncorr = A(198);
                if (realloc >= ReallocatedCritical) crit.Add($"{realloc} setores realocados (SMART 5)");
                else if (realloc > 0) warn.Add($"{realloc} setores realocados (SMART 5)");
                if (pending > 0) crit.Add($"{pending} setores pendentes (SMART 197)");
                if (uncorr > 0) crit.Add($"{uncorr} setores irrecuperáveis (SMART 198)");
                if (A(187) > 0) warn.Add($"{A(187)} erros não corrigidos reportados (SMART 187)");
                if (A(199) > 0) warn.Add($"{A(199)} erros de comunicação, cabo ou conector (SMART 199)");
            }

            if (d.Nvme is NvmeHealth n)
            {
                src.Add("Log de saúde NVMe (página 0x02)");
                if (n.CriticalWarning != 0) crit.Add($"alerta crítico NVMe ativo: {NvmeWarningText(n.CriticalWarning)}");
                if (n.MediaErrors > 0) crit.Add($"{n.MediaErrors} erros de integridade de mídia");
                if (n.AvailableSpareThreshold > 0 && n.AvailableSpare < n.AvailableSpareThreshold) crit.Add($"reserva disponível {n.AvailableSpare}% abaixo do limite {n.AvailableSpareThreshold}%");
                if (n.PercentageUsed >= NvmeUsedCritical) crit.Add($"vida útil consumida: {n.PercentageUsed}%");
                else if (n.PercentageUsed >= NvmeUsedWarning) warn.Add($"vida útil consumida: {n.PercentageUsed}%");
                facts.Add($"vida útil consumida {n.PercentageUsed}%");
                facts.Add($"{TB(n.DataUnitsWritten * 512000UL)} gravados");
            }
            else if (d.WearPercent is uint wear && d.PowerOnHours != null)
            {
                if (wear >= NvmeUsedCritical) crit.Add($"desgaste {wear}%");
                else if (wear >= NvmeUsedWarning) warn.Add($"desgaste {wear}%");
                if (wear > 0) facts.Add($"desgaste {wear}%");
            }
            // Vendors reuse these attribute IDs for other things: shown as an estimate, never used for the verdict.
            else if (SmartWear(d) is uint est) facts.Add($"desgaste estimado {est}% (SMART {SmartWearId(d)}, varia por fabricante)");

            if (d.ReadErrorsUncorrected > 0 || d.WriteErrorsUncorrected > 0)
                warn.Add($"erros não corrigidos: {d.ReadErrorsUncorrected ?? 0} leitura, {d.WriteErrorsUncorrected ?? 0} escrita");
            if (d.PowerOnHours != null) src.Add("MSFT_StorageReliabilityCounter");

            var poh = PowerOnHours(d);
            if (poh != null) facts.Insert(0, $"{N(poh)} h ligado (~{N(poh / 24)} dias)");
            var cycles = PowerCycles(d);
            if (cycles != null) facts.Add($"{cycles} vezes ligado");

            var unsafeCount = UnsafeShutdowns(d);
            if (unsafeCount != null && cycles > 0)
            {
                facts.Add($"{unsafeCount} desligamentos sem aviso");
                if (unsafeCount >= UnsafeShutdownMin && (double)unsafeCount / cycles.Value >= UnsafeShutdownRatio)
                    warn.Add($"{unsafeCount} de {cycles} ligamentos terminaram sem desligar corretamente (queda de energia, travamento ou botão)");
            }
            var temp = d.Nvme != null ? (uint?)Math.Max(0, d.Nvme.TemperatureC) : d.TemperatureC;
            if (temp > 0) facts.Add($"{temp} °C agora");

            // "OK" needs the sector / media-error counters; Windows' own counters alone don't cover them.
            bool fullSmart = d.Smart.Count > 0 || d.Nvme != null;
            Status st = crit.Count > 0 ? Status.Critical : warn.Count > 0 ? Status.Warning : fullSmart ? Status.Ok : Status.NotChecked;
            // The pendrive Vistoria runs from is a disk too. Without SMART it says nothing about the machine, so it
            // must not turn every vistoria "Inconclusivo". Not the system disk: cheap laptops boot from eMMC on the SD bus.
            // A worn-out pendrive is not the machine's fault either.
            if (!d.IsSystem && (d.BusType == "USB" || d.BusType == "SD"))
                return Make("disk.health." + d.DeviceId, "Disco", Status.Info, name,
                    "Disco removível (USB ou cartão), por exemplo o pendrive do Vistoria: fica fora do veredito."
                    + (crit.Count + warn.Count > 0 ? " Apontamentos dele: " + string.Join("; ", crit.Concat(warn)) + "." : "")
                    + (facts.Count > 0 ? " Lido: " + string.Join(", ", facts) + "." : ""), src.ToArray());
            string detail = st == Status.NotChecked
                ? $"SMART completo não lido: setores ruins e erros de mídia não foram verificados. {Why(ev, "disks.nvme." + d.DeviceId, "disks.smart_ata", "disks.reliability")}"
                  + (d.BusType == "USB" ? " Discos USB costumam esconder o SMART atrás do adaptador." : "")
                  + $" O Windows informa: {d.Health}." + (facts.Count > 0 ? " Lido: " + string.Join(", ", facts) + "." : "")
                : string.Join("; ", crit.Concat(warn).DefaultIfEmpty("nenhum sinal de defeito")) + ". " + string.Join(", ", facts) + (facts.Count > 0 ? "." : "");
            return Make("disk.health." + d.DeviceId, "Disco", st, name, detail, src.ToArray());
        }

        static Check DiskEvents(Evidence ev)
        {
            var bad = Cat(ev, "disk_bad_block"); var ntfs = Cat(ev, "ntfs_corruption"); var io = Cat(ev, "disk_io");
            if (bad == null && ntfs == null && io == null)
                return Make("disk.events", "Disco", Status.NotChecked, "Erros de disco no log do Windows", Why(ev, "events.disk_bad_block.System"), "Log System");
            var parts = new List<string>();
            Status st = Status.Ok;
            if (bad?.Count > 0)
            {
                // Pendrives and SD cards log disk 7 all the time; only an internal disk's bad block fails the machine.
                bool internalDisk = bad.Targets.Concat(bad.Recent.Select(h => h.Detail)).Any(t => IsInternalDisk(ev, t));
                st = internalDisk ? Status.Critical : Status.Info;
                parts.Add($"{P(bad.Count, "bloco defeituoso", "blocos defeituosos")} (último {D(bad.Last)})"
                    + (internalDisk ? " em disco interno" : " só em disco removível ou externo (pendrive, cartão)"));
            }
            if (ntfs?.Count > 0) { st = Max(st, Status.Warning); parts.Add($"{P(ntfs.Count, "corrupção", "corrupções")} de NTFS (última {D(ntfs.Last)})"); }
            if (io?.Count > 0) { st = Max(st, io.Count >= DiskIoWarning ? Status.Warning : Status.Info); parts.Add($"{P(io.Count, "falha ou repetição", "falhas ou repetições")} de E/S (última {D(io.Last)})"); }
            return Make("disk.events", "Disco", st, "Erros de disco no log do Windows",
                parts.Count == 0 ? "Nenhum erro de disco registrado no período coberto pelo log." : string.Join("; ", parts) + ".",
                "Log System: disk 7, disk 51/153, NTFS 55, storport 129");
        }

        // The key cross-check: a disk can't be powered on longer than calendar time since Windows was installed on it.
        static Check DiskVsInstall(Evidence ev, DateTime now)
        {
            const string title = "Idade do disco x instalação do Windows";
            var sys = ev.Disks.FirstOrDefault(d => d.IsSystem) ?? (ev.Disks.Count == 1 ? ev.Disks[0] : null);
            var poh = sys == null ? null : PowerOnHours(sys);
            var install = OriginalInstall(ev.Machine);
            if (poh == null || install == null)
                return Make("history.disk_vs_install", "Histórico", Status.NotChecked, title,
                    poh == null ? "Horas de uso do disco do sistema não lidas. " + Why(ev, "disks.reliability", "disks.smart_ata") : "Data de instalação do Windows não encontrada.",
                    "SMART / NVMe", @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\InstallDate", @"HKLM\SYSTEM\Setup\Source OS");
            var calendarHours = (now - install.Value).TotalHours;
            var src = new[] { $"Disco {sys.DeviceId}: {N(poh)} h ligado",$"Windows mais antigo registrado: {D(install)}" };
            if (poh.Value > calendarHours + DiskOlderMarginDays * 24)
                return Make("history.disk_vs_install", "Histórico", Status.Warning, title,
                    $"O disco do sistema tem {N(poh)} h ligado (~{N(poh / 24)} dias), mas o Windows mais antigo registrado é de {D(install)} ({N((now - install.Value).TotalDays)} dias atrás). "
                    + $"O Windows foi reinstalado num disco já usado: o histórico de antes de {D(install)} foi apagado.", src);
            return Make("history.disk_vs_install", "Histórico", Status.Ok, title,
                $"Compatível: {N(poh)} h de uso desde a instalação de {D(install)}.", src);
        }

        static Check InstallHistory(Evidence ev, DateTime now)
        {
            var m = ev.Machine; var install = OriginalInstall(m);
            if (install == null)
                return Make("history.install", "Histórico", Status.NotChecked, "Instalações do Windows", Why(ev, "machine.install_history"), @"HKLM\SYSTEM\Setup");
            var parts = new List<string> { $"Primeira instalação registrada: {D(install)} ({N((now - install.Value).TotalDays)} dias)" };
            if (m.PreviousInstalls.Count > 0) parts.Add($"{m.PreviousInstalls.Count} atualizações de versão desde então ({string.Join(", ", m.PreviousInstalls.Select(p => p.Product + " " + p.Build))})");
            if (m.OsInstallDate != null && m.OsInstallDate != install) parts.Add($"versão atual instalada em {D(m.OsInstallDate)}");
            if (m.WindowsOldPresent) parts.Add("pasta Windows.old presente (reinstalação ou atualização recente)");
            return Make("history.install", "Histórico", Status.Info, "Instalações do Windows", string.Join("; ", parts) + ".",
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\InstallDate", @"HKLM\SYSTEM\Setup\Source OS (*)");
        }

        static Check Coverage(Evidence ev, DateTime now)
        {
            var sys = ev.Events.Logs.FirstOrDefault(l => l.Log == "System");
            if (sys?.Oldest == null)
                return Make("history.coverage", "Histórico", Status.NotChecked, "Período coberto pelo histórico", Why(ev, "events.coverage.System"), "Log System");
            var days = (now - sys.Oldest.Value).TotalDays;
            var text = $"O log System vai de {D(sys.Oldest)} até {D(sys.Newest)} ({N(days)} dias, {N(sys.Records)} eventos). Travamentos e erros antes disso não aparecem.";
            return Make("history.coverage", "Histórico", days < ShortHistoryDays ? Status.Warning : Status.Info,
                "Período coberto pelo histórico",
                days < ShortHistoryDays ? "Histórico curto. " + text : text, "Log System (primeiro e último evento)");
        }

        static Check Bsod(Evidence ev, DateTime now)
        {
            const string title = "Telas azuis";
            var cat = Cat(ev, "bsod");
            var dumps = ev.Dumps.Where(x => x.Path.IndexOf("LiveKernelReports", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            bool dumpsRead = !ev.Errors.ContainsKey("dumps.minidump");
            if (cat == null && !dumpsRead)
                return Make("stability.bsod", "Estabilidade", Status.NotChecked, title, Why(ev, "events.bsod.System", "dumps.minidump"), "Log System 1001", @"C:\Windows\Minidump");

            // One crash leaves an event 1001, a minidump and MEMORY.DMP: count codes from a single source, never the sum.
            var mini = dumps.Where(x => x.Path.IndexOf(@"\Minidump\", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var crashes = mini.Count > 0 ? mini : dumps;
            List<uint> Codes(IEnumerable<uint?> src) => src.Where(x => x != null).Select(x => Dumps.Normalize(x.Value)).ToList();
            var eventCodes = Codes(cat?.Recent.Select(h => Dumps.ParseBugCheckText(h.Detail)) ?? Enumerable.Empty<uint?>());
            var dumpCodes = Codes(crashes.Select(x => x.BugCheck));
            var codes = eventCodes.Count >= dumpCodes.Count ? eventCodes : dumpCodes;
            int total = Math.Max(cat?.Count ?? 0, crashes.Count);
            var src = new List<string>();
            if (cat != null) src.Add($"Log System, WER-SystemErrorReporting 1001: {cat.Count}");
            if (dumpsRead) src.Add($@"C:\Windows\Minidump e MEMORY.DMP: {dumps.Count} arquivos");
            // An empty dump folder alone proves little (Disk Cleanup empties it): OK needs the event log too.
            if (total == 0 && cat == null)
                return Make("stability.bsod", "Estabilidade", Status.NotChecked, title,
                    "Nenhum dump encontrado, mas o log System não foi lido. " + Why(ev, "events.bsod.System"), src.ToArray());
            if (total == 0)
                return Make("stability.bsod", "Estabilidade", Status.Ok, title, "Nenhuma tela azul registrada no período coberto.", src.ToArray());

            var recent = Math.Max(cat?.CountLast90Days ?? 0, crashes.Count(x => x.WrittenUtc > now.AddDays(-RecentDays)));
            var groups = codes.GroupBy(x => x).OrderByDescending(g => g.Count()).ToList();
            var hinted = groups.Where(g => Dumps.BugCheckHint(g.Key) != null).ToList();
            // Critical = the same hardware-pointing code repeats AND the machine still crashes; old history alone is a warning.
            Status st = hinted.Any(g => g.Count() >= 2) && recent > 0 ? Status.Critical
                : hinted.Count > 0 || recent >= BsodRecentWarning ? Status.Warning : Status.Info;
            var last = new[] { cat?.Last, crashes.Count > 0 ? crashes.Max(x => x.WrittenUtc) : (DateTime?)null }.Max();
            var detail = $"{P(total, "tela azul", "telas azuis")}; {recent} nos últimos {RecentDays} dias; última em {D(last)}.";
            if (groups.Count > 0)
                detail += " Códigos: " + string.Join(", ", groups.Select(g => $"{Dumps.BugCheckName(g.Key)} ({g.Count()}×{(Dumps.BugCheckHint(g.Key) is string h ? ", aponta para " + h : "")})")) + ".";
            return Make("stability.bsod", "Estabilidade", st, title, detail, src.ToArray());
        }

        static Check UnexpectedShutdowns(Evidence ev, DateTime now)
        {
            const string title = "Desligamentos inesperados";
            var cat = Cat(ev, "unexpected_shutdown");
            if (cat == null) return Make("stability.power", "Estabilidade", Status.NotChecked, title, Why(ev, "events.unexpected_shutdown.System"), "Log System, Kernel-Power 41");
            var recent = cat.CountLast90Days;
            Status st = recent >= UnexpectedShutdownWarning ? Status.Warning : cat.Count > 0 ? Status.Info : Status.Ok;
            return Make("stability.power", "Estabilidade", st, title,
                cat.Count == 0 ? "Nenhum desligamento inesperado no período coberto."
                    : $"{P(cat.Count, "vez", "vezes")} o PC desligou ou reiniciou sem passar pelo desligamento normal; {recent} nos últimos {RecentDays} dias; último em {D(cat.Last)}. Causas comuns: queda de energia, fonte, superaquecimento, travamento ou botão segurado.",
                $"Log System, Kernel-Power 41: {cat.Count}");
        }

        static Check Whea(Evidence ev)
        {
            const string title = "Erros de hardware (WHEA)";
            var err = Cat(ev, "whea_error"); var warn = Cat(ev, "whea_warning");
            if (err == null && warn == null) return Make("hardware.whea", "Hardware", Status.NotChecked, title, Why(ev, "events.whea_error.System"), "Log System, WHEA-Logger");
            int e = err?.Count ?? 0, w = warn?.Count ?? 0;
            int eRecent = err?.CountLast90Days ?? 0;
            // An uncorrected error in the last 90 days fails the machine; one only in old history may be a part already replaced.
            Status st = eRecent > 0 ? Status.Critical : e > 0 || w >= WheaWarningCount ? Status.Warning : w > 0 ? Status.Info : Status.Ok;
            var detail = e == 0 && w == 0 ? "Nenhum erro de hardware reportado pelo processador, memória ou barramento PCIe."
                : $"{P(e, "erro não corrigido", "erros não corrigidos")} ({eRecent} nos últimos {RecentDays} dias) e {P(w, "corrigido", "corrigidos")}, reportados pelo próprio hardware (processador, memória, PCIe)."
                  + (err?.Recent.FirstOrDefault()?.Detail is string s ? " Último erro: " + s : "");
            return Make("hardware.whea", "Hardware", st, title, detail, $"Log System, WHEA-Logger: {e} erro(s), {w} aviso(s)");
        }

        static Check Gpu(Evidence ev)
        {
            const string title = "Placa de vídeo travando";
            var cat = Cat(ev, "gpu_tdr");
            var live = ev.Dumps.Where(x => x.Path.IndexOf("LiveKernelReports", StringComparison.OrdinalIgnoreCase) >= 0
                                           && x.BugCheck is uint b && (Dumps.BugCheckHint(b) ?? "").Contains("vídeo")).ToList();
            if (cat == null)
                return Make("hardware.gpu", "Hardware", Status.NotChecked, title, Why(ev, "events.gpu_tdr.System"), "Log System, Display 4101");
            int n = Math.Max(cat?.Count ?? 0, live.Count);
            Status st = n >= GpuTdrWarning ? Status.Warning : n > 0 ? Status.Info : Status.Ok;
            return Make("hardware.gpu", "Hardware", st, title,
                n == 0 ? "O driver de vídeo nunca precisou ser reiniciado por travamento no período coberto."
                    : $"O driver de vídeo travou e foi reiniciado {n} vezes (último em {D(new[] { cat?.Last, live.Count > 0 ? live.Max(x => x.WrittenUtc) : (DateTime?)null }.Max())}). Pode ser driver, superaquecimento ou defeito na placa.",
                $"Log System, Display 4101: {cat?.Count ?? 0}", $@"C:\Windows\LiveKernelReports (vídeo): {live.Count}");
        }

        static Check LogCleared(Evidence ev)
        {
            const string title = "Logs do Windows apagados";
            var cat = Cat(ev, "log_cleared");
            if (cat == null) return Make("tamper.log_cleared", "Adulteração", Status.NotChecked, title, Why(ev, "events.log_cleared.System"), "System 104", "Security 1102");
            var securityNote = ev.Errors.ContainsKey("events.log_cleared.Security") ? " O log Security não foi lido (sem administrador)." : "";
            // Half the source unread is not a clean bill: a cleared Security log is exactly what would be missed.
            if (cat.Count == 0 && (ev.Errors.ContainsKey("events.log_cleared.Security") || ev.Errors.ContainsKey("events.log_cleared.System")))
                return Make("tamper.log_cleared", "Adulteração", Status.NotChecked, title,
                    "Nenhuma limpeza encontrada no que foi lido, mas parte da fonte não foi lida. " + Why(ev, "events.log_cleared.Security", "events.log_cleared.System"),
                    "System 104", "Security 1102");
            if (cat.Count == 0)
                return Make("tamper.log_cleared", "Adulteração", Status.Ok, title, "Nenhuma limpeza de log registrada." + securityNote, "System 104", "Security 1102");
            return Make("tamper.log_cleared", "Adulteração", Status.Warning, title,
                $"Alguém apagou logs do Windows {cat.Count} vez(es): {string.Join(", ", cat.Recent.Select(h => $"{h.Log} em {DT(h.TimeUtc)}"))}. Apagar logs esconde travamentos e erros anteriores." + securityNote,
                "System 104", "Security 1102");
        }

        static Check Clock(Evidence ev)
        {
            const string title = "Relógio do sistema voltado";
            var cat = Cat(ev, "clock_backwards");
            if (cat == null) return Make("tamper.clock", "Adulteração", Status.NotChecked, title, Why(ev, "events.clock_backwards.System"), "Log System, Kernel-General 1");
            return cat.Count == 0
                ? Make("tamper.clock", "Adulteração", Status.Ok, title, "O relógio nunca foi atrasado mais de 24 h.", "Log System, Kernel-General 1")
                : Make("tamper.clock", "Adulteração", Status.Warning, title,
                    $"O relógio foi atrasado mais de 24 h {cat.Count} vez(es) ({string.Join("; ", cat.Recent.Take(5).Select(h => h.Detail))}). Isso bagunça datas e pode ser usado para disfarçar a idade do sistema. Também acontece com bateria da placa-mãe (CMOS) gasta.",
                    "Log System, Kernel-General 1");
        }

        static IEnumerable<Check> Batteries(Evidence ev)
        {
            if (ev.Batteries.Count == 0)
            {
                yield return ev.Errors.ContainsKey("battery")
                    ? Make("battery", "Bateria", Status.NotChecked, "Bateria", Why(ev, "battery"), @"root\wmi BatteryStaticData")
                    : Make("battery", "Bateria", Status.Info, "Bateria", "Nenhuma bateria detectada (desktop, ou bateria removida).", @"root\wmi BatteryStaticData");
                yield break;
            }
            int i = 0;
            foreach (var b in ev.Batteries)
            {
                var id = "battery." + i++;
                var title = "Bateria " + (b.Name ?? "") + (b.Manufacturer != null ? " (" + b.Manufacturer + ")" : "");
                if (b.DesignCapacity_mWh == 0 || b.FullChargeCapacity_mWh == 0)
                {
                    yield return Make(id, "Bateria", Status.NotChecked, title, "O firmware não informa a capacidade.", "BatteryStaticData", "BatteryFullChargedCapacity");
                    continue;
                }
                var pct = (int)Math.Round(100.0 * b.FullChargeCapacity_mWh / b.DesignCapacity_mWh);
                Status st = pct < BatteryCriticalPct ? Status.Critical : pct < BatteryWarningPct ? Status.Warning : Status.Ok;
                if (st == Status.Ok && b.CycleCount >= BatteryCyclesWarning) st = Status.Warning;
                yield return Make(id, "Bateria", st, title,
                    $"Segura {pct}% da carga original ({N(b.FullChargeCapacity_mWh)} de {N(b.DesignCapacity_mWh)} mWh)"
                    + (b.CycleCount != null ? $"; {b.CycleCount} ciclos de carga." : "; o firmware não informa os ciclos."),
                    "BatteryStaticData.DesignedCapacity", "BatteryFullChargedCapacity", "BatteryCycleCount");
            }
        }

        static Check RemovedParts(Evidence ev)
        {
            const string title = "Peças que já passaram por esta máquina";
            if (ev.Errors.ContainsKey("devices"))
                return Make("parts.removed", "Peças", Status.NotChecked, title, Why(ev, "devices"), "SetupAPI");
            var gone = new List<string>();
            foreach (var cls in new[] { ("Processor", "Processador"), ("Display", "Placa de vídeo"), ("DiskDrive", "Disco") })
            {
                var present = new HashSet<string>(ev.Devices.Where(d => d.Class == cls.Item1 && d.Present).Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var g in ev.Devices.Where(d => d.Class == cls.Item1 && !d.Present && d.Name != null && !present.Contains(d.Name)
                                                        && d.Name.IndexOf("Microsoft Basic", StringComparison.OrdinalIgnoreCase) < 0)
                                            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var first = g.Min(x => x.FirstInstall); var last = g.Max(x => x.LastRemoval ?? x.LastArrival);
                    gone.Add($"{cls.Item2}: {g.Key} (instalado em {D(first)}, visto pela última vez em {D(last)})");
                }
            }
            var since = OriginalInstall(ev.Machine);
            var scope = since != null ? $" desde a instalação do Windows ({D(since)})" : "";
            return gone.Count == 0
                ? Make("parts.removed", "Peças", Status.Ok, title, $"Nenhum processador, placa de vídeo ou disco removido{scope}.", "SetupAPI: dispositivos presentes e ausentes")
                : Make("parts.removed", "Peças", Status.Info, title,
                    $"Estiveram nesta máquina e não estão mais{scope}: " + string.Join("; ", gone) + ". Pode ser upgrade, troca por defeito ou disco externo.",
                    "SetupAPI: DEVPKEY_Device_FirstInstallDate / LastRemovalDate");
        }

        static Check Ram(Evidence ev)
        {
            var ram = ev.Machine.Ram;
            if (ram.Count == 0) return Make("hardware.ram", "Hardware", Status.NotChecked, "Memória RAM", Why(ev, "machine.hardware"), "Win32_PhysicalMemory");
            var total = ram.Aggregate(0UL, (s, r) => s + r.CapacityBytes);
            var desc = string.Join("; ", ram.Select(r => $"{r.Slot}: {RamSize(r.CapacityBytes)} {r.Manufacturer} {r.PartNumber} {r.SpeedMHz} MHz".Trim()));
            bool mixed = ram.Select(r => (r.PartNumber ?? "") + "|" + r.SpeedMHz + "|" + r.CapacityBytes).Distinct().Count() > 1;
            return Make("hardware.ram", "Hardware", mixed ? Status.Info : Status.Ok, "Memória RAM",
                $"{RamSize(total)} em {P(ram.Count, "pente", "pentes")}{(mixed ? ", modelos diferentes misturados (costuma indicar upgrade; pode causar instabilidade)" : "")}. {desc}.",
                "Win32_PhysicalMemory");
        }

        static Check Collection(Evidence ev)
        {
            const string title = "Cobertura da vistoria";
            var problems = ev.Errors.Select(e => $"{e.Key}: {e.Value}").ToList();
            if (!ev.Run.IsAdmin) problems.Insert(0, "rodou sem administrador: SMART, log Security e dumps exigem admin");
            return problems.Count == 0
                ? Make("run.coverage", "Coleta", Status.Ok, title, "Todas as fontes foram lidas.", "Vistoria")
                : Make("run.coverage", "Coleta", Status.NotChecked, title, "Fontes não lidas: " + string.Join("; ", problems) + ".", "Vistoria");
        }

        // --- helpers ---

        public static DateTime? OriginalInstall(MachineInfo m) =>
            m.PreviousInstalls.Select(p => p.InstallDate).Append(m.OsInstallDate).Where(x => x != null).Min();

        public static ulong? PowerOnHours(DiskInfo d) =>
            d.Nvme?.PowerOnHours ?? (Attr(d, 9) is ulong h ? h & 0xFFFFFFFF : (ulong?)null) ?? d.PowerOnHours;

        // Percent of rated life used: NVMe log, else Windows' counter, else the SSD's own SMART life attribute.
        public static uint? WearUsed(DiskInfo d) =>
            d.Nvme != null ? d.Nvme.PercentageUsed : d.WearPercent != null && d.PowerOnHours != null ? d.WearPercent : SmartWear(d);

        // 231 SSD Life Left, 233 Media Wearout, 177 Wear Leveling: normalized value counts down from 100 on SSDs.
        static uint? SmartWear(DiskInfo d) => SmartWearAttr(d) is SmartAttribute a ? 100u - a.Current : (uint?)null;
        static byte? SmartWearId(DiskInfo d) => SmartWearAttr(d)?.Id;
        static SmartAttribute SmartWearAttr(DiskInfo d) => d.MediaType != "SSD" ? null
            : new byte[] { 231, 233, 177 }.Select(id => d.Smart.FirstOrDefault(a => a.Id == id)).FirstOrDefault(a => a != null && a.Current >= 1 && a.Current <= 100);

        public static ulong? PowerCycles(DiskInfo d) => d.Nvme?.PowerCycles ?? Attr(d, 12);
        public static ulong? UnsafeShutdowns(DiskInfo d) => d.Nvme?.UnsafeShutdowns ?? Attr(d, 174) ?? Attr(d, 192);

        static ulong? Attr(DiskInfo d, byte id) => d.Smart.FirstOrDefault(a => a.Id == id)?.Raw;

        // disk 7 names the disk as "\Device\HarddiskN\DRn"; N is PhysicalDriveN at the time of the event.
        static bool IsInternalDisk(Evidence ev, string detail)
        {
            var m = System.Text.RegularExpressions.Regex.Match(detail ?? "", @"Harddisk(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return true; // unknown disk: assume the worst for the buyer
            var d = ev.Disks.FirstOrDefault(x => x.DeviceId == m.Groups[1].Value);
            return d != null && d.BusType != "USB" && d.BusType != "SD";
        }

        static EventCategory Cat(Evidence ev, string key) => ev.Events.Categories.FirstOrDefault(x => x.Key == key);

        static string Why(Evidence ev, params string[] keys)
        {
            var reason = keys.Select(k => ev.Errors.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => v != null);
            return reason != null ? "Motivo: " + reason + "." : "Fonte não disponível nesta máquina.";
        }

        static string NvmeWarningText(byte w)
        {
            var parts = new List<string>();
            if ((w & 1) != 0) parts.Add("reserva abaixo do limite");
            if ((w & 2) != 0) parts.Add("temperatura fora da faixa");
            if ((w & 4) != 0) parts.Add("confiabilidade degradada");
            if ((w & 8) != 0) parts.Add("disco em modo somente leitura");
            if ((w & 16) != 0) parts.Add("falha no backup de memória volátil");
            return parts.Count > 0 ? string.Join(", ", parts) : $"0x{w:X2}";
        }

        static Status Max(Status a, Status b) => Rank(a) >= Rank(b) ? a : b;
        public static int Rank(Status s) => s == Status.Critical ? 4 : s == Status.Warning ? 3 : s == Status.NotChecked ? 2 : s == Status.Info ? 1 : 0;

        static Check Make(string id, string area, Status st, string title, string detail, params string[] sources) =>
            new Check { Id = id, Area = area, Status = st, Title = title, Detail = detail, Sources = sources.ToList() };

        public static string D(DateTime? d) => d?.ToLocalTime().ToString("dd/MM/yyyy", Br) ?? "?";
        public static string DT(DateTime? d) => d?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Br) ?? "?";
        public static string N(double? v) => v?.ToString("N0", Br) ?? "?";
        public static string Size(ulong b) => b >= 1e12 ? (b / 1e12).ToString("0.#", Br) + " TB" : (b / 1e9).ToString("0", Br) + " GB";
        static string TB(ulong b) => Size(b);
        // RAM is sold in binary units: a "16 GB" module is 16 GiB.
        public static string RamSize(ulong b) => (b / 1073741824.0).ToString("0.#", Br) + " GB";
        public static string P(long n, string one, string many) => n.ToString("N0", Br) + " " + (n == 1 ? one : many);
    }
}
