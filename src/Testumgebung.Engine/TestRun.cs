/**
 * @file
 * @brief Orchestriert Gruppen, Wiederholungen und Exit-Abläufe; Ereignisse trennen Ausführung von Darstellung und Speicherung.
 */

using System.Collections.Concurrent;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Results;
using Testumgebung.Domain.Scopes;
using Testumgebung.Hardware;

namespace Testumgebung.Engine;

public sealed class TestRunOptions
{
    public IClock Clock { get; init; } = SystemClock.Instance;

    /// <summary>Gemeinsame Ressourcenvermittlung aller Prüfplätze. Ohne Angabe: eigene Instanz.</summary>
    public IResourceManager? Resources { get; init; }

    public HardwareRegistry? Hardware { get; init; }

    /// <summary>Gemeinsames Zugriffsprotokoll aller Prüfplätze (Nachweis: kein widersprüchlicher Gerätezugriff).</summary>
    public HardwareAccessJournal? AccessJournal { get; init; }

    public IOperatorInterface? Operator { get; init; }
    public DebugController? Debugger { get; init; }
    public string StationName { get; init; } = "Prüfplatz 01";

    /// <summary>Bausteinkatalog (für Run-Patches). Standard: <see cref="Blocks.BlockCatalog.Default"/>.</summary>
    public Blocks.BlockCatalog? Catalog { get; init; }

    /// <summary>Worker für C#-/Python-CodeBlocks (Etappe 4).</summary>
    public Testumgebung.Domain.Code.ICodeRuntime? CodeRuntime { get; init; }

    /// <summary>Python-Umgebung aus dem Auswahlprogramm (höchste Priorität).</summary>
    public string? PythonVenv { get; init; }
    public PairedStationContext? PairedStation { get; init; }

    /// <summary>
    /// Auswahl-/Auftragsablauf (Abschnitt 5): höchstens so viele Prüflinge, danach keine weiteren Wiederholungen –
    /// der Lauf endet regulär über die Finalization. null = unbegrenzt.
    /// </summary>
    public int? DutLimit { get; init; }

    public bool ContinuousLoop { get; init; }
    public IReadOnlyDictionary<string, object?>? SessionPresets { get; init; }

    /// <summary>
    /// Vorbelegung aus der Auswahl (Kennung, Auftrag, Prüfparameter): beim Eintritt in den Prüflingstest werden sichtbare
    /// Variablen gleichen Namens gesetzt; Werkereingaben mit dieser Zielvariable übernehmen den Wert ohne erneute Abfrage.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Presets { get; init; }

    /// <summary>Neue freigegebene Version: wird nur beim Eintritt in den nächsten Prüflingstest übernommen (V01).</summary>
    public IVersionSource? VersionSource { get; init; }
    public int EventHistoryLimit { get; init; } = int.MaxValue;
    public bool RetainWaveformsInHistory { get; init; } = true;
    public bool DevelopmentRun { get; init; }
}

/// <summary>
/// Liefert beim Eintritt in einen Prüflingstest ggf. eine neuere Version. Die Engine übernimmt sie nur dort –
/// ein laufender Prüfling behält seine gestartete Version. Die Quelle prüft die Verträglichkeit
/// (Init, Finalization, Loop außerhalb des Prüflings und Hauptablauf-Variablen unverändert).
/// </summary>
public interface IVersionSource
{
    TestDefinition? NextDefinition(TestDefinition current);
}

/// <summary>Eintrag des Ausführungsstacks (konkrete Verschachtelung statt globaler Statusvariable).</summary>
public sealed record ExecutionFrame(string NodeId, string Name, string Path, int Iteration, bool IsExit, bool IsGroup, string? ScopeId)
{
    internal string Branch { get; init; } = "";
}

/// <summary>Ergebnis einer gezielten Ausführung im Debugkontext.</summary>
public sealed record TargetedExecutionResult(bool Executed, ResultRating Rating, string? RefusalReason);

/// <summary>
/// Ein Lauf eines Prüfplatzablaufs: Init → Loop (bis Werkerabbruch) → Finalization.
/// Mehrere Läufe (Prüfplätze) sind unabhängig und teilen sich nur die <see cref="IResourceManager"/>.
/// </summary>
public sealed class TestRun
{
    internal AsyncLocal<string?> ProcessOwnerOverride { get; } = new();
    internal Testumgebung.Workers.ProcessSession Processes { get; } = new();
    private static int _runCounter;

    private readonly object _gate = new();
    private readonly object _eventGate = new();
    private readonly ConcurrentDictionary<string, IResourceLease> _scopeReservations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _debugGate = new(1, 1);
    internal AsyncLocal<string?> ExecutionBranch { get; } = new();
    private readonly List<EngineEvent> _events = [];
    private readonly List<ExecutionFrame> _stack = [];
    private readonly List<EngineState> _transient = [];
    private readonly AbortCoordinator _abort = new();
    private long _sequence;
    private int _started;
    private EngineState _lastPublishedState = EngineState.Idle;
    private bool _completed;
    private readonly RunPatcher _patcher;

