using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Vistoria
{
    // Self-contained HTML: no network, no external assets, prints cleanly to PDF for the customer.
    // The only script is the print button; everything else (collapsibles, navigation) is plain HTML.
    public static class Report
    {
        static readonly string[] AreaOrder = { "Disco", "Estabilidade", "Hardware", "Bateria", "Adulteração", "Histórico", "Peças", "Coleta" };

        public static string Render(Evidence ev, string evidenceSha256)
        {
            var (label, level, complete) = Rules.Verdict(ev.Checks);
            var m = ev.Machine;
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\">")
              .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
              .Append("<title>Laudo de vistoria · ").Append(H(Join(" ", m.Manufacturer, m.Model) is var t && t.Length > 0 ? t : ev.Run.ComputerName)).Append("</title>")
              .Append("<style>").Append(Css).Append("</style></head><body>");

            // App bar
            sb.Append("<header class=\"appbar\"><div class=\"brand\">").Append(Logo).Append("<span class=\"brand-name\">Vistoria</span>")
              .Append("<span class=\"brand-doc\">Laudo de vistoria</span></div>")
              .Append("<button class=\"btn\" type=\"button\" aria-label=\"Imprimir ou salvar PDF\" onclick=\"window.print()\">").Append(PrintIcon).Append("Imprimir ou salvar PDF</button></header>");

            var sections = new List<(string Id, string Name)> { ("resumo", "Resumo"), ("verificacoes", "Verificações"), ("maquina", "Máquina") };
            if (ev.Disks.Count > 0) sections.Add(("discos", "Discos"));
            if (ev.Events.Categories.Count > 0) sections.Add(("eventos", "Eventos"));
            if (ev.Devices.Count > 0) sections.Add(("pecas", "Peças"));
            if (ev.Dumps.Count > 0) sections.Add(("dumps", "Dumps"));
            sections.Add(("integridade", "Integridade"));

            sb.Append("<div class=\"layout\"><nav class=\"toc\" aria-label=\"Seções do laudo\"><ul>");
            foreach (var s in sections) sb.Append("<li><a href=\"#").Append(s.Id).Append("\">").Append(H(s.Name)).Append("</a></li>");
            sb.Append("</ul></nav><main>");

            // Summary
            var counts = ev.Checks.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count());
            int Count(Status s) => counts.TryGetValue(s, out var n) ? n : 0;
            sb.Append("<section id=\"resumo\" class=\"card hero v-").Append(Cls(level)).Append("\">")
              .Append("<div class=\"hero-head\"><span class=\"hero-icon s-").Append(Cls(level)).Append("\">").Append(Icon(level)).Append("</span><div>")
              .Append("<h1>").Append(H(label)).Append("</h1><p class=\"hero-sub\">")
              .Append(H(Join(" · ", Join(" ", m.Manufacturer, m.Model), Serial(m) is string sn ? "série " + sn : null, Rules.DT(ev.Run.StartedUtc))))
              .Append("</p></div></div><ul class=\"chips\">");
            foreach (var st in new[] { Status.Critical, Status.Warning, Status.Ok, Status.Info, Status.NotChecked })
                if (Count(st) > 0)
                    sb.Append("<li class=\"chip s-").Append(Cls(st)).Append("\">").Append(Icon(st)).Append(Count(st)).Append(' ').Append(H(ChipText(st, Count(st)))).Append("</li>");
            sb.Append("</ul>");
            if (!complete)
                sb.Append("<p class=\"banner\">").Append(Icon(Status.NotChecked)).Append("<span><strong>Vistoria incompleta.</strong> Itens de disco ou estabilidade não puderam ser lidos, então o veredito pode estar otimista. ")
                  .Append(ev.Run.IsAdmin ? "Veja “Cobertura da vistoria”." : "Rode o Vistoria como administrador para a leitura completa.").Append("</span></p>");
            sb.Append("<dl class=\"facts\">");
            Fact(sb, "Loja", ev.Run.Shop);
            Fact(sb, "Técnico", ev.Run.Technician);
            Fact(sb, "Sistema", Join(" · ", m.OsName, m.OsVersion));
            Fact(sb, "Windows desde", Rules.OriginalInstall(m) is DateTime inst ? Rules.D(inst) : null);
            sb.Append("</dl></section>");

            // Checks
            sb.Append("<section id=\"verificacoes\"><h2>Verificações</h2>");
            foreach (var area in AreaOrder.Where(a => ev.Checks.Any(c => c.Area == a)))
            {
                var items = ev.Checks.Where(c => c.Area == area).OrderByDescending(c => Rules.Rank(c.Status)).ToList();
                sb.Append("<div class=\"card group\"><h3>").Append(H(area)).Append("</h3><ul class=\"checks\">");
                foreach (var c in items)
                {
                    sb.Append("<li><span class=\"row-icon s-").Append(Cls(c.Status)).Append("\" title=\"").Append(StatusText(c.Status)).Append("\">").Append(Icon(c.Status)).Append("</span><div class=\"row-body\">")
                      .Append("<p class=\"row-title\">").Append(H(c.Title)).Append("<span class=\"tag s-").Append(Cls(c.Status)).Append("\">").Append(StatusText(c.Status)).Append("</span></p>")
                      .Append("<p class=\"row-detail\">").Append(H(c.Detail)).Append("</p>");
                    if (c.Sources.Count > 0) sb.Append("<p class=\"row-source\">Fonte: ").Append(H(string.Join(" · ", c.Sources))).Append("</p>");
                    sb.Append("</div></li>");
                }
                sb.Append("</ul></div>");
            }
            sb.Append("</section>");

            // Machine identity
            sb.Append("<section id=\"maquina\"><h2>Identificação da máquina</h2><dl class=\"card grid\">");
            Def(sb, "Fabricante e modelo", Join(" ", m.Manufacturer, m.Model));
            Def(sb, "Número de série", Serial(m), mono: true);
            Def(sb, "Placa-mãe", Join(" ", m.BoardManufacturer, m.BoardProduct));
            Def(sb, "Série da placa-mãe", m.BoardSerial, mono: true);
            Def(sb, "BIOS", Join(" · ", m.BiosVendor, m.BiosVersion, m.BiosDate != null ? "de " + Rules.D(m.BiosDate) : null));
            Def(sb, "Processador", m.Cpu != null ? $"{m.Cpu} · {m.CpuCores} núcleos, {m.CpuThreads} threads" : null);
            Def(sb, "Memória", m.Ram.Count > 0 ? Rules.RamSize(m.Ram.Aggregate(0UL, (s, r) => s + r.CapacityBytes)) + " em " + Rules.P(m.Ram.Count, "pente", "pentes") : null);
            Def(sb, "Vídeo", string.Join(" + ", m.Gpus.Where(g => g != null)));
            Def(sb, "Sistema", Join(" · ", m.OsName, m.OsVersion));
            Def(sb, "Último boot", m.LastBoot != null ? Rules.DT(m.LastBoot) : null);
            sb.Append("</dl></section>");

            // Disks
            if (ev.Disks.Count > 0)
            {
                sb.Append("<section id=\"discos\"><h2>Discos</h2><div class=\"card scroll\"><table><thead><tr><th>Disco</th><th>Série</th><th class=\"n\">Tamanho</th><th class=\"n\">Horas ligado</th><th class=\"n\">Ligado</th><th class=\"n\">Desl. sem aviso</th><th class=\"n\">Vida usada</th><th>Windows diz</th></tr></thead><tbody>");
                foreach (var d in ev.Disks)
                {
                    var cycles = Rules.PowerCycles(d);
                    var unsafeN = Rules.UnsafeShutdowns(d);
                    var used = Rules.WearUsed(d);
                    sb.Append("<tr><td><strong>").Append(H(d.Model)).Append("</strong><br><small>").Append(H(Join(" · ", "#" + d.DeviceId, d.BusType, d.MediaType, d.IsSystem ? "sistema" : null)))
                      .Append("</small></td><td class=\"mono\">").Append(H(d.Serial ?? "—")).Append("</td><td class=\"n\">").Append(Rules.Size(d.SizeBytes))
                      .Append("</td><td class=\"n\">").Append(Rules.N(Rules.PowerOnHours(d))).Append("</td><td class=\"n\">").Append(Rules.N(cycles))
                      .Append("</td><td class=\"n\">").Append(Rules.N(unsafeN)).Append("</td><td class=\"n\">").Append(used != null ? used + "%" : "?")
                      .Append("</td><td>").Append(H(d.Health)).Append("</td></tr>");
                }
                sb.Append("</tbody></table></div>");
                foreach (var d in ev.Disks.Where(x => x.Smart.Count > 0))
                {
                    sb.Append("<details class=\"card\"><summary>Atributos SMART · ").Append(H(d.Model)).Append("</summary><div class=\"scroll\"><table><thead><tr><th class=\"n\">ID</th><th>Atributo</th><th class=\"n\">Atual</th><th class=\"n\">Pior</th><th class=\"n\">Bruto</th></tr></thead><tbody>");
                    foreach (var a in d.Smart)
                        sb.Append("<tr><td class=\"n\">").Append(a.Id).Append("</td><td>").Append(H(a.Name ?? "—")).Append("</td><td class=\"n\">").Append(a.Current)
                          .Append("</td><td class=\"n\">").Append(a.Worst).Append("</td><td class=\"n mono\">").Append(a.Raw).Append("</td></tr>");
                    sb.Append("</tbody></table></div></details>");
                }
                foreach (var d in ev.Disks.Where(x => x.Nvme != null))
                {
                    var n = d.Nvme;
                    sb.Append("<details class=\"card\"><summary>Log de saúde NVMe · ").Append(H(d.Model)).Append("</summary><dl class=\"grid\">");
                    Def(sb, "Alerta crítico", n.CriticalWarning == 0 ? "nenhum" : $"0x{n.CriticalWarning:X2}");
                    Def(sb, "Temperatura", n.TemperatureC > 0 ? n.TemperatureC + " °C" : null); // raw 0 K = sensor not reported
                    Def(sb, "Reserva disponível", $"{n.AvailableSpare}% (limite {n.AvailableSpareThreshold}%)");
                    Def(sb, "Vida útil consumida", n.PercentageUsed + "%");
                    Def(sb, "Dados gravados", Rules.Size(n.DataUnitsWritten * 512000UL));
                    Def(sb, "Dados lidos", Rules.Size(n.DataUnitsRead * 512000UL));
                    Def(sb, "Horas ligado", Rules.N(n.PowerOnHours));
                    Def(sb, "Vezes ligado", Rules.N(n.PowerCycles));
                    Def(sb, "Desligamentos sem aviso", Rules.N(n.UnsafeShutdowns));
                    Def(sb, "Erros de mídia", Rules.N(n.MediaErrors));
                    Def(sb, "Entradas no log de erros", Rules.N(n.ErrorLogEntries));
                    sb.Append("</dl></details>");
                }
                sb.Append("</section>");
            }

            // Event history, each category expandable to its occurrences
            if (ev.Events.Categories.Count > 0)
            {
                sb.Append("<section id=\"eventos\"><h2>Histórico de eventos</h2>");
                foreach (var l in ev.Events.Logs)
                    sb.Append("<p class=\"note\">Log ").Append(H(l.Log)).Append(": ").Append(Rules.N(l.Records)).Append(" eventos, de ")
                      .Append(Rules.D(l.Oldest)).Append(" a ").Append(Rules.D(l.Newest)).Append(".</p>");
                sb.Append("<div class=\"card list\">");
                foreach (var c in ev.Events.Categories)
                {
                    var head = new StringBuilder().Append("<span class=\"ev-name\">").Append(H(c.Name)).Append("</span><span class=\"ev-meta\">")
                        .Append(c.Last != null ? "último " + Rules.DT(c.Last) : "").Append("</span><span class=\"count").Append(c.Count > 0 ? "" : " zero").Append("\">").Append(Rules.N(c.Count)).Append("</span>");
                    if (c.Recent.Count == 0) { sb.Append("<div class=\"ev-row\">").Append(head).Append("</div>"); continue; }
                    sb.Append("<details class=\"ev\"><summary class=\"ev-row\">").Append(head).Append("</summary><div class=\"scroll\"><table><thead><tr><th>Quando</th><th>Log</th><th>Origem</th><th class=\"n\">ID</th><th>Detalhe</th></tr></thead><tbody>");
                    foreach (var h in c.Recent)
                        sb.Append("<tr><td class=\"nowrap\">").Append(Rules.DT(h.TimeUtc)).Append("</td><td>").Append(H(h.Log)).Append("</td><td>").Append(H(h.Provider))
                          .Append("</td><td class=\"n\">").Append(h.Id).Append("</td><td>").Append(H(h.Detail ?? "—")).Append("</td></tr>");
                    sb.Append("</tbody></table></div>");
                    if (c.Count > c.Recent.Count) sb.Append("<p class=\"note pad\">Mostrando as ").Append(c.Recent.Count).Append(" mais recentes de ").Append(Rules.N(c.Count)).Append(". Todas estão no log do Windows.</p>");
                    sb.Append("</details>");
                }
                sb.Append("</div></section>");
            }

            // Parts history (one row per distinct device name per class)
            if (ev.Devices.Count > 0)
            {
                sb.Append("<section id=\"pecas\"><h2>Histórico de peças</h2><p class=\"note\">Processadores, placas de vídeo e discos que o Windows já instalou nesta máquina. Peças ausentes podem ter sido trocadas, removidas ou desativadas na BIOS.</p>")
                  .Append("<div class=\"card scroll\"><table><thead><tr><th>Tipo</th><th>Peça</th><th>Situação</th><th>Primeira vez</th><th>Última remoção</th></tr></thead><tbody>");
                var rows = ev.Devices.Where(d => d.Name != null)
                    .GroupBy(d => (d.Class, d.Name.ToUpperInvariant()))
                    .Select(g => new { g.First().Class, g.First().Name, Present = g.Any(x => x.Present), First = g.Min(x => x.FirstInstall), Removed = g.Max(x => x.LastRemoval) })
                    .OrderBy(r => r.Present).ThenBy(r => r.Class).ThenBy(r => r.First);
                foreach (var r in rows)
                    sb.Append("<tr><td>").Append(H(ClassName(r.Class))).Append("</td><td>").Append(H(r.Name)).Append("</td><td><span class=\"tag s-").Append(r.Present ? "ok" : "info").Append("\">")
                      .Append(r.Present ? "Presente" : "Ausente").Append("</span></td><td class=\"nowrap\">").Append(Rules.D(r.First)).Append("</td><td class=\"nowrap\">")
                      .Append(r.Removed != null ? Rules.D(r.Removed) : "—").Append("</td></tr>");
                sb.Append("</tbody></table></div></section>");
            }

            // Crash dumps
            if (ev.Dumps.Count > 0)
            {
                sb.Append("<section id=\"dumps\"><h2>Arquivos de despejo (dumps)</h2><div class=\"card scroll\"><table><thead><tr><th>Data</th><th>Código</th><th>Arquivo</th><th class=\"n\">Tamanho</th></tr></thead><tbody>");
                foreach (var d in ev.Dumps.OrderByDescending(x => x.WrittenUtc))
                    sb.Append("<tr><td class=\"nowrap\">").Append(Rules.DT(d.WrittenUtc)).Append("</td><td class=\"mono\">").Append(H(d.BugCheckName ?? "—"))
                      .Append("</td><td class=\"mono\">").Append(H(d.Path)).Append("</td><td class=\"n\">").Append(Rules.N(d.SizeBytes / 1024.0)).Append(" KB</td></tr>");
                sb.Append("</tbody></table></div></section>");
            }

            // Integrity
            sb.Append("<section id=\"integridade\"><h2>Integridade e custódia</h2><dl class=\"card grid\">");
            Def(sb, "Ferramenta", $"{ev.Run.Tool} {ev.Run.Version}");
            Def(sb, "Início (UTC)", ev.Run.StartedUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            Def(sb, "Fim (UTC)", ev.Run.FinishedUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            Def(sb, "Executado por", $"{ev.Run.OperatorUser} em {ev.Run.ComputerName}" + (ev.Run.IsAdmin ? " (administrador)" : " (sem administrador)"));
            sb.Append("<div class=\"wide\"><dt>SHA-256 de evidencias.json</dt><dd class=\"mono hash\">").Append(H(evidenceSha256)).Append("</dd></div>");
            sb.Append("</dl><p class=\"note\">O arquivo evidencias.json, que fica na mesma pasta deste laudo, traz tudo o que foi lido, em bruto. Se o hash não bater com o arquivo, ele foi alterado depois da vistoria. Para conferir no PowerShell: <code>Get-FileHash evidencias.json</code>. Nada foi gravado na máquina examinada.</p></section>");

            sb.Append("<footer>Este laudo descreve o que o hardware e o Windows registraram até a data da vistoria. Ausência de registro não prova ausência de defeito: o histórico só cobre o período indicado em “Período coberto pelo histórico”. Não substitui teste de estresse.</footer>");
            sb.Append("</main></div></body></html>");
            return sb.ToString();
        }

        static string Serial(MachineInfo m) => m.SystemSerial ?? m.BiosSerial ?? m.BoardSerial;

        static void Fact(StringBuilder sb, string k, string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            sb.Append("<div><dt>").Append(H(k)).Append("</dt><dd>").Append(H(v)).Append("</dd></div>");
        }

        static void Def(StringBuilder sb, string k, string v, bool mono = false) =>
            sb.Append("<div><dt>").Append(H(k)).Append("</dt><dd").Append(mono ? " class=\"mono\"" : "").Append('>')
              .Append(string.IsNullOrWhiteSpace(v) ? "—" : H(v)).Append("</dd></div>");

        static string Join(string sep, params string[] parts) => string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        static string H(string s) => WebUtility.HtmlEncode(s ?? "");

        static string ClassName(string c) => c == "Processor" ? "Processador" : c == "Display" ? "Vídeo" : c == "DiskDrive" ? "Disco" : c;

        static string StatusText(Status s) =>
            s == Status.Critical ? "Crítico" : s == Status.Warning ? "Atenção" : s == Status.Ok ? "OK" : s == Status.Info ? "Info" : "Não verificado";

        static string ChipText(Status s, int n) =>
            s == Status.Critical ? (n == 1 ? "crítico" : "críticos") : s == Status.Warning ? (n == 1 ? "apontamento" : "apontamentos")
            : s == Status.Ok ? "sem problema" : s == Status.Info ? (n == 1 ? "informativo" : "informativos") : (n == 1 ? "não verificado" : "não verificados");

        static string Cls(Status s) =>
            s == Status.Critical ? "crit" : s == Status.Warning ? "warn" : s == Status.Ok ? "ok" : s == Status.Info ? "info" : "nc";

        // Authored 24px icons, one stroke weight, currentColor.
        static string Icon(Status s)
        {
            const string open = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">";
            switch (s)
            {
                case Status.Critical: return open + "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 7.5v5.5M12 16.5v.01\"/></svg>";
                case Status.Warning: return open + "<path d=\"M10.3 4.2 2.8 17.2A2 2 0 0 0 4.5 20h15a2 2 0 0 0 1.7-2.8L13.7 4.2a2 2 0 0 0-3.4 0Z\"/><path d=\"M12 9.5v4M12 17v.01\"/></svg>";
                case Status.Ok: return open + "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"m8 12.3 2.7 2.7L16 9.7\"/></svg>";
                case Status.Info: return open + "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 11v5.5M12 7.5v.01\"/></svg>";
                default: return open + "<circle cx=\"12\" cy=\"12\" r=\"9\" stroke-dasharray=\"3 3\"/><path d=\"M8.5 12h7\"/></svg>";
            }
        }

        const string Logo = "<svg class=\"logo\" viewBox=\"0 0 28 28\" aria-hidden=\"true\"><rect width=\"28\" height=\"28\" rx=\"8\" fill=\"#1a73e8\"/><path d=\"m8 13.8 4.2 4.2L20.5 9.5\" fill=\"none\" stroke=\"#fff\" stroke-width=\"2.6\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>";

        const string PrintIcon = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><path d=\"M7 9V3h10v6\"/><rect x=\"3\" y=\"9\" width=\"18\" height=\"8\" rx=\"2\"/><path d=\"M7 14h10v7H7z\"/></svg>";

        const string Css = @"
:root{--ink:#202124;--muted:#5f6368;--line:#dadce0;--surface:#fff;--bg:#f8f9fa;--accent:#1a73e8;--crit:#c5221f;--warn:#b06000;--ok:#137333;
--crit-bg:#fce8e6;--warn-bg:#fef7e0;--ok-bg:#e6f4ea;--info-bg:#e8f0fe;--nc-bg:#f1f3f4;--shadow:0 1px 2px rgba(60,64,67,.3),0 1px 3px 1px rgba(60,64,67,.15)}
*{box-sizing:border-box}
html{background:var(--bg);color:var(--ink);font:14px/1.55 'Segoe UI Variable Text','Segoe UI',Roboto,system-ui,sans-serif;-webkit-text-size-adjust:100%;scroll-behavior:smooth;scroll-padding-top:80px;scrollbar-color:#bdc1c6 transparent;accent-color:var(--accent)}
body{margin:0}
::selection{background:#c2dbff}
a{color:var(--accent);text-decoration:none}
a:hover{text-decoration:underline;text-underline-offset:3px}
:focus-visible{outline:2px solid var(--accent);outline-offset:2px;border-radius:6px}
svg{width:20px;height:20px;flex:none}
p{margin:0}
code{font-family:'Cascadia Mono',Consolas,monospace;font-size:.92em;background:var(--nc-bg);padding:1px 6px;border-radius:4px}

.appbar{position:sticky;top:0;z-index:2;display:flex;align-items:center;justify-content:space-between;gap:16px;height:64px;padding:0 24px;background:var(--surface);border-bottom:1px solid var(--line)}
.brand{display:flex;align-items:center;gap:12px;min-width:0}
.logo{width:28px;height:28px}
.brand-name{font-size:1.375rem;font-weight:600;letter-spacing:-0.01em}
.brand-doc{color:var(--muted);font-size:1rem;padding-left:12px;border-left:1px solid var(--line);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.btn{display:inline-flex;align-items:center;gap:8px;height:36px;padding:0 16px 0 12px;border:1px solid var(--line);border-radius:18px;background:var(--surface);color:var(--accent);font:inherit;font-weight:600;cursor:pointer;transition:background .15s ease-out}
.btn:hover{background:var(--info-bg)}
.btn svg{width:18px;height:18px}

.layout{display:grid;grid-template-columns:200px minmax(0,1fr);gap:32px;max-width:1240px;margin:0 auto;padding:24px}
.toc ul{position:sticky;top:88px;list-style:none;margin:0;padding:0}
.toc a{display:block;padding:8px 16px;border-radius:0 18px 18px 0;color:var(--ink);font-weight:500}
.toc a:hover{background:#e8eaed;text-decoration:none}
main{min-width:0;max-width:960px}

h1{font-size:1.75rem;line-height:1.2;letter-spacing:-0.015em;margin:0;font-weight:600}
h2{font-size:1.25rem;font-weight:600;margin:40px 0 12px;letter-spacing:-0.005em}
h3{font-size:.9375rem;font-weight:600;margin:0;padding:14px 20px;border-bottom:1px solid var(--line)}
.card{background:var(--surface);border-radius:12px;box-shadow:var(--shadow)}
.note{color:var(--muted);margin:0 0 12px}
.note.pad{margin:0;padding:10px 20px 14px}
small{color:var(--muted);font-size:.8125rem}

.hero{padding:24px}
.hero-head{display:flex;gap:16px;align-items:flex-start}
.hero-icon{display:grid;place-items:center;width:48px;height:48px;border-radius:50%}
.hero-icon svg{width:28px;height:28px}
.v-crit h1{color:var(--crit)} .v-warn h1{color:var(--warn)} .v-ok h1{color:var(--ok)}
.hero-sub{color:var(--muted);margin-top:4px}
.chips{display:flex;flex-wrap:wrap;gap:8px;list-style:none;margin:20px 0 0;padding:0}
.chip{display:inline-flex;align-items:center;gap:6px;height:32px;padding:0 12px 0 8px;border-radius:8px;font-weight:600;font-variant-numeric:tabular-nums}
.chip svg{width:18px;height:18px}
.banner{display:flex;gap:10px;align-items:flex-start;margin-top:16px;padding:12px 16px;border-radius:8px;background:var(--nc-bg)}
.facts{display:flex;flex-wrap:wrap;gap:12px 40px;margin:20px 0 0;padding-top:16px;border-top:1px solid var(--line)}
.facts dt{color:var(--muted);font-size:.8125rem}
.facts dd{margin:0;font-weight:600}

.s-crit{color:var(--crit);background:var(--crit-bg)} .s-warn{color:var(--warn);background:var(--warn-bg)} .s-ok{color:var(--ok);background:var(--ok-bg)}
.s-info{color:#174ea6;background:var(--info-bg)} .s-nc{color:var(--muted);background:var(--nc-bg)}

.group{margin-bottom:16px;overflow:hidden}
.checks{list-style:none;margin:0;padding:0}
.checks li{display:flex;gap:16px;padding:16px 20px;border-top:1px solid var(--line);break-inside:avoid}
.checks li:first-child{border-top:0}
.row-icon{display:grid;place-items:center;width:36px;height:36px;border-radius:50%;flex:none}
.row-body{min-width:0;flex:1}
.row-title{font-weight:600;display:flex;flex-wrap:wrap;align-items:center;gap:4px 10px}
.row-detail{margin-top:4px;max-width:75ch}
.row-source{margin-top:6px;font-size:.8125rem;color:var(--muted);overflow-wrap:anywhere}
.tag{display:inline-block;font-size:.75rem;font-weight:600;line-height:20px;padding:0 8px;border-radius:10px}

.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:16px 32px;margin:0;padding:20px}
.grid dt{font-size:.8125rem;color:var(--muted)}
.grid dd{margin:2px 0 0;overflow-wrap:anywhere}
.grid .wide{grid-column:1/-1}
.scroll{overflow-x:auto}
table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}
th,td{text-align:left;padding:10px 16px;border-top:1px solid var(--line);vertical-align:top}
thead th{border-top:0;color:var(--muted);font-weight:600;font-size:.8125rem;white-space:nowrap}
.n{text-align:right;white-space:nowrap}
.nowrap{white-space:nowrap}
.mono{font-family:'Cascadia Mono',Consolas,monospace;font-size:.8125rem}
td.mono{overflow-wrap:anywhere;min-width:14ch;max-width:30ch}
.hash{overflow-wrap:anywhere}

details.card{margin-top:12px}
details.card>summary{padding:14px 20px;font-weight:600;cursor:pointer;color:var(--ink)}
details[open].card>summary{border-bottom:1px solid var(--line)}
summary::marker{color:var(--muted)}
.list{overflow:hidden}
.ev-row{display:flex;align-items:center;gap:16px;padding:12px 20px;border-top:1px solid var(--line)}
.list>:first-child .ev-row,.list>.ev-row:first-child{border-top:0}
summary.ev-row{cursor:pointer;list-style:none}
summary.ev-row::-webkit-details-marker{display:none}
summary.ev-row::before{content:'';width:8px;height:8px;border-right:2px solid var(--muted);border-bottom:2px solid var(--muted);transform:rotate(-45deg);transition:transform .15s ease-out;flex:none;margin-right:-4px}
details[open]>summary.ev-row::before{transform:rotate(45deg)}
div.ev-row{padding-left:40px}
.ev-name{flex:1;min-width:0}
.ev-meta{color:var(--muted);font-size:.8125rem;white-space:nowrap}
.count{min-width:40px;text-align:center;font-weight:700;font-variant-numeric:tabular-nums;padding:2px 10px;border-radius:12px;background:var(--info-bg);color:#174ea6}
.count.zero{background:transparent;color:var(--muted);font-weight:500}
details.ev>.scroll{background:var(--bg);border-top:1px solid var(--line)}

footer{margin:48px 0 24px;padding-top:20px;border-top:1px solid var(--line);color:var(--muted);font-size:.8125rem;max-width:80ch}

@media (max-width:1000px){.layout{grid-template-columns:minmax(0,1fr);padding:16px}.toc{display:none}}
@media (max-width:600px){.appbar{padding:0 12px}.brand-doc{display:none}.btn{padding:0 10px}.btn{font-size:0;gap:0}.btn svg{width:20px;height:20px}
.hero{padding:20px}.checks li{gap:12px;padding:14px 16px}.row-icon{width:32px;height:32px}.ev-meta{display:none}h1{font-size:1.5rem}}
@media print{html{background:#fff;font-size:11px}.appbar .btn,.toc{display:none}.appbar{position:static;height:auto;padding:0 0 12px}
.layout{display:block;padding:0;max-width:none}.card{box-shadow:none;border:1px solid var(--line)}h2{break-after:avoid}tr,li,.card.hero{break-inside:avoid}}
@media (prefers-reduced-motion:reduce){html{scroll-behavior:auto}*{transition:none!important}}
";
    }
}
