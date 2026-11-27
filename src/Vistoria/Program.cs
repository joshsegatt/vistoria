using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Vistoria.Collectors;

namespace Vistoria
{
    static class Program
    {
        const string Usage = @"Vistoria — vistoria cautelar para PCs usados

Uso: Vistoria.exe [opções]

  --loja ""Nome""       nome da loja, impresso no laudo
  --tecnico ""Nome""    técnico responsável, impresso no laudo e no manifesto
  --saida <pasta>     onde gravar a vistoria (padrão: pasta 'vistorias' ao lado do exe)
  --sem-admin         não pede elevação (coleta parcial, itens que exigem admin saem como não verificados)
  --abrir             abre o laudo no navegador ao terminar (o navegador grava histórico
                      e cache na máquina examinada; por isso fica desligado por padrão)
  -h, --help          mostra esta ajuda

Nada é gravado na máquina examinada. A saída vai para a pasta indicada:
  relatorio.html  laudo para o cliente
  evidencias.json tudo o que foi lido, em bruto
  manifesto.txt   SHA-256 dos arquivos acima, para provar que não foram alterados";

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            int code;
            try { code = Run(args); }
            catch (Exception e)
            {
                Console.Error.WriteLine("Falha inesperada: " + e);
                code = 1;
            }
            // Double-click or UAC relaunch: the window closes with the process, so hold it open to be read.
            if (code != RelaunchedCode && !Console.IsInputRedirected && GetConsoleProcessList(new uint[2], 2) <= 1)
            {
                Console.WriteLine("Pressione Enter para fechar.");
                Console.ReadLine();
            }
            return code == RelaunchedCode ? 0 : code;
        }

        const int RelaunchedCode = -1;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern uint GetConsoleProcessList(uint[] list, uint count);

        static int Run(string[] args)
        {
            string Opt(string name)
            {
                var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            bool Flag(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (Flag("-h") || Flag("--help") || Flag("/?")) { Console.WriteLine(Usage); return 0; }

            // The elevated copy starts in System32: pin a relative --saida to where the user typed it.
            var outIdx = Array.FindIndex(args, a => a.Equals("--saida", StringComparison.OrdinalIgnoreCase));
            if (outIdx >= 0 && outIdx + 1 < args.Length) args[outIdx + 1] = Path.GetFullPath(args[outIdx + 1]);

            bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            if (!admin && !Flag("--sem-admin") && Relaunch(args)) return RelaunchedCode;

            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var ev = new Evidence();
            ev.Run.Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            ev.Run.StartedUtc = DateTime.UtcNow;
            ev.Run.ComputerName = Environment.MachineName;
            ev.Run.OperatorUser = Environment.UserDomainName + "\\" + Environment.UserName;
            ev.Run.IsAdmin = admin;
            ev.Run.Shop = Opt("--loja");
            ev.Run.Technician = Opt("--tecnico");

            Console.WriteLine($"Vistoria {ev.Run.Version} em {ev.Run.ComputerName}{(admin ? "" : " (sem administrador: coleta parcial)")}");
            Step("Identificação da máquina", () => Machine.Collect(ev));
            Step("Discos e SMART", () => Disks.Collect(ev));
            Step("Bateria", () => Battery.Collect(ev));
            Step("Logs de eventos", () => Events.Collect(ev));
            Step("Peças já instaladas", () => Devices.Collect(ev));
            Step("Telas azuis (dumps)", () => Dumps.Collect(ev));
            ev.Run.FinishedUtc = DateTime.UtcNow;
            ev.Checks = Rules.Evaluate(ev);

            var outDir = Path.Combine(Opt("--saida") ?? Path.Combine(exeDir, "vistorias"),
                $"{ev.Run.StartedUtc.ToLocalTime():yyyyMMdd-HHmmss}_{Safe(ev.Run.ComputerName)}");
            Directory.CreateDirectory(outDir);

            var utf8 = new UTF8Encoding(false);
            var jsonPath = Path.Combine(outDir, "evidencias.json");
            File.WriteAllText(jsonPath, Json.Write(ev), utf8);
            var jsonHash = Sha256(jsonPath);
            var htmlPath = Path.Combine(outDir, "relatorio.html");
            File.WriteAllText(htmlPath, Report.Render(ev, jsonHash), utf8);

            var manifest = new StringBuilder()
                .AppendLine($"Vistoria {ev.Run.Version}")
                .AppendLine($"Maquina: {ev.Run.ComputerName}")
                .AppendLine($"Serie: {ev.Machine.SystemSerial ?? ev.Machine.BiosSerial ?? "-"}")
                .AppendLine($"Inicio UTC: {ev.Run.StartedUtc:yyyy-MM-ddTHH:mm:ssZ}")
                .AppendLine($"Fim UTC: {ev.Run.FinishedUtc:yyyy-MM-ddTHH:mm:ssZ}")
                .AppendLine($"Operador: {ev.Run.OperatorUser}{(admin ? " (admin)" : "")}")
                .AppendLine($"Loja: {ev.Run.Shop ?? "-"}")
                .AppendLine($"Tecnico: {ev.Run.Technician ?? "-"}")
                .AppendLine()
                .AppendLine("SHA-256")
                .AppendLine($"{jsonHash}  evidencias.json")
                .AppendLine($"{Sha256(htmlPath)}  relatorio.html");
            var manifestPath = Path.Combine(outDir, "manifesto.txt");
            File.WriteAllText(manifestPath, manifest.ToString(), utf8);

            var (label, _, complete) = Rules.Verdict(ev.Checks);
            Console.WriteLine();
            Console.WriteLine($"Veredito: {label}{(complete ? "" : " (vistoria incompleta)")}");
            foreach (var c in ev.Checks.Where(c => c.Status == Status.Critical || c.Status == Status.Warning).OrderByDescending(c => Rules.Rank(c.Status)))
                Console.WriteLine($"  [{(c.Status == Status.Critical ? "CRÍTICO" : "ATENÇÃO")}] {c.Title}");
            Console.WriteLine();
            Console.WriteLine("Laudo:     " + htmlPath);
            if (!Flag("--abrir")) Console.WriteLine("           abra no seu computador, a partir do pendrive (ou rode com --abrir)");
            Console.WriteLine("Manifesto: " + manifestPath + "  (SHA-256 " + Sha256(manifestPath) + ")");

            if (Flag("--abrir"))
                try { Process.Start(new ProcessStartInfo(htmlPath) { UseShellExecute = true }); }
                catch (Win32Exception) { /* no default browser: the path is printed above */ }
            return 0;
        }

        static void Step(string name, Action a)
        {
            Console.Write($"  {name}...");
            var sw = Stopwatch.StartNew();
            a();
            Console.WriteLine($" {sw.Elapsed.TotalSeconds:0.0}s");
        }

        // Asks UAC once. If the user declines, carry on unelevated instead of leaving them with nothing.
        static bool Relaunch(string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = string.Join(" ", args.Select(Quote)),
                };
                Process.Start(psi);
                return true;
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined
            {
                Console.WriteLine("Elevação recusada: seguindo sem administrador (coleta parcial).");
                return false;
            }
        }

        // CommandLineToArgvW rules: backslashes only matter right before a quote.
        public static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (var ch in a)
            {
                if (ch == '\\') { slashes++; continue; }
                sb.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                sb.Append(ch);
            }
            return sb.Append('\\', slashes * 2).Append('"').ToString();
        }

        static string Safe(string s) => new string((s ?? "PC").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }
    }
}