    public TestRun(TestDefinition definition, TestRunOptions? options = null, string? runId = null)
    {
        Definition = definition;
        Options = options ?? new TestRunOptions();
        IsDevelopmentRun = Options.DevelopmentRun;
        Clock = Options.Clock;
        Resources = Options.Resources ?? new ResourceManager(Clock);
        if (!Resources.SupportsParallelBranches && definition.AllNodes().OfType<GroupDefinition>().Any(g => g.Parallel))
            throw new ArgumentException("Die Ressourcenvermittlung unterstützt keine parallelen Ausführungszweige.", nameof(options));
        RunId = runId ?? $"Run-{Interlocked.Increment(ref _runCounter):D3}";
        _patcher = new RunPatcher(this, Options.Catalog ?? Blocks.BlockCatalog.Default);
        if (Options.Debugger is { } dbg) dbg.Run = this;
    }

    /// <summary>Aktive Definition; wechselt nur an der Grenze zwischen zwei Prüflingstests (siehe <see cref="IVersionSource"/>).</summary>
    public TestDefinition Definition { get; private set; }
    public TestRunOptions Options { get; }
    public IClock Clock { get; }
    public IResourceManager Resources { get; }
    public string RunId { get; }

    /// <summary>Lauf enthält Entwicklereingriffe (Variablenänderung, Patch, gezielte Ausführung).</summary>
    public bool IsDevelopmentRun { get; private set; }

    public bool OperatorAborted => _abort.OperatorAborted;

    public event Action<EngineEvent>? EventRaised;

    public IReadOnlyList<EngineEvent> Events
    {
        get { lock (_gate) return _events.ToList(); }
    }

    public IReadOnlyList<ExecutionFrame> ExecutionStack
    {
        get { lock (_gate) return _stack.ToList(); }
    }

    public EngineState State
    {
        get { lock (_gate) return ComputeState(); }
    }

