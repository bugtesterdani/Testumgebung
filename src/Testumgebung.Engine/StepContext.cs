/**
 * @file
 * @brief Vermittelt einem Schritt seine Variablen, Geräte, Werkerinteraktion und Abbruchsignale.
 */

using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Scopes;
using Testumgebung.Hardware;

namespace Testumgebung.Engine;

/// <summary>Anbindung der Werkeroberfläche an Werkerinteraktionen.</summary>
public interface IOperatorInterface
{
    Task<object?> AskAsync(string runId, OperatorPrompt prompt, CancellationToken cancellationToken);
}

/// <summary>Laufzeitkontext eines Schritts – Variablen des Ordners, Hardware über abstrakte Namen.</summary>
internal sealed class StepContext(
    TestRun run,
    StepDefinition step,
    string path,
    Scope scope,
    ExecutionPhase phase,
    int iteration) : IStepContext
{
    private readonly CancellationToken token = phase.Token;

    private static long _displayCounter;

    public string RunId => run.RunId;
    public IReadOnlyList<Testumgebung.Domain.Code.ManagedAssembly> ManagedAssemblies => run.Definition.Assemblies;
    public string StepId => step.Id;
    public string Path => path;
    public CancellationToken CancellationToken => token;
    public IClock Clock => run.Clock;

    public object Get(string name) => scope.Get(name);

    public object? Preset(string name) => run.Options.Presets is { } p && p.TryGetValue(name, out var v) ? v
        : run.Options.SessionPresets is { } s && s.TryGetValue(name, out var sv) ? sv : null;

    public void Set(string name, object? value) => scope.Set(name, value);

    public async Task<double> ReadAsync(string resource)
    {
        var binding = CheckAccess(resource);
        using (Track(binding, "lesen"))
            return await binding.Adapter.ReadAsync(binding.Channel, token).ConfigureAwait(false);
    }

    public async Task WriteAsync(string resource, double value)
    {
        var binding = CheckAccess(resource);
        using (Track(binding, "schreiben"))
            await binding.Adapter.WriteAsync(binding.Channel, value, token).ConfigureAwait(false);
    }

    public async Task<T> UseDeviceAsync<T>(string resource, string operation, Func<IHardwareAdapter, string, CancellationToken, Task<T>> action)
    {
        var binding = CheckAccess(resource);
        using (Track(binding, operation))
            return await action(binding.Adapter, binding.Channel, token).ConfigureAwait(false);
    }

    /// <summary>Zugriff im gemeinsamen Protokoll erfassen (falls vorhanden).</summary>
    private IDisposable? Track(ResourceBinding binding, string operation)
        => run.Options.AccessJournal?.Begin(run.RunId, run.Options.StationName, binding, operation);

    public Testumgebung.Domain.Curves.CurveDefinition? FindCurve(string id, string? version) => run.Definition.Curves.Find(id, version);

    public BusDatabaseDefinition? FindBusDatabase(string name) => run.Definition.FindBusDatabase(name);

    public async Task<OutputState> CurveOutputAsync(string resource, CurveCommand command, OutputCurve? curve)
    {
        var binding = CheckAccess(resource);
        if (binding.Adapter is not IWaveformOutputAdapter output)
            throw new InvalidOperationException($"Gerät '{binding.Adapter.Name}' ({resource}) ist kein Kurvenausgang.");
        using var tracked = Track(binding, "Kurve " + command);
        switch (command)
        {
            case CurveCommand.Load:
                await output.LoadAsync(binding.Channel, curve ?? throw new ArgumentNullException(nameof(curve)), token).ConfigureAwait(false);
                break;
            case CurveCommand.Transfer:
                await output.TransferAsync(binding.Channel, token).ConfigureAwait(false);
                break;
            case CurveCommand.Start:
                await output.StartAsync(binding.Channel, token).ConfigureAwait(false);
                break;
            default:
                await output.StopAsync(binding.Channel, CancellationToken.None).ConfigureAwait(false); // Stoppen auch in der Abbruchfolge
                break;
        }
        return output.GetOutputState(binding.Channel);
    }

    public IReadOnlyList<SuitabilityIssue> CheckOutput(string resource, OutputCurve curve)
    {
        var binding = CheckAccess(resource);
        if (binding.Adapter is not IWaveformOutputAdapter output)
            throw new InvalidOperationException($"Gerät '{binding.Adapter.Name}' ({resource}) ist kein Kurvenausgang.");
        return OutputSuitability.Check(output.GetOutputCapabilities(binding.Channel), curve);
    }

    public Task<WaveformSet> CaptureAsync(IReadOnlyList<string> resources, double sampleRate, int samples)
    {
        if (resources.Count == 0) throw new InvalidOperationException("Keine Kanäle für die Kurvenaufnahme angegeben.");
        var bindings = resources.Select(CheckAccess).ToList();
        var adapter = bindings[0].Adapter;
        if (bindings.Any(b => !ReferenceEquals(b.Adapter, adapter)))
            throw new InvalidOperationException("Gemeinsame Zeitbasis: alle Kanäle einer Aufnahme müssen am selben Gerät liegen.");
        if (adapter is not IWaveformAdapter wave)
            throw new InvalidOperationException($"Gerät '{adapter.Name}' unterstützt keine Kurvenaufnahme.");
        return CaptureMapped(wave, bindings, sampleRate, samples);
    }

    private async Task<WaveformSet> CaptureMapped(IWaveformAdapter wave, List<ResourceBinding> bindings, double sampleRate, int samples)
    {
        var tracked = bindings.Select(b => Track(b, "Kurvenaufnahme")).ToList();
        WaveformSet set;
        try
        {
            set = await wave.CaptureAsync(bindings.Select(b => b.Channel).ToList(), sampleRate, samples, token).ConfigureAwait(false);
        }
        finally
        {
            foreach (var t in tracked) t?.Dispose();
        }
        // Gerätekanal → abstrakter Name (Tests und Ergebnisse verwenden nur abstrakte Namen).
        return new WaveformSet(set.Channels.Select((w, i) => w with { Channel = bindings[i].Name }).ToList());
    }

    public async Task<object?> AskOperatorAsync(OperatorPrompt prompt)
    {
        var shown = prompt with
        {
            DisplayId = $"{step.Id}#{Interlocked.Increment(ref _displayCounter)}",
            GroupScopeId = scope.Id,
            StepName = step.Name,
        };
        run.Emit(new OperatorContentShown(shown) { Time = run.Clock.Now, RunId = run.RunId, NodeId = step.Id, Path = path });

        // „Ablauf fortsetzen“: Inhalt anzeigen, nicht auf Antwort warten.
        if (!shown.WaitForAnswer) return shown.ExpectsValue ? shown.DefaultValue : null;

        var ui = run.Options.Operator ?? throw new InvalidOperationException("Keine Werkeroberfläche angebunden.");
        object? answer;
        using (run.EnterTransientState(EngineState.AwaitingOperator))
            answer = await ui.AskAsync(run.RunId, shown, token).ConfigureAwait(false);

        var (value, error) = shown.Validate(answer);
        if (error is not null) throw new InvalidOperationException($"Werkereingabe ungültig: {error}");
        run.Emit(new OperatorAnswered(shown.DisplayId, value) { Time = run.Clock.Now, RunId = run.RunId, NodeId = step.Id, Path = path });
        return shown.ExpectsValue ? value : null;
    }

    public IReadOnlyList<(string Name, VarType Type)> VisibleVariables()
        => scope.VisibleVariables().Select(v => (v.Name, v.Type)).ToList();

    public string? ProgramPythonVenv => run.Options.PythonVenv;
    public PairedStationContext? PairedStation => run.Options.PairedStation;

    public Testumgebung.Domain.Code.ICodeRuntime? CodeRuntime => run.Options.CodeRuntime;

    public Testumgebung.Domain.Code.CodeDebugConfig CodeDebugConfig
        => run.Options.Debugger?.CodeConfigFor(step.Id) ?? Testumgebung.Domain.Code.CodeDebugConfig.Off;

    public Task<Testumgebung.Domain.Code.CodeDebugStop> CodeDebugAsync(
        Testumgebung.Domain.Code.CodeDebugEvent debugEvent, Testumgebung.Domain.Code.CodeDebugCapability capability)
        => run.Options.Debugger is { } debugger
            ? debugger.OnCodeEventAsync(step.Id, step.Name, path, iteration, capability, debugEvent, token)
            : Task.FromResult(Testumgebung.Domain.Code.CodeDebugStop.Continue);

    public Testumgebung.Domain.Code.CodeBlockDefinition? FindLibraryBlock(string id)
        => run.Definition.Library.FirstOrDefault(b => b.Id == id);

    public GroupBlockDefinition? FindGroupBlock(string id)
        => run.Definition.GroupLibrary.FirstOrDefault(b => b.Id == id);

    public Task<(ResultRating Rating, IReadOnlyDictionary<string, object?> Outputs)> ExecuteGroupBlockAsync(
        GroupBlockDefinition block, IReadOnlyDictionary<string, object?> inputs)
        => run.ExecuteGroupBlockAsync(block, inputs, phase, path, iteration);

    public void Log(string message)
        => run.Emit(new Diagnostic(step.Id, message) { Time = run.Clock.Now, RunId = run.RunId });

    private ResourceBinding CheckAccess(string resource)
    {
        var registry = run.Options.Hardware ?? throw new InvalidOperationException("Keine Prüfplatzkonfiguration (HardwareRegistry) gesetzt.");
        var binding = registry.Resolve(resource);
        if (binding.RequiresLock && !run.Resources.IsHeldBy(binding.Key, run.RunId))
            throw new UnreservedHardwareAccessException(
                $"Zugriff auf '{resource}' ohne Reservierung. Gemeinsam genutzte Hardware muss an einer Gruppe reserviert werden.");
        return binding;
    }
}
