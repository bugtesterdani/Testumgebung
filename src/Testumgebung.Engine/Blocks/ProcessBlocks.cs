/**
 * @file
 * @brief Bindet Prozessaufrufe und Exitcode-Bewertungen an die grafische Palette.
 */

using System.Globalization;
using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Processes;
using Testumgebung.Workers;

namespace Testumgebung.Engine.Blocks;

/// <summary>Graphical external-process actions; expressions are expanded inside argument values, never as a shell command.</summary>
public static class ProcessBlocks
{
    public const string Start = "prozess.start", Wait = "prozess.warten", Status = "prozess.status", Stop = "prozess.beenden";
    private static readonly string[] Kinds = ["EXE", "Python", ".NET-DLL", "DLL-Main"];
    public static void Register(BlockCatalog catalog)
    {
        catalog.Register(new(Start, "Prozess starten", "Prozesse", "arrow", "Startet ein Programm, Python-Skript oder .NET-DLL. Gebundene Prozesse werden beim Prüflingsende aufgeräumt.",
        [new("Art", "Aufrufart", ParameterKind.Choice, Kinds[0], Choices: Kinds),
         new("Datei", "Absoluter Datei-/Skriptpfad", ParameterKind.Text, Required: true, Hint: "Platzhalter {Variable} erlaubt."),
         new("Argumente", "Argumente (JSON-Liste)", ParameterKind.Text, "[]", Hint: "Beispiel: [\"--serial\",\"{Kennung}\"]"),
         new("Arbeitsverzeichnis", "Arbeitsverzeichnis", ParameterKind.Text, Hint: "Leer = Verzeichnis der Programm-/Skriptdatei. Absoluter Pfad; {Variable} erlaubt."),
         new("Umgebung", "Umgebungsvariablen (JSON-Objekt)", ParameterKind.Text, "{}", Hint: "Stringwerte; nicht protokolliert. Beispiel: {\"MODE\":\"test\"}"),
         new("Typ", "DLL-Main: vollständiger .NET-Typname", ParameterKind.Text),
         new("Python", "Eigener Python-Interpreter (optional)", ParameterKind.Text, Hint: "Leer = bestehende Python-Auswahl des Prüfprogramms."),
         new("Modus", "Lebensdauer", ParameterKind.Choice, "Gebunden", Choices: ["Gebunden", "Unabhängig"]),
         new("Fenster", "Konsolenfenster zulassen", ParameterKind.Bool, "false"),
         new("Ausgabe", "stdout/stderr erfassen", ParameterKind.Bool, "true"),
         new("Kodierung", "Ausgabekodierung", ParameterKind.Text, "utf-8", Hint: "Python: utf-8; ältere EXE ggf. windows-1252 oder ibm850."),
         new("Ausgabelimit", "Max. Zeichen je Ausgabestream", ParameterKind.Number, "65536"),
         new("Warten", "Auf Prozessende warten", ParameterKind.Bool, "true"),
         new("Zeitlimit", "Wartezeitlimit [ms]", ParameterKind.Number, "30000"),
         new("ProzessZiel", "Prozesskennung → Stringvariable", ParameterKind.Variable),
         new("PidZiel", "Host-Prozess-ID → Intvariable", ParameterKind.Variable), .. ResultParameters()], p => CreateStart(p)));
        catalog.Register(new(Wait, "Auf Prozess warten", "Prozesse", "wait", "Wartet auf eine Prozesskennung desselben Prüflaufs und bewertet erst danach den Exitcode.",
        [HandleParameter(), new("Zeitlimit", "Wartezeitlimit [ms]", ParameterKind.Number, "30000"), .. ResultParameters()], p => new WaitAction(p.Text("Prozess"), Timeout(p), Results(p))));
        catalog.Register(new(Status, "Prozessstatus abfragen", "Prozesse", "arrow", "Liefert einen Zwischenstand. Ein noch laufender Prozess wird nicht als PASS bewertet.",
        [HandleParameter(), .. ResultParameters()], p => new StatusAction(p.Text("Prozess"), Results(p))));
        catalog.Register(new(Stop, "Prozess beenden", "Prozesse", "error", "Beendet ausdrücklich die Prozesskennung und deren Prozessbaum; kein automatisches PASS.",
        [HandleParameter()], p => new StopAction(p.Text("Prozess"))));
    }
    private static ParameterDescriptor HandleParameter() => new("Prozess", "Prozesskennung", ParameterKind.Text, Required: true, Hint: "Platzhalter {MeineProzesskennung}; keine fremde Betriebssystem-PID.");
    private static ParameterDescriptor[] ResultParameters() =>
    [new("ExitcodeZiel", "Exitcode → Intvariable (erst nach Ende gültig)", ParameterKind.Variable),
     new("StdoutZiel", "stdout → Stringvariable", ParameterKind.Variable),
     new("StderrZiel", "stderr → Stringvariable", ParameterKind.Variable),
     new("FertigZiel", "Prozess beendet → Boolvariable", ParameterKind.Variable),
     new("ErwarteteExitcodes", "Erlaubte Exitcodes", ParameterKind.Text, "0", Hint: "Durch ; getrennte Zahlen; leer = unbewertet. {Variable} erlaubt."),
     new("Protokoll", "Ausgabe protokollieren", ParameterKind.Bool, "false", Hint: "Nur aktivieren, wenn die Ausgabe keine Geheimnisse enthält.")];
    private static ResultTargets Results(BlockParameters p) => new(p.Optional("ExitcodeZiel"), p.Optional("StdoutZiel"), p.Optional("StderrZiel"), p.Optional("FertigZiel"), p.Text("ErwarteteExitcodes"), p.Bool("Protokoll"));
    private static TimeSpan Timeout(BlockParameters p)
    {
        var value = p.Number("Zeitlimit") ?? 30000;
        if (!double.IsFinite(value) || value <= 0 || value > int.MaxValue) throw new BlockConfigurationException("Zeitlimit muss zwischen 1 und 2147483647 ms liegen.");
        return TimeSpan.FromMilliseconds(value);
    }
    private static StartAction CreateStart(BlockParameters p)
    {
        var limit = p.Number("Ausgabelimit") ?? 65536;
        if (!double.IsFinite(limit) || limit != Math.Truncate(limit) || limit is < 0 or > 1048576) throw new BlockConfigurationException("Ausgabelimit muss eine ganze Zahl zwischen 0 und 1048576 sein.");
        var kind = (ExternalProcessKind)Array.IndexOf(Kinds, p.Choice("Art"));
        if (kind == ExternalProcessKind.ManagedMain && p.Optional("Typ") is null) throw new BlockConfigurationException("DLL-Main benötigt einen vollständigen Typnamen.");
        if (!p.Bool("Warten") && p.Optional("ProzessZiel") is null) throw new BlockConfigurationException("Start ohne Warten benötigt eine String-Zielvariable für die Prozesskennung.");
        return new(p.Text("Datei"), p.Text("Argumente"), p.Text("Arbeitsverzeichnis"), p.Text("Umgebung"), kind, p.Optional("Typ"), p.Optional("Python"),
            p.Choice("Modus") == "Gebunden", p.Bool("Fenster"), p.Bool("Ausgabe"), (int)limit, p.Text("Kodierung"), p.Bool("Warten"), Timeout(p), p.Optional("ProzessZiel"), p.Optional("PidZiel"), Results(p));
    }
    private static string Expand(string text, IStepContext ctx) => CommunicationBlocks.Interpolate(text, ctx);
    private static string[] Arguments(string text, IStepContext ctx)
    {
        var arguments = JsonSerializer.Deserialize<string[]>(text) ?? throw new BlockConfigurationException("Argumente müssen eine JSON-Liste aus Strings sein.");
        return arguments.Select(value => Expand(value ?? throw new BlockConfigurationException("Argument darf nicht null sein."), ctx)).ToArray();
    }
    private static Dictionary<string, string> Environment(string text, IStepContext ctx)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? throw new BlockConfigurationException("Umgebung muss ein JSON-Objekt aus Strings sein.");
        return values.ToDictionary(pair => pair.Key, pair => Expand(pair.Value ?? throw new BlockConfigurationException("Umgebungswert darf nicht null sein."), ctx));
    }
    private static void CheckTarget(IStepContext ctx, string? name, Type type)
    {
        if (name is not null && ctx.Get(name).GetType() != type) throw new BlockConfigurationException($"Zielvariable {name} hat nicht den erforderlichen Typ {type.Name}.");
    }
    private sealed record ResultTargets(string? Exit, string? Output, string? Error, string? Done, string Expected, bool Log)
    {
        public void Validate(IStepContext ctx)
        {
            CheckTarget(ctx, Exit, typeof(long)); CheckTarget(ctx, Output, typeof(string));
            CheckTarget(ctx, Error, typeof(string)); CheckTarget(ctx, Done, typeof(bool));
            if (Output is not null) ctx.Set(Output, "");
            if (Error is not null) ctx.Set(Error, "");
            if (Done is not null) ctx.Set(Done, false);
        }
        public void Assign(ProcessSnapshot result, IStepContext ctx)
        {
            if (Exit is not null && result.ExitCode is { } code) ctx.Set(Exit, (long)code);
            if (Output is not null) ctx.Set(Output, result.StandardOutput);
            if (Error is not null) ctx.Set(Error, result.StandardError);
            if (Done is not null) ctx.Set(Done, result.Completed);
            if (result.OutputTruncated) ctx.Log("Prozessausgabe wurde auf das konfigurierte Zeichenlimit gekürzt.");
            if (Log) { ctx.Log(result.StandardOutput); ctx.Log(result.StandardError); }
        }
        public StepOutcome Apply(ProcessSnapshot result, IStepContext ctx)
        {
            Assign(result, ctx);
            if (result.HostError is not null) return StepOutcome.Error("Prozesshost: " + result.HostError);
            if (result.Stopped) return StepOutcome.Error("Prozess wurde beendet; keine reguläre Exitcode-Bewertung.");
            if (!result.Completed) return StepOutcome.Unrated(result.Handle, "Prozess läuft; Exitcode noch nicht bekannt.");
            var expected = Expand(Expected, ctx);
            if (string.IsNullOrWhiteSpace(expected)) return StepOutcome.Unrated(result.ExitCode);
            var allowed = expected.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(code => int.Parse(code, CultureInfo.InvariantCulture));
            return allowed.Contains(result.ExitCode!.Value) ? StepOutcome.Pass(result.ExitCode) : StepOutcome.Fail("Unerwarteter Exitcode: " + result.ExitCode, result.ExitCode);
        }
    }
    private sealed record StartAction(string File, string Args, string Directory, string Variables, ExternalProcessKind Kind, string? Type, string? Python,
        bool Bound, bool Show, bool Capture, int Limit, string Encoding, bool Wait, TimeSpan Timeout, string? HandleTarget, string? PidTarget, ResultTargets Targets) : IStepAction
    {
        public async Task<StepOutcome> ExecuteAsync(IStepContext ctx)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            CheckTarget(ctx, HandleTarget, typeof(string));
            CheckTarget(ctx, PidTarget, typeof(long));
            Targets.Validate(ctx);
            var file = Expand(File, ctx);
            var directory = string.IsNullOrWhiteSpace(Directory) ? Path.GetDirectoryName(file)! : Expand(Directory, ctx);
            var request = new ProcessLaunch(Kind, file, Arguments(Args, ctx), directory, Environment(Variables, ctx), Bound, Show, Capture, Limit,
                Type is null ? null : Expand(Type, ctx), Python is null ? null : Expand(Python, ctx), Expand(Encoding, ctx));
            var options = (ctx.CodeRuntime as CodeRuntime)?.Options ?? new CodeRuntimeOptions();
            var variable = ctx.VisibleVariables().Any(v => v.Name == "PythonVenv" && v.Type == VarType.String) ? ctx.Get("PythonVenv") as string : null;
            var python = PythonEnvironmentResolver.Resolve(options.WorkerDirectory, ctx.ProgramPythonVenv, variable, options.PythonExecutable ?? new PythonEnvironments().LoadSelection());
            var result = ctx.Processes.Start(request, ctx.ProcessOwner, options.WorkerDirectory, python, ctx.CancellationToken);
            if (HandleTarget is not null) ctx.Set(HandleTarget, result.Handle);
            if (PidTarget is not null) ctx.Set(PidTarget, (long)result.ProcessId);
            if (!Wait)
            {
                Targets.Assign(result, ctx);
                return StepOutcome.Unrated(result.Handle, "Prozess gestartet; Exitcode separat abfragen.");
            }
            result = await ctx.Processes.WaitAsync(result.Handle, Timeout, ctx.CancellationToken).ConfigureAwait(false);
            return Targets.Apply(result, ctx);
        }
    }
    private sealed record WaitAction(string Handle, TimeSpan Timeout, ResultTargets Targets) : IStepAction
    {
        public async Task<StepOutcome> ExecuteAsync(IStepContext ctx)
            => Targets.Apply(await ctx.Processes.WaitAsync(Expand(Handle, ctx), Timeout, ctx.CancellationToken).ConfigureAwait(false), ctx);
    }
    private sealed record StatusAction(string Handle, ResultTargets Targets) : IStepAction
    {
        public Task<StepOutcome> ExecuteAsync(IStepContext ctx) => Task.FromResult(Targets.Apply(ctx.Processes.Snapshot(Expand(Handle, ctx)), ctx));
    }
    private sealed record StopAction(string Handle) : IStepAction
    {
        public Task<StepOutcome> ExecuteAsync(IStepContext ctx)
        { ctx.Processes.Stop(Expand(Handle, ctx)); return Task.FromResult(StepOutcome.Unrated(message: "Prozessbeendigung angefordert.")); }
    }
}