    /// <summary>Startet den Hauptablauf und liefert Bewertung plus WERROR-Kennzeichen.</summary>
    public async Task<RunStatus> RunAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) throw new InvalidOperationException("Lauf wurde bereits gestartet.");
        var diagnostics = Definition.Validate();
        if (diagnostics.Count > 0)
            throw new InvalidOperationException("Ablauf ungültig: " + string.Join(" ", diagnostics));

        var total = new ResultAccumulator();
        PublishState();
        try { await ExecuteNodeAsync(Definition.Root, null, _abort.Regular, total, "", 1, forceEnter: false).ConfigureAwait(false); }
        finally { Processes.Dispose(); }
        lock (_gate) _completed = true;
        PublishState();
        return new RunStatus(total.Rating, _abort.OperatorAborted);
    }

    /// <summary>Anzahl angewendeter Run-Patches.</summary>
    public int PatchCount => _patcher.PatchCount;

    /// <summary>Run-Patch prüfen, ohne ihn anzuwenden („Patch prüfen“).</summary>
    public PatchResult ValidatePatch(IReadOnlyList<PatchChange> changes) => _patcher.Validate(changes);

    /// <summary>Run-Patch im pausierten Lauf anwenden („Änderungen übernehmen“). Gilt nur für diesen Lauf.</summary>
    public PatchResult ApplyPatch(IReadOnlyList<PatchChange> changes, string user) => _patcher.Apply(changes, user);

    /// <summary>Werkerabbruch (nummeriertes Ereignis). Wirkung siehe <see cref="AbortCoordinator"/>.</summary>
    public void RequestAbort()
    {
        var (number, effect, _) = _abort.RequestAbort();
        Emit(new OperatorAbortRequested(number, effect) { Time = Clock.Now, RunId = RunId });
    }

    // ------------------------------------------------------------------ gezielte Ausführung (Abschnitt 6.3)

    /// <summary>
    /// Einzelnen Schritt gezielt ausführen – unabhängig von Bedingungen der umgebenden Gruppen.
    /// Fehlende Kontextwerte können über <paramref name="context"/> vorgegeben werden.
    /// </summary>
    public async Task<TargetedExecutionResult> ExecuteStepTargetedAsync(string stepId, IReadOnlyDictionary<string, object>? context = null, string user = "")
    {
        if (Definition.Find(stepId) is not StepDefinition step) throw new KeyNotFoundException($"Schritt '{stepId}' nicht gefunden.");
        var scope = BuildDebugContext(stepId, context);
        MarkDevelopmentRun();
        Emit(new DevelopmentEvent(user, "Schritt gezielt ausgeführt", step.Name) { Time = Clock.Now, RunId = RunId });
        using var phase = new ExecutionPhase("gezielt", isExit: false);
        using var processScope = BeginTargetedProcessScope();
        var rating = await ExecuteStepAsync(step, scope, phase, PathOf(stepId), 1).ConfigureAwait(false);
        return new TargetedExecutionResult(rating is not null, rating ?? ResultRating.NResult, null);
    }

    /// <summary>Gruppe gezielt ausführen – nur wenn ihre Ausführungsbedingung erfüllt ist; sonst verweigert mit Ursache.</summary>
    public async Task<TargetedExecutionResult> ExecuteGroupTargetedAsync(string groupId, IReadOnlyDictionary<string, object>? context = null, string user = "")
    {
        if (Definition.Find(groupId) is not GroupDefinition group) throw new KeyNotFoundException($"Gruppe '{groupId}' nicht gefunden.");
        var parentScope = BuildDebugContext(groupId, context);
        if (group.ParsedEntryCondition is { } condition)
        {
            var probe = Scope.CreateFor(group, parentScope);
            string? refusal = null;
            try
            {
                if (!condition.EvaluateBool(probe)) refusal = $"Bedingung nicht erfüllt: {condition.Source}";
            }
            catch (Exception ex) when (ex is ExpressionException or VariableNotFoundException or TypeMismatchException)
            {
                refusal = $"Bedingung nicht auswertbar: {ex.Message}";
            }
            if (refusal is not null)
            {
                Emit(new Diagnostic(groupId, $"Gezielte Ausführung verweigert – {refusal}") { Time = Clock.Now, RunId = RunId });
                return new TargetedExecutionResult(false, ResultRating.NResult, refusal);
            }
        }
        MarkDevelopmentRun();
        Emit(new DevelopmentEvent(user, "Gruppe gezielt ausgeführt", group.Name) { Time = Clock.Now, RunId = RunId });
        using var phase = new ExecutionPhase("gezielt", isExit: false);
        var parentPath = string.Join(" > ", Definition.AncestorsOf(groupId).Skip(1).Select(g => g.Name));
        using var processScope = BeginTargetedProcessScope();
        var result = await ExecuteGroupAsync(group, parentScope, phase, parentPath, 1, forceEnter: false, isExit: false).ConfigureAwait(false);
        return new TargetedExecutionResult(result is not null, result?.Rating ?? ResultRating.NResult, null);
    }

    private IDisposable BeginTargetedProcessScope()
    {
        var owner = RunId + "/debug/" + Guid.NewGuid().ToString("N");
        ProcessOwnerOverride.Value = owner;
        return new TargetedProcessScope(this, owner);
    }
    private sealed class TargetedProcessScope(TestRun run, string owner) : IDisposable
    {
        public void Dispose() { run.Processes.CloseOwner(owner); run.ProcessOwnerOverride.Value = null; }
    }

    private Scope BuildDebugContext(string nodeId, IReadOnlyDictionary<string, object>? context)
    {
        Scope? scope = null;
        if (Definition.OwningGroupBlock(nodeId) is { } block) scope = PortScope(block, new Dictionary<string, object?>());
        foreach (var ancestor in Definition.AncestorsOf(nodeId))
        {
            scope = Scope.CreateFor(ancestor, scope);
            if (ancestor.Resources.Count > 0 && !scope.TryGetLocal(ResourcesAcquiredVariable, out _))
                scope.Define(ResourcesAcquiredVariable, VarType.Bool, false);
        }
        if (context is not null)
            foreach (var (name, value) in context) scope!.Set(name, value);
        return scope!;
    }

    /// <summary>Schnittstellen-Scope eines grafischen Bausteins: Ports als Variablen, Grenze für die Namenssuche.</summary>
    private static Scope PortScope(GroupBlockDefinition block, IReadOnlyDictionary<string, object?> inputs)
    {
        var scope = new Scope($"Baustein {block.Name}", null, isInterfaceBoundary: true, groupId: block.Id);
        foreach (var port in block.Ports)
            scope.Define(port.Name, port.Type, inputs.TryGetValue(port.Name, out var v) ? v : null);
        return scope;
    }

    /// <summary>
    /// Führt den Körper eines grafischen Bausteins in der Phase des aufrufenden Schritts aus
    /// (Werkerabbruch, Exit-Gruppen und Haltepunkte gelten wie im Hauptablauf).
    /// </summary>
    internal async Task<(ResultRating Rating, IReadOnlyDictionary<string, object?> Outputs)> ExecuteGroupBlockAsync(
        GroupBlockDefinition block, IReadOnlyDictionary<string, object?> inputs, ExecutionPhase phase, string callerPath, int iteration)
    {
        var scope = PortScope(block, inputs);
        var accumulator = new ResultAccumulator();
        await ExecuteNodeAsync(block.Body, scope, phase, accumulator, callerPath, iteration, forceEnter: false).ConfigureAwait(false);
        var outputs = block.Ports.Where(p => p.IsOutput).ToDictionary(p => p.Name, p => (object?)scope.Get(p.Name), StringComparer.Ordinal);
        return (accumulator.Rating, outputs);
    }

    private string PathOf(string nodeId)
    {
        var parents = Definition.AncestorsOf(nodeId).Skip(1).Select(g => g.Name);
        return string.Join(" > ", parents.Append(Definition.Find(nodeId)!.Name));
    }

    // ------------------------------------------------------------------ Ausführung

    /// <summary>Name der eingebauten Variable mit dem Erwerbsstatus der Gruppenressourcen.</summary>
    public const string ResourcesAcquiredVariable = "RessourcenErhalten";

    /// <summary>„ – belegt von Prüfplatz 02“ für die Meldung (aktueller Stand der Vermittlung).</summary>
    private string Holders(IEnumerable<string> names)
    {
        var keys = LockKeys(names);
        var owners = Resources.Snapshot().Where(o => keys.Contains(o.Resource) && o.OwnerRunId is not null && o.OwnerRunId != RunId)
            .Select(o => o.OwnerName ?? o.OwnerRunId!).Distinct().ToList();
        return owners.Count == 0 ? "" : " – belegt von " + string.Join(", ", owners);
    }

    /// <summary>Prüft den tatsächlichen Besitz einer Vorfahrengruppe, unabhängig von Geschwisterzweigen.</summary>
    internal bool HasScopeReservation(string scopeId, string key)
        => _scopeReservations.TryGetValue(scopeId, out var lease) && lease.Request.Resources.Contains(key);

    /// <summary>Abstrakte Namen → Sperrschlüssel der Prüfplatzkonfiguration (gleiches Gerät = gleiche Sperre).</summary>
    internal IReadOnlyList<string> LockKeys(IEnumerable<string> names)
        => names.Select(n => Options.Hardware?.LockKeyOf(n) ?? n).Distinct(StringComparer.Ordinal).ToList();

    private sealed record NodeResult(ResultRating Rating, ScopeSnapshot? Snapshot);

    /// <summary>
    /// Führt ein Element aus und überträgt seine Bewertung an die Eltern – bei Haltepunkt erst nach dem Fortsetzen.
    /// </summary>
    private async Task ExecuteNodeAsync(
        NodeDefinition node, Scope? parentScope, ExecutionPhase phase, ResultAccumulator parent,
        string parentPath, int parentIteration, bool forceEnter, bool isExit = false)
    {
        var pending = new PendingResult();
        var executions = 0;
        while (true)
        {
            NodeResult? result = node switch
            {
                StepDefinition step => await ExecuteStepAsync(step, parentScope!, phase, Combine(parentPath, step.Name), parentIteration).ConfigureAwait(false) is { } r
                    ? new NodeResult(r, null)
                    : null,
                GroupDefinition group => await ExecuteGroupAsync(group, parentScope, phase, parentPath, parentIteration, forceEnter, isExit).ConfigureAwait(false),
                _ => throw new NotSupportedException(node.GetType().Name),
            };
            if (result is null) return; // nicht ausgeführt / nicht betreten
            executions++;
            pending.Replace(result.Rating);

            // Haltepunkt: nach Ausführung (Ausgänge wirksam), vor Bewertungsrückgabe.
            if (await PauseAfterNodeAsync(node, parentScope, phase, parentPath, parentIteration, pending, parent, executions, result).ConfigureAwait(false))
            {
                MarkDevelopmentRun();
                continue;
            }
            break;
        }
        pending.CommitTo(parent);
    }

    private async Task<bool> PauseAfterNodeAsync(NodeDefinition node, Scope? parentScope, ExecutionPhase phase, string parentPath,
        int iteration, PendingResult pending, ResultAccumulator parent, int executions, NodeResult result)
    {
        if (Options.Debugger is not { } dbg || parentScope is null || phase.IsCancelled || !dbg.ShouldPause(node.Id, Combine(parentPath, node.Name))) return false;
        await _debugGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (phase.IsCancelled || !dbg.ShouldPause(node.Id, Combine(parentPath, node.Name))) return false;
            dbg.ClearStep();
            var info = new PauseInfo(node.Id, node.Name, Combine(parentPath, node.Name), iteration, node is GroupDefinition,
                pending.Rating, parent.Rating, executions, result.Snapshot);
            var waiting = dbg.BeginPause(info, parentScope);
            Emit(new Paused(info) { Time = Clock.Now, RunId = RunId });
            DebugCommand command;
            using (EnterTransientState(EngineState.PausedBeforeResultReturn))
                command = await dbg.WaitAsync(waiting, info, phase.Token).ConfigureAwait(false);
            Emit(new Resumed(command) { Time = Clock.Now, RunId = RunId });
            return command == DebugCommand.Rerun && !phase.IsCancelled;
        }
        finally { _debugGate.Release(); }
    }

    private async Task ExecuteParallelChildrenAsync(GroupDefinition group, Scope scope, ExecutionPhase phase,
        ResultAccumulator accumulator, string path, int iteration)
    {
        var parentBranch = ExecutionBranch.Value ?? "";
        var started = Clock.Now;
        var tasks = group.Children.ToArray().Select(child => Task.Run(async () =>
        {
            using var clockBranch = (Clock as IParallelClock)?.BeginBranch(started);
            ExecutionBranch.Value = parentBranch + "/" + Guid.NewGuid().ToString("N");
            try { await ExecuteNodeAsync(child, scope, phase, accumulator, path, iteration, forceEnter: false).ConfigureAwait(false); }
            finally { ExecutionBranch.Value = parentBranch; }
        })).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ResultRating?> ExecuteStepAsync(StepDefinition step, Scope scope, ExecutionPhase phase, string path, int iteration)
    {
        if (phase.IsCancelled) return null;

        Emit(new StepStarted { Time = Clock.Now, RunId = RunId, NodeId = step.Id, Path = path, Iteration = iteration });
        PushFrame(new ExecutionFrame(step.Id, step.Name, path, iteration, phase.IsExit, false, null));
        StepOutcome outcome;
        var cancelled = false;
        try
        {
            using (EnterTransientState(EngineState.Executing))
            {
                await using var context = new StepContext(this, step, path, scope, phase, iteration);
                outcome = await step.Action.ExecuteAsync(context).ConfigureAwait(false)
                          ?? StepOutcome.Error("Baustein lieferte kein Ergebnis.");
            }
        }
        catch (OperationCanceledException) when (phase.IsCancelled)
        {
            outcome = StepOutcome.Unrated(message: "Durch Werkerabbruch abgebrochen");
            cancelled = true;
        }
        catch (Exception ex)
        {
            outcome = StepOutcome.Error(ex.Message);
        }
        finally
        {
            PopFrame(step.Id);
        }

        Emit(new StepCompleted(outcome.Rating, outcome.Value, outcome.Unit, outcome.LowLimit, outcome.HighLimit, outcome.Message, cancelled)
        {
            Time = Clock.Now,
            RunId = RunId,
            NodeId = step.Id,
            Path = path,
            Iteration = iteration,
        });
        return outcome.Rating;
    }

    /// <summary>Versionswechsel ausschließlich vor Beginn eines Prüflingstests.</summary>
    private int _dutsCompleted;
    private int _activeDut;

    /// <summary>Abgeschlossene Prüflingstests dieses Laufs.</summary>
    public int DutsCompleted => Volatile.Read(ref _dutsCompleted);

    /// <summary>Vorbelegung: sichtbare Variablen gleichen Namens setzen (Text wird in den Variablentyp umgewandelt).</summary>
    private void ApplyPresets(GroupDefinition group, Scope scope, IReadOnlyDictionary<string, object?> presets)
    {
        foreach (var (name, value) in presets)
        {
            if (value is null || !scope.TryLookup(name, out var variable, out _)) continue;
            try
            {
                variable.Set(value is string text && variable.Type != VarType.String ? TypedValue.Parse(text, variable.Type) : value);
                Emit(new Diagnostic(group.Id, $"Aus der Auswahl übernommen: {name} = {TypedValue.Format(variable.Value)}") { Time = Clock.Now, RunId = RunId });
            }
            catch (Exception ex) when (ex is TypeMismatchException or FormatException or InvalidOperationException)
            {
                Emit(new Diagnostic(group.Id, $"Vorbelegung {name} = {value} nicht übernommen: {ex.Message}") { Time = Clock.Now, RunId = RunId });
            }
        }
    }

    private GroupDefinition ActivateVersionAtDutStart(GroupDefinition group)
    {
        if (Options.VersionSource?.NextDefinition(Definition) is { } next && !ReferenceEquals(next, Definition)
            && next.Find(group.Id) is GroupDefinition { IsDutRun: true })
        {
            var from = Definition.Version;
            Definition = next;
            Emit(new VersionActivated(from, next.Version) { Time = Clock.Now, RunId = RunId });
        }
        // Der Loop stammt ggf. noch aus der Vorversion: immer den Prüfling der aktiven Definition ausführen.
        return ReferenceEquals(Definition.Find(group.Id), group) ? group : Definition.Find(group.Id) as GroupDefinition ?? group;
    }

    private bool CanEnterGroup(GroupDefinition group, ExecutionPhase phase, string parentPath, int parentIteration, bool forceEnter, bool isExit)
    {
        if (phase.IsCancelled && !forceEnter) return false;
        if (group.IsDutRun && !isExit && Options.DutLimit is { } limit && Volatile.Read(ref _dutsCompleted) >= limit)
        {
            Emit(new GroupNotEntered($"Prüflingsanzahl {limit} erreicht") { Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = Combine(parentPath, group.Name), Iteration = parentIteration });
            return false;
        }
        return true;
    }

    private string? OrderOf(Scope scope)
        => Options.SessionPresets?.GetValueOrDefault("Auftrag") as string
            ?? (scope.TryLookup("Auftrag", out var order, out _) ? order.Value as string : null);

    private void ApplyDutPresets(GroupDefinition group, Scope scope, bool isExit)
    {
        if (group.IsDutRun && !isExit && Options.Presets is { Count: > 0 } presets) ApplyPresets(group, scope, presets);

    }

    private async Task<NodeResult?> ExecuteGroupAsync(
        GroupDefinition group, Scope? parentScope, ExecutionPhase phase, string parentPath, int parentIteration, bool forceEnter, bool isExit)
    {
        if (!CanEnterGroup(group, phase, parentPath, parentIteration, forceEnter, isExit)) return null;
        if (!group.IsDutRun) return await ExecuteGroupCoreAsync(group, parentScope, phase, parentPath, parentIteration, forceEnter, isExit).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _activeDut, 1, 0) != 0)
        {
            Emit(new Diagnostic(group.Id, "Ein Prüflingsdurchlauf ist bereits aktiv. Parallele Schritte innerhalb eines Prüflings oder getrennte Prüfplätze verwenden.") { Time = Clock.Now, RunId = RunId });
            return new NodeResult(ResultRating.Error, null);
        }
        try { return await ExecuteGroupCoreAsync(group, parentScope, phase, parentPath, parentIteration, forceEnter, isExit).ConfigureAwait(false); }
        finally { Volatile.Write(ref _activeDut, 0); }
    }

    private async Task<NodeResult?> ExecuteGroupCoreAsync(
        GroupDefinition group, Scope? parentScope, ExecutionPhase phase, string parentPath, int parentIteration, bool forceEnter, bool isExit)
    {
        if (!CanEnterGroup(group, phase, parentPath, parentIteration, forceEnter, isExit)) return null;
        if (group.IsDutRun && !isExit) group = ActivateVersionAtDutStart(group);
        var path = group == Definition.Root ? group.Name : Combine(parentPath, group.Name);
        var childPath = group == Definition.Root ? "" : path;

        var scope = InitializeScope();
        if (scope is null) return new NodeResult(ResultRating.Error, null);

        Scope? InitializeScope()
        {
            // 1. Scope initialisieren
            try
            {
                var initialized = Scope.CreateFor(group, parentScope);
                if (group == Definition.Root && Options.SessionPresets is { Count: > 0 } sessionPresets)
                    ApplyPresets(group, initialized, sessionPresets);
                if (group.Resources.Count > 0 && !initialized.TryGetLocal(ResourcesAcquiredVariable, out _))
                    initialized.Define(ResourcesAcquiredVariable, VarType.Bool, false);
                return initialized;
            }
            catch (Exception ex) when (ex is TypeMismatchException or InvalidOperationException or ArgumentException)
            {
                Emit(new Diagnostic(group.Id, $"Variableninitialisierung fehlgeschlagen: {ex.Message}") { Time = Clock.Now, RunId = RunId });
                return null;
            }

        }

        var (entered, entryResult) = EvaluateEntry();
        if (!entered) return entryResult;

        (bool Entered, NodeResult? Result) EvaluateEntry()
        {
            // 2. Eintrittsbedingung (in der Abbruchfolge für Exit-Gruppen ignoriert)
            if (!forceEnter && group.ParsedEntryCondition is { } condition)
            {
                bool enter;
                try
                {
                    enter = condition.EvaluateBool(scope);
                }
                catch (Exception ex) when (ex is ExpressionException or VariableNotFoundException or TypeMismatchException)
                {
                    scope.Discard();
                    Emit(new Diagnostic(group.Id, $"Bedingung '{condition.Source}': {ex.Message}") { Time = Clock.Now, RunId = RunId });
                    Emit(new GroupNotEntered($"Bedingungsfehler: {ex.Message}") { Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = path, Iteration = parentIteration });
                    return (false, new NodeResult(ResultRating.Error, null));
                }
                if (!enter)
                {
                    scope.Discard();
                    Emit(new GroupNotEntered($"Bedingung nicht erfüllt: {condition.Source}") { Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = path, Iteration = parentIteration });
                    return (false, null);
                }
            }

            return (true, null);
        }

        ApplyDutPresets(group, scope, isExit);

        PushFrame(new ExecutionFrame(group.Id, group.Name, path, parentIteration, isExit, true, scope.Id));
        Emit(new GroupEntered(scope.Id, isExit) { Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = path, Iteration = parentIteration });

        var accumulator = new ResultAccumulator();
        var dutRunId = group.IsDutRun ? $"PT-{Guid.NewGuid():N}"[..15] : null;
        var dutStarted = Clock.Now;
        var abortsAtEntry = _abort.AbortCount;
        if (dutRunId is not null)
            Emit(new DutRunStarted(dutRunId) { Order = OrderOf(scope), Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = path, Iteration = parentIteration });

        IResourceLease? lease = null;
        var iterations = 0;

        async Task<bool> AcquireGroupResourcesAsync()
        {
            // 3. Ressourcen atomar erwerben
            var regularAllowed = true;
            if (group.Resources.Count > 0)
            {
                Emit(new ResourceWaitStarted(group.Resources.ToList()) { Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = path, Iteration = parentIteration });
                try
                {
                    using (EnterTransientState(EngineState.WaitingResource))
                        lease = await Resources.AcquireAsync(new ResourceRequest(RunId, scope.Id, LockKeys(group.Resources), Options.StationName, group.Name) { Branch = ExecutionBranch.Value ?? "" },
                            group.ResourceTimeout, phase.Token).ConfigureAwait(false);
                    _scopeReservations[scope.Id] = lease;
                    scope.Set(ResourcesAcquiredVariable, true);
                    Emit(new ResourceAcquired(group.Resources.ToList()) { Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = path, Iteration = parentIteration });
                }
                catch (Exception ex) when (ex is ResourceTimeoutException or ResourceDeadlockException)
                {
                    regularAllowed = false;
                    accumulator.Merge(ResultRating.Error);
                    var reason = ex is ResourceTimeoutException
                        ? $"Zeitlimit {group.ResourceTimeout?.TotalSeconds:0.###} s beim Warten auf {string.Join(", ", group.Resources)} überschritten{Holders(group.Resources)}."
                        : ex.Message;
                    Emit(new ResourceNotAcquired(group.Resources.ToList(), reason) { Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = path, Iteration = parentIteration });
                }
                catch (OperationCanceledException) when (phase.IsCancelled)
                {
                    regularAllowed = false;
                    Emit(new ResourceNotAcquired(group.Resources.ToList(), "Werkerabbruch während des Wartens") { Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = path, Iteration = parentIteration });
                }
            }

            return regularAllowed;
        }

        try
        {
            var regularAllowed = await AcquireGroupResourcesAsync();
            // 4. Reguläre Wiederholungen
            if (regularAllowed)
                iterations = await RunIterationsAsync(group, scope, phase, accumulator, path, childPath).ConfigureAwait(false);

            await ExecuteExitGroupAsync().ConfigureAwait(false);
        }
        finally
        {
            if (group.IsDutRun) Processes.CloseOwner(scope.Id);
            // 6. Sperren freigeben (nach Exit), Prüflingstest abschließen, Scope verwerfen
            if (lease is not null)
            {
                Emit(new ResourceReleased(group.Resources.ToList()) { Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = path, Iteration = parentIteration });
                _scopeReservations.TryRemove(scope.Id, out _);
                lease.Dispose();
            }
        }

        if (dutRunId is not null)
        {
            CompleteDutRun();
        }

        async Task ExecuteExitGroupAsync()
        {
            // 5. Exit-Gruppe: nach allen Wiederholungen bzw. in der Abbruchfolge (dann ohne Eintrittsbedingung)
            if (group.ExitGroup is { } exitGroup)
            {
                var abortSequence = phase.IsCancelled;
                var exitPhase = _abort.PushExit(exitGroup.Id, exitGroup.Name);
                try
                {
                    await ExecuteNodeAsync(exitGroup, scope, exitPhase, accumulator, childPath, Math.Max(iterations, 1), forceEnter: abortSequence, isExit: true).ConfigureAwait(false);
                    if (exitPhase.IsCancelled)
                        Emit(new ExitGroupAborted { Time = Clock.Now, RunId = RunId, NodeId = exitGroup.Id, Path = Combine(childPath, exitGroup.Name), Iteration = iterations });
                }
                finally
                {
                    _abort.Pop(exitPhase); // kein Dispose: asynchrone Cancel-Callbacks könnten noch laufen
                }
            }
        }

        void CompleteDutRun()
        {
            var dutId = scope.TryLookup(group.DutIdVariable, out var idVar, out _) ? idVar.Value as string : null;
            if (string.IsNullOrWhiteSpace(dutId))
            {
                accumulator.Merge(ResultRating.Error);
                Emit(new Diagnostic(group.Id, $"Prüflingskennung '{group.DutIdVariable}' fehlt – Kennung ist verpflichtend.") { Time = Clock.Now, RunId = RunId });
            }
            Interlocked.Increment(ref _dutsCompleted);
            var werror = _abort.AbortCount > abortsAtEntry;
            Emit(new DutRunCompleted(dutRunId!, dutId ?? "", new RunStatus(accumulator.Rating, werror), dutStarted, Definition.Version)
            {
                Time = Clock.Now,
                RunId = RunId,
                NodeId = group.Id,
                Path = path,
                Iteration = parentIteration,
                Order = OrderOf(scope),
            });
        }

        Emit(new OperatorContentCleared(scope.Id) { Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = path, Iteration = parentIteration });
        var snapshot = scope.Snapshot();
        scope.Discard();
        PopFrame(group.Id);
        Emit(new GroupCompleted(accumulator.Rating, iterations, isExit) { Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = path, Iteration = parentIteration });
        return new NodeResult(accumulator.Rating, snapshot);
    }

    private async Task<int> RunIterationsAsync(GroupDefinition group, Scope scope, ExecutionPhase phase, ResultAccumulator accumulator, string path, string childPath)
    {
        var repetition = Options.ContinuousLoop && group == Definition.Loop ? Repetition.Endless : group.Repetition;
        var repeatExpression = repetition.Expression is { } e ? Expression.Parse(e) : null;
        var start = Clock.Now;
        var iteration = 0;

        async Task ExecuteIterationChildrenAsync()
        {
            if (group.Parallel)
            {
                await ExecuteParallelChildrenAsync(group, scope, phase, accumulator, childPath, iteration).ConfigureAwait(false);
                return;
            }
            var first = true;
            foreach (var child in group.Children)
            {
                if (phase.IsCancelled) break;
                if (!first && repetition is { Mode: RepeatMode.Duration, StopWithinIteration: true } && Clock.Now - start >= repetition.Duration)
                {
                    Emit(new Diagnostic(group.Id, $"Zeitspanne {repetition.Duration.TotalSeconds:0.###} s abgelaufen – restliche Schritte von Iteration {iteration} übersprungen.")
                    {
                        Time = Clock.Now,
                        RunId = RunId,
                    });
                    break;
                }
                first = false;
                await ExecuteNodeAsync(child, scope, phase, accumulator, childPath, iteration, forceEnter: false).ConfigureAwait(false);
            }

        }

        bool CanRepeat()
        {
            if (iteration > 0 && Options.DutLimit is { } dutLimit && Volatile.Read(ref _dutsCompleted) >= dutLimit) return false;
            if (iteration > 0)
            {
                var more = repetition.Mode switch
                {
                    RepeatMode.Once => false,
                    RepeatMode.Count => iteration < repetition.Count,
                    RepeatMode.Duration => Clock.Now - start < repetition.Duration,
                    _ => true,
                };
                if (!more) return false;
            }

            return true;
        }

        while (!phase.IsCancelled)
        {
            if (!CanRepeat()) break;

            if (repetition.Mode == RepeatMode.While)
            {
                if (!TryEvaluate(repeatExpression!, scope, group, accumulator, out var whileValue) || !whileValue) break;
            }

            if (iteration > 0)
            {
                if (group.ResultBeforeRepeat == RepeatResultPolicy.ResetToNResult) accumulator.Reset();
                await Task.Yield(); // Endlosschleifen ohne await dürfen den Thread nicht blockieren
            }

            iteration++;
            UpdateFrameIteration(group.Id, iteration);
            Emit(new GroupIterationStarted(iteration) { Time = Clock.Now, RunId = RunId, NodeId = group.Id, Path = path, Iteration = iteration });

            await ExecuteIterationChildrenAsync();

            if (repetition.Mode == RepeatMode.Until && !phase.IsCancelled)
            {
                if (!TryEvaluate(repeatExpression!, scope, group, accumulator, out var untilValue) || untilValue) break;
            }
        }
        return iteration;
    }

    private bool TryEvaluate(Expression expression, Scope scope, GroupDefinition group, ResultAccumulator accumulator, out bool value)
    {
        try
        {
            value = expression.EvaluateBool(scope);
            return true;
        }
        catch (Exception ex) when (ex is ExpressionException or VariableNotFoundException or TypeMismatchException)
        {
            value = false;
            accumulator.Merge(ResultRating.Error);
            Emit(new Diagnostic(group.Id, $"Wiederholungsausdruck '{expression.Source}': {ex.Message}") { Time = Clock.Now, RunId = RunId });
            return false;
        }
    }

    private static string Combine(string parentPath, string name) => string.IsNullOrEmpty(parentPath) ? name : $"{parentPath} > {name}";

    // ------------------------------------------------------------------ Zustand & Ereignisse

    internal void MarkDevelopmentRun() => IsDevelopmentRun = true;

    internal void Emit(EngineEvent e)
    {
        // Assign and deliver events in one order, without holding the execution-stack gate during callbacks.
        lock (_eventGate)
        {
            lock (_gate)
            {
                e.Sequence = ++_sequence;
                var historyEvent = !Options.RetainWaveformsInHistory && e is StepCompleted { Value: WaveformSet or Waveform } step
                    ? step with { Value = step.Value.ToString() } : e;
                _events.Add(historyEvent);
                if (_events.Count > Options.EventHistoryLimit)
                    _events.RemoveRange(0, _events.Count - Math.Max(0, Options.EventHistoryLimit));
            }
            EventRaised?.Invoke(e);
        }
    }

    internal IDisposable EnterTransientState(EngineState state)
    {
        lock (_gate) _transient.Add(state);
        PublishState();
        return new Callback(() =>
        {
            lock (_gate) _transient.Remove(state);
            PublishState();
        });
    }

    private void PushFrame(ExecutionFrame frame)
    {
        lock (_gate) _stack.Add(frame with { Branch = ExecutionBranch.Value ?? "" });
        PublishState();
    }

    private void PopFrame(string nodeId)
    {
        lock (_gate)
        {
            var index = _stack.FindLastIndex(f => f.NodeId == nodeId && f.Branch == (ExecutionBranch.Value ?? ""));
            if (index >= 0) _stack.RemoveAt(index);
        }
        PublishState();
    }

    private void UpdateFrameIteration(string nodeId, int iteration)
    {
        lock (_gate)
        {
            var index = _stack.FindLastIndex(f => f.NodeId == nodeId && f.Branch == (ExecutionBranch.Value ?? ""));
            if (index >= 0) _stack[index] = _stack[index] with { Iteration = iteration };
        }
    }

    private EngineState ComputeState()
    {
        if (_completed) return EngineState.Completed;
        if (_transient.Count > 0) return _transient[^1];
        if (_stack.Count == 0) return _started == 1 ? EngineState.Initializing : EngineState.Idle;
        if (_stack.Any(f => f.NodeId == Definition.Finalization.Id)) return EngineState.Finalizing;
        if (_stack.Any(f => f.IsExit)) return EngineState.Exiting;
        if (_stack.Any(f => f.NodeId == Definition.Init.Id)) return EngineState.Initializing;
        return EngineState.Testing;
    }

    private void PublishState()
    {
        EngineState state;
        lock (_gate)
        {
            state = ComputeState();
            if (state == _lastPublishedState) return;
            _lastPublishedState = state;
        }
        Emit(new StateChanged(state) { Time = Clock.Now, RunId = RunId });
    }

    private sealed class Callback(Action action) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) action();
        }
    }
}
