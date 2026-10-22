/**
 * @file
 * @brief Kommandozeile der Testumgebung: prüfen, bauen, simulieren, Katalog und Anleitung – ohne Oberfläche.
 */

using System.Reflection;
using System.Text;
using Testumgebung.Cli;
using Testumgebung.Persistence;
using Testumgebung.Workers;

Console.OutputEncoding = new UTF8Encoding(false);
return await Cli.MainAsync(args).ConfigureAwait(false);

namespace Testumgebung.Cli
{
    public static class Cli
    {
        public const string Usage = """
            testumgebung-cli – Prüfprogramme ohne Oberfläche prüfen, bauen und simulieren

              check    <Pfad>                     Projekte (*.tproj.json) laden und prüfen (Struktur, Bausteine, Rundreise)
              build    <Pfad> [--junit <Datei>]   prüfen und alle CodeBlocks/Bibliotheksbausteine übersetzen
              test     <Pfad> [--junit <Datei>]   prüfen, bauen und alle Szenarien (*.tsim.json) simulieren
              simulate <Szenario.tsim.json>       ein Szenario simulieren, mit Ablaufprotokoll
              katalog  [Suchtext|Bausteintyp]     Bausteinübersicht bzw. Parameter passender Bausteine
              anleitung [format|szenario|beispiel] Projektformat, Szenarioformat bzw. geprüftes Beispielprojekt
              --version

            <Pfad>: eine Projektdatei, eine Szenario-Datei oder ein Ordner (rekursiv).
            Exitcode: 0 alles bestanden, 1 Prüfpunkte fehlgeschlagen, 2 Aufruf- oder Dateifehler.
            """;

