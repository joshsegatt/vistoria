using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;

namespace Vistoria.Collectors
{
    static class Events
    {
        const int RecentCap = 25;

        class Category
        {
            public string Key, Name;
            public (string Log, string Predicate)[] Queries;
            public Func<EventRecord, bool> Keep = _ => true;
            public Func<EventRecord, string> Detail = Describe;
            public Func<EventRecord, string> Target; // which device the event names, tracked over every hit
        }

        static string Provs(params string[] names) => "(" + string.Join(" or ", names.Select(n => $"Provider[@Name='{n}']")) + ")";

        static readonly Category[] Categories =
        {
            new Category { Key = "bsod", Name = "Tela azul (BugCheck)",
                Queries = new[] { ("System", Provs("Microsoft-Windows-WER-SystemErrorReporting", "BugCheck") + " and EventID=1001") },
                Detail = e => Prop(e, 0) },
            new Category { Key = "unexpected_shutdown", Name = "Desligamento ou reinício inesperado (Kernel-Power 41)",
                Queries = new[] { ("System", Provs("Microsoft-Windows-Kernel-Power") + " and EventID=41") } },
            new Category { Key = "whea_error", Name = "Erro de hardware (WHEA, erro)",
                Queries = new[] { ("System", Provs("Microsoft-Windows-WHEA-Logger") + " and (Level=1 or Level=2)") } },
            new Category { Key = "whea_warning", Name = "Erro de hardware corrigido (WHEA, aviso)",
                Queries = new[] { ("System", Provs("Microsoft-Windows-WHEA-Logger") + " and Level=3") } },
            new Category { Key = "disk_bad_block", Name = "Bloco defeituoso no disco (disk 7)",
                Queries = new[] { ("System", Provs("disk") + " and EventID=7") },
                // "\Device\Harddisk1\DR1": tells an internal disk from a pendrive. Pendrives log these by the
                // hundred, so the decision uses every disk named, not only the newest events.
                Detail = e => Prop(e, 0), Target = e => Prop(e, 0) },
            new Category { Key = "ntfs_corruption", Name = "Corrupção no sistema de arquivos (NTFS 55)",
                Queries = new[] { ("System", Provs("Ntfs", "Microsoft-Windows-Ntfs") + " and EventID=55") } },
            new Category { Key = "disk_io", Name = "Falha ou repetição de E/S no disco (disk 51/153, reset 129)",
                Queries = new[] { ("System", $"({Provs("disk")} and (EventID=51 or EventID=153)) or ({Provs("stornvme", "storahci", "iaStorA", "iaStorAC", "iaStorAVC")} and EventID=129)") } },
            new Category { Key = "gpu_tdr", Name = "Driver de vídeo parou de responder (Display 4101)",
                Queries = new[] { ("System", Provs("Display") + " and EventID=4101") } },
            new Category { Key = "clock_backwards", Name = "Relógio voltado mais de 24 h (Kernel-General 1)",
                Queries = new[] { ("System", Provs("Microsoft-Windows-Kernel-General") + " and EventID=1") },
                // Small backward steps are normal (RTC sync, dual boot with Linux in UTC); a day or more is not.
                Keep = e => ClockJump(e) is TimeSpan j && j > TimeSpan.FromDays(1),
                Detail = e => $"de {Prop(e, 1)} para {Prop(e, 0)}" },
            new Category { Key = "log_cleared", Name = "Log de eventos apagado (System 104 / Security 1102)",
                Queries = new[] { ("System", Provs("Microsoft-Windows-Eventlog") + " and EventID=104"),
                                  ("Security", Provs("Microsoft-Windows-Eventlog") + " and EventID=1102") } },
            new Category { Key = "boots", Name = "Inicializações do Windows (Kernel-General 12)",
                Queries = new[] { ("System", Provs("Microsoft-Windows-Kernel-General") + " and EventID=12") },
                Detail = _ => "" },
        };

        public static void Collect(Evidence ev)
        {
            var recentCutoff = ev.Run.StartedUtc.AddDays(-Rules.RecentDays);
            foreach (var log in new[] { "System", "Security" })
                Guard.Try(ev, "events.coverage." + log, () => ev.Events.Logs.Add(Coverage(log)));

            foreach (var c in Categories)
            {
                var cat = new EventCategory { Key = c.Key, Name = c.Name };
                bool anyRead = false;
                foreach (var (log, pred) in c.Queries)
                    Guard.Try(ev, $"events.{c.Key}.{log}", () => { Read(c, log, pred, cat, recentCutoff); anyRead = true; });
                if (anyRead) ev.Events.Categories.Add(cat);
            }
        }

        static void Read(Category c, string log, string predicate, EventCategory cat, DateTime recentCutoff)
        {
            var q = new EventLogQuery(log, PathType.LogName, $"*[System[{predicate}]]") { ReverseDirection = true };
            using (var r = new EventLogReader(q))
                for (var e = r.ReadEvent(); e != null; e = r.ReadEvent())
                    using (e)
                    {
                        if (!c.Keep(e) || e.TimeCreated == null) continue;
                        var t = e.TimeCreated.Value.ToUniversalTime();
                        cat.Count++;
                        if (t > recentCutoff) cat.CountLast90Days++;
                        if (cat.First == null || t < cat.First) cat.First = t;
                        if (cat.Last == null || t > cat.Last) cat.Last = t;
                        if (c.Target?.Invoke(e) is string target && !cat.Targets.Contains(target)) cat.Targets.Add(target);
                        if (cat.Recent.Count < RecentCap * 2)
                            cat.Recent.Add(new EventHit { TimeUtc = t, Log = log, Provider = e.ProviderName, Id = e.Id, Detail = Trim(c.Detail(e)) });
                    }
            cat.Recent = cat.Recent.OrderByDescending(h => h.TimeUtc).Take(RecentCap).ToList();
        }

        static LogCoverage Coverage(string log)
        {
            var info = EventLogSession.GlobalSession.GetLogInformation(log, PathType.LogName);
            return new LogCoverage { Log = log, Records = info.RecordCount, Oldest = Edge(log, false), Newest = Edge(log, true) };
        }

        static DateTime? Edge(string log, bool newest)
        {
            using (var r = new EventLogReader(new EventLogQuery(log, PathType.LogName) { ReverseDirection = newest }))
            using (var e = r.ReadEvent())
                return e?.TimeCreated?.ToUniversalTime();
        }

        public static TimeSpan? ClockJump(EventRecord e) =>
            e.Properties.Count >= 2 && e.Properties[0].Value is DateTime now && e.Properties[1].Value is DateTime old
                ? old.ToUniversalTime() - now.ToUniversalTime() : (TimeSpan?)null;

        static string Prop(EventRecord e, int i) =>
            e.Properties.Count > i ? (e.Properties[i].Value is DateTime d ? d.ToUniversalTime().ToString("u") : e.Properties[i].Value?.ToString()) : null;

        static string Describe(EventRecord e)
        {
            try { return e.FormatDescription(); }
            catch (EventLogException) { return null; } // provider manifest missing on this machine
        }

        static string Trim(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }
    }
}