        public static async Task<int> MainAsync(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "hilfe")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }
            if (args[0] == "--version")
            {
                var version = typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
                Console.WriteLine($"testumgebung-cli {version} (Projektformat {ProjectSerializer.FormatName} {ProjectSerializer.FormatVersion})");
                return 0;
            }
            try
            {
                ValidateArguments(args);
                return args[0] switch
                {
                    "check" => await CheckAsync(Single(args), build: false, scenarios: false, Option(args, "--junit")).ConfigureAwait(false),
                    "build" => await CheckAsync(Single(args), build: true, scenarios: false, Option(args, "--junit")).ConfigureAwait(false),
                    "test" => await CheckAsync(Single(args), build: true, scenarios: true, Option(args, "--junit")).ConfigureAwait(false),
                    "simulate" => await SimulateAsync(Single(args)).ConfigureAwait(false),
                    "katalog" => Catalog(args.Skip(1).FirstOrDefault()),
                    "anleitung" => Guide(args.Skip(1).FirstOrDefault()),
                    _ => throw new ArgumentException($"Unbekannter Befehl „{args[0]}“."),
                };
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(Usage);
                return 2;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Fehler: " + ex.Message);
                return 2;
            }
        }

        private static void ValidateArguments(string[] args)
        {
            if (args[0] is "katalog" or "anleitung")
            {
                if (args.Length > 2) throw new ArgumentException("Zu viele Argumente.");
                return;
            }
            if (args[0] is not ("check" or "build" or "test" or "simulate"))
                throw new ArgumentException($"Unbekannter Befehl „{args[0]}“.");
            ValidatePathArguments(args);
        }

        private static void ValidatePathArguments(string[] args)
        {
            var positional = 0;
            var junitSeen = false;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--junit" && args[0] != "simulate" && !junitSeen)
                {
                    junitSeen = true;
                    if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("--junit braucht einen Dateipfad.");
                }
                else if (args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Unbekannte oder doppelte Option „{args[i]}“.");
                else positional++;
            }
            if (positional != 1) throw new ArgumentException("Genau einen Projekt-, Szenario- oder Ordnerpfad angeben.");
        }

        private static string Single(string[] args)
        {
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--junit") { i++; continue; }
                if (!File.Exists(args[i]) && !Directory.Exists(args[i])) throw new IOException($"Nicht gefunden: {args[i]}");
                return Path.GetFullPath(args[i]);
            }
            throw new ArgumentException("Pfad fehlt.");
        }

        private static string? Option(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i < 0 ? null : args[i + 1];
        }

        /// <summary>Projekte und Szenarien unter einem Pfad: Ordner rekursiv, Projektdatei mit den Szenarien im selben Ordnerbaum.</summary>
        private static (List<string> Projects, List<string> Scenarios) Collect(string path, bool includeScenarios)
        {
            static IEnumerable<string> Find(string dir, string pattern) => Directory.EnumerateFiles(dir, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            }).Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "node_modules")).Order(StringComparer.Ordinal);

            if (Directory.Exists(path)) return (Find(path, "*.tproj.json").ToList(), includeScenarios ? Find(path, "*.tsim.json").ToList() : []);
            if (path.EndsWith(".tsim.json", StringComparison.OrdinalIgnoreCase))
                return ([Scenario.Load(path).ProjectPath], [path]);
            if (path.EndsWith(".tproj.json", StringComparison.OrdinalIgnoreCase))
            {
                var scenarios = includeScenarios ? Find(Path.GetDirectoryName(path)!, "*.tsim.json")
                    .Where(s => ReferencesProject(s, path)).ToList() : [];
                return ([path], scenarios);
            }
            throw new ArgumentException($"Erwartet einen Ordner, eine *.tproj.json oder eine *.tsim.json: {path}");
        }

        private static async Task<int> CheckAsync(string path, bool build, bool scenarios, string? junit)
        {
            var (projects, scenarioFiles) = Collect(path, scenarios);
            var report = new Report();
            if (projects.Count == 0) report.Fail("Testumgebung", "Projekte", $"Keine *.tproj.json unter {path}.");
            using var runtime = build ? CreateRuntime() : null;
            var valid = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in projects)
            {
                var (loaded, ok) = ProjectChecks.Check(project, report);
                valid[project] = ok;
                if (loaded is not null && runtime is not null)
                {
                    // Auch bei Bausteinfehlern bauen: alle Fehler in einem Durchgang melden.
                    var before = report.Failed;
                    await ProjectChecks.BuildAsync(project, loaded, runtime, report, CancellationToken.None).ConfigureAwait(false);
                    valid[project] = ok && report.Failed == before;
                }
            }
            if (scenarios) await RunScenariosAsync(path, projects, scenarioFiles, valid, runtime!, report).ConfigureAwait(false);
            report.WriteSummary();
            if (junit is not null) report.WriteJUnit(junit);
            return report.Failed == 0 ? 0 : 1;
        }

        // A broken neighbouring scenario belongs in the report instead of preventing all project checks.
        private static bool ReferencesProject(string scenario, string project)
        {
            try { return string.Equals(Scenario.Load(scenario).ProjectPath, project, StringComparison.OrdinalIgnoreCase); }
            catch (InvalidDataException) { return true; }
        }

        private static async Task RunScenariosAsync(string path, List<string> projects, List<string> scenarioFiles,
            Dictionary<string, bool> valid, CodeRuntime runtime, Report report)
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in scenarioFiles)
            {
                Scenario scenario;
                try
                {
                    scenario = Scenario.Load(file);
                }
                catch (InvalidDataException ex)
                {
                    report.Fail(ProjectChecks.SuiteName(file), "Szenario laden", ex.Message);
                    continue;
                }
                covered.Add(scenario.ProjectPath);
                if (!valid.TryGetValue(scenario.ProjectPath, out var ok))
                {
                    report.Fail(ProjectChecks.SuiteName(file), "Projekt", $"Projektdatei {scenario.Project} nicht gefunden bzw. nicht unter {path}.");
                    continue;
                }
                if (!ok)
                {
                    report.Skip(ProjectChecks.SuiteName(file), "Simulation", "Projekt ist ungültig oder nicht baubar – siehe oben.");
                    continue;
                }
                var fresh = new ProjectSerializer().Load(scenario.ProjectPath); // jeder Lauf mit unveränderter Projektdatei
                await ScenarioRunner.RunAsync(scenario, fresh, runtime!, report, printTrace: false).ConfigureAwait(false);
            }
            foreach (var project in projects.Where(p => valid[p] && !covered.Contains(p)))
                report.Skip(ProjectChecks.SuiteName(project), "Szenarien",
                    "keine *.tsim.json für dieses Projekt – Ablauf wurde nur geprüft und gebaut, nicht simuliert (Format: testumgebung-cli anleitung szenario).");
        }

        private static async Task<int> SimulateAsync(string path)
        {
            if (!path.EndsWith(".tsim.json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("simulate erwartet eine *.tsim.json.");
            var scenario = Scenario.Load(path);
            var report = new Report();
            var (project, ok) = ProjectChecks.Check(scenario.ProjectPath, report);
            if (project is not null)
            {
                using var runtime = CreateRuntime();
                var before = report.Failed;
                await ProjectChecks.BuildAsync(scenario.ProjectPath, project, runtime, report, CancellationToken.None).ConfigureAwait(false);
                if (ok && report.Failed == before)
                {
                    Console.WriteLine($"--- Ablauf „{scenario.Name}“ ---");
                    await ScenarioRunner.RunAsync(scenario, new ProjectSerializer().Load(scenario.ProjectPath), runtime, report, printTrace: true).ConfigureAwait(false);
                }
            }
            report.WriteSummary();
            return report.Failed == 0 ? 0 : 1;
        }

        private static int Catalog(string? query)
        {
            Console.WriteLine(query is null ? CatalogText.Overview() : CatalogText.Search(query));
            return 0;
        }

        private static int Guide(string? topic)
        {
            var file = topic switch
            {
                null or "format" => "ki-testprogramme.md",
                "szenario" or "kommandozeile" => "kommandozeile.md",
                "beispiel" => "konvertierungsbeispiel.tproj.json",
                _ => throw new ArgumentException("anleitung: format, szenario oder beispiel"),
            };
            Console.WriteLine(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ki", file)));
            return 0;
        }

        /// <summary>Code-Worker wie in der Anwendung: Python aus TESTUMGEBUNG_PYTHON, sonst die mitgelieferte Laufzeit.</summary>
        private static CodeRuntime CreateRuntime() => new(new CodeRuntimeOptions
        {
            PythonExecutable = Environment.GetEnvironmentVariable("TESTUMGEBUNG_PYTHON") is { Length: > 0 } py ? py : null,
        });
    }
}
