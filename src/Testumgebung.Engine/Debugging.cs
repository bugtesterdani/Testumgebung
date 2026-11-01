/**
 * @file
 * @brief Beschreibt Halteentscheidungen und sichtbare Variablen für Ablauf- und Code-Debugging.
 */

using System.Collections.Concurrent;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Scopes;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine;

public enum DebugCommand
{
    /// <summary>Fortsetzen: ausstehende Bewertung an die Eltern übergeben.</summary>
    Continue,
    /// <summary>Element erneut ausführen; die letzte Bewertung ersetzt die ausstehende.</summary>
    Rerun,
}

/// <summary>
/// Zustand am Haltepunkt (Abschnitt 6.2): Element ist ausgeführt, Ausgänge sind wirksam,
/// die Bewertung ist noch nicht an die Eltern übergeben.
/// </summary>
public sealed record PauseInfo(
    string NodeId,
    string NodeName,
    string Path,
    int Iteration,
    bool IsGroup,
    ResultRating PendingRating,
    ResultRating ParentRating,
    int ExecutionCount,
    /// <summary>Für Gruppen: lesbarer Schnappschuss der verlassenen lokalen Variablen (kein aktiver Scope).</summary>
    ScopeSnapshot? LeftScopeSnapshot);

/// <summary>Eine Variable im aktiven Debugkontext mit sichtbarem Herkunftsordner.</summary>
public sealed record DebugVariable(string Name, string LookupPath, VarType Type, object Value, string OriginGroup);

/// <summary>Eine im Code-Haltepunkt sichtbare Variable (lokaler Name und Wert, vom Worker erfasst).</summary>
public sealed record CodeDebugVariable(string Name, object? Value);

/// <summary>
/// Zustand an einem Code-Haltepunkt: erreichte Nutzercode-Zeile (1-basiert; 0 = Eintritt/Austritt im reduzierten Modus),
/// Art (line/entry/exit), Debug-Fähigkeit der Sprache und Variablen-Schnappschuss.
/// </summary>
public sealed record CodeLinePause(
    string NodeId,
    string NodeName,
    string Path,
    int Iteration,
    int Line,
    string Kind,
    CodeDebugCapability Capability,
    IReadOnlyList<CodeDebugVariable> Variables);

/// <summary>
/// Haltepunkte und Steuerung eines pausierten Laufs. Haltepunkte liegen ausschließlich vor der Bewertungsrückgabe.
/// </summary>
public sealed class DebugController
{
    private readonly ConcurrentDictionary<string, BreakpointState> _breakpoints = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private TaskCompletionSource<DebugCommand>? _waiting;
    private PauseInfo? _current;
    private Scope? _contextScope;

    /// <summary>Wird ausgelöst, sobald der Lauf an einem Haltepunkt pausiert.</summary>
    public event Action<PauseInfo>? Paused;

    private TestRun? _run;
    internal TestRun? Run
    {
        get => _run;
        set
        {
            _run = value;
            foreach (var state in _breakpoints.Values.Concat(_codeRules.Values)) state.Reset();
        }
    }

    public IReadOnlyCollection<string> Breakpoints => _breakpoints.Keys.ToList();

    public PauseInfo? CurrentPause
    {
        get { lock (_gate) return _current; }
    }

    public bool IsPaused => CurrentPause is not null;

    public void SetBreakpoint(string nodeId, string user = "")
    {
        _breakpoints[nodeId] = new(BreakpointRule.Default);
        Run?.Emit(new DevelopmentEvent(user, "Breakpoint gesetzt", nodeId) { Time = Run.Clock.Now, RunId = Run.RunId });
    }

    /// <summary>Ändert die Filter eines aktiven Haltepunkts und beginnt dessen Trefferzählung neu.</summary>
    public void ConfigureBreakpoint(string nodeId, BreakpointRule rule) => _breakpoints[nodeId] = new(rule);
    public BreakpointRule GetBreakpointRule(string nodeId) => _breakpoints.TryGetValue(nodeId, out var state) ? state.Rule : BreakpointRule.Default;

    public void ClearBreakpoint(string nodeId) => _breakpoints.TryRemove(nodeId, out _);

    public bool HasBreakpoint(string nodeId) => _breakpoints.ContainsKey(nodeId);

    public void Continue() => Release(DebugCommand.Continue);

    // ------------------------------------------------------------------ Einzelschritt (Abschnitt 15)

    private StepMode _stepMode;
    private int _stepDepth;

    /// <summary>Schachtelungstiefe eines Ablaufpfads („A &gt; B &gt; C“ = 2).</summary>
    public static int DepthOf(string path) => path.Split(" > ").Length - 1;

    /// <summary>
    /// Einzelschritt: fortsetzen und nach dem nächsten ausgeführten Element wieder anhalten (auch innerhalb einer Gruppe).
    /// Weil Haltepunkte nach der Ausführung liegen, hält „Hinein“ am ersten fertigen Kind der nächsten Gruppe.
    /// </summary>
    public void StepInto() => Step(StepMode.Into);

    /// <summary>Prozedurschritt: nach dem nächsten Element derselben oder einer äußeren Ebene anhalten.</summary>
    public void StepOver() => Step(StepMode.Over);

    /// <summary>Bis zum Ende der umgebenden Gruppe ausführen und dort anhalten.</summary>
    public void StepOut() => Step(StepMode.Out);

    private void Step(StepMode mode)
    {
        var pause = CurrentPause;
        if (pause is null) return;
        _stepDepth = DepthOf(pause.Path);
        _stepMode = mode;
        Release(DebugCommand.Continue);
    }

    /// <summary>Haltepunkt oder fälliger Einzelschritt nach Abschluss dieses Elements.</summary>
    public bool ShouldPause(string nodeId, string path)
    {
        return HasBreakpoint(nodeId) || StepDue(path);
    }

    internal bool ShouldPause(PauseInfo info, Scope scope)
    {
        var configured = _breakpoints.TryGetValue(info.NodeId, out var state)
            && EvaluateRule(state, info.NodeId, info.Iteration, info.PendingRating, name => ResolveNode(name, scope, info.LeftScopeSnapshot));
        return configured || StepDue(info.Path);
    }
    private bool StepDue(string path)
    {
        var depth = DepthOf(path);
        return _stepMode switch { StepMode.Into => true, StepMode.Over => depth <= _stepDepth, StepMode.Out => depth < _stepDepth, _ => false };
    }
    private static object ResolveNode(string name, Scope parent, ScopeSnapshot? left)
    {
        if (left is null) return parent.Get(name);
        if (name.StartsWith("../", StringComparison.Ordinal)) return parent.Get(name[3..]);
        return left.Variables.FirstOrDefault(v => v.Name == name)?.Value ?? parent.Get(name);
    }
    private bool EvaluateRule(BreakpointState state, string nodeId, int iteration, ResultRating? rating, Func<string, object> resolve)
    {
        try { return state.Evaluate(iteration, rating, resolve); }
        catch (Exception ex) when (ex is Domain.Expressions.ExpressionException or Domain.Scopes.VariableNotFoundException or TypeMismatchException or KeyNotFoundException)
        {
            // A mistyped condition must be visible, not silently suppress a requested stop.
            if (Run is { } run) run.Emit(new Diagnostic(nodeId, "Haltebedingung nicht auswertbar; halte zur Prüfung: " + ex.Message) { Time = run.Clock.Now, RunId = run.RunId });
            return true;
        }
    }

    /// <summary>Beim Anhalten Einzelschritt zurücksetzen.</summary>
    internal void ClearStep() => _stepMode = StepMode.None;

    private enum StepMode
    {
        None,
        Into,
        Over,
        Out,
    }

    public void Rerun() => Release(DebugCommand.Rerun);

    // ================================================================== Zeilen-Debugger für CodeBlocks (ADR 0017)

    private readonly ConcurrentDictionary<string, HashSet<int>> _codeBreakpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Node, int Line), BreakpointState> _codeRules = new();
    private readonly object _codeGate = new();
    private readonly SemaphoreSlim _codePauseSerial = new(1, 1);
    private bool _codeStopOnEntry;
    private bool _codeStep;
    private TaskCompletionSource<string>? _codeWaiting;
    private CodeLinePause? _codeCurrent;

    /// <summary>Wird ausgelöst, sobald der Code an einer Zeile (bzw. bei Eintritt/Austritt) pausiert.</summary>
    public event Action<CodeLinePause>? CodeLinePaused;

    public CodeLinePause? CurrentCodePause
    {
        get { lock (_codeGate) return _codeCurrent; }
    }

    public bool IsCodePaused => CurrentCodePause is not null;

    /// <summary>Bei Eintritt jedes CodeBlocks anhalten (gilt für alle Sprachen, auch den reduzierten Modus).</summary>
    public bool StopOnCodeEntry
    {
        get { lock (_codeGate) return _codeStopOnEntry; }
        set { lock (_codeGate) _codeStopOnEntry = value; }
    }

    public IReadOnlyCollection<int> CodeBreakpoints(string nodeId)
    {
        if (_codeBreakpoints.TryGetValue(nodeId, out var set)) lock (set) return set.ToList();
        return [];
    }

    public void SetCodeBreakpoint(string nodeId, int line)
    {
        var set = _codeBreakpoints.GetOrAdd(nodeId, _ => []);
        lock (set) set.Add(line);
    }

    public void ClearCodeBreakpoint(string nodeId, int line)
    {
        _codeRules.TryRemove((nodeId, line), out _);
        if (_codeBreakpoints.TryGetValue(nodeId, out var set)) lock (set) set.Remove(line);
    }

    public void ClearCodeBreakpoints(string nodeId)
    {
        _codeBreakpoints.TryRemove(nodeId, out _);
        foreach (var key in _codeRules.Keys.Where(k => k.Node == nodeId)) _codeRules.TryRemove(key, out _);
    }
    public void ConfigureCodeBreakpoint(string nodeId, int line, BreakpointRule rule)
    {
        if (line < 1) throw new ArgumentOutOfRangeException(nameof(line));
        var state = new BreakpointState(rule, code: true);
        SetCodeBreakpoint(nodeId, line);
        _codeRules[(nodeId, line)] = state;
    }
    public BreakpointRule GetCodeBreakpointRule(string nodeId, int line)
        => _codeRules.TryGetValue((nodeId, line), out var state) ? state.Rule : BreakpointRule.Default;

    public bool HasCodeBreakpoint(string nodeId, int line)
    {
        if (!_codeBreakpoints.TryGetValue(nodeId, out var set)) return false;
        lock (set) return set.Contains(line);
    }

    /// <summary>Fortsetzen bis zum nächsten Haltepunkt.</summary>
    public void CodeContinue() => ReleaseCode("continue");

    /// <summary>Einzelschritt: an der nächsten Nutzercode-Zeile (bzw. beim Austritt im reduzierten Modus) anhalten.</summary>
    public void CodeStepLine() => ReleaseCode("step");

    /// <summary>Ausführung des Codes abbrechen.</summary>
    public void CodeStop() => ReleaseCode("stop");

    private void ReleaseCode(string command)
    {
        TaskCompletionSource<string>? tcs;
        lock (_codeGate) tcs = _codeWaiting;
        if (tcs is null) throw new InvalidOperationException("Code ist nicht pausiert.");
        tcs.TrySetResult(command);
    }

    /// <summary>Startkonfiguration für einen CodeBlock-Aufruf: aktiv, wenn Haltepunkte bestehen oder bei Eintritt gehalten wird.</summary>
    public CodeDebugConfig CodeConfigFor(string nodeId)
    {
        var breakpoints = CodeBreakpoints(nodeId);
        bool entry;
        lock (_codeGate) entry = _codeStopOnEntry;
        if (breakpoints.Count == 0 && !entry) return CodeDebugConfig.Off;
        return new CodeDebugConfig(true, [.. breakpoints], entry, Step: false);
    }

    private bool ShouldPauseCode(string nodeId, int iteration, CodeDebugCapability capability, CodeDebugEvent e)
    {
        lock (_codeGate)
        {
            if (_codeStep) return true;
            if (e.Kind == "entry")
            {
                if (_codeStopOnEntry) return true;
                // Reduzierter Modus: ein beliebiger Haltepunkt am Baustein = Halt bei Eintritt (Zeilen gibt es nicht).
                return capability == CodeDebugCapability.EntryExitOnly && MatchReducedCode(nodeId, iteration, e);
            }
            if (e.Kind == "exit") return false;
        }
        return HasCodeBreakpoint(nodeId, e.Line) && MatchCodeRule(nodeId, e.Line, iteration, e);
    }

    private bool MatchReducedCode(string nodeId, int iteration, CodeDebugEvent e)
    {
        var stop = false;
        foreach (var line in CodeBreakpoints(nodeId)) stop |= MatchCodeRule(nodeId, line, iteration, e);
        return stop;
    }
    private bool MatchCodeRule(string nodeId, int line, int iteration, CodeDebugEvent e)
        => EvaluateRule(_codeRules.GetOrAdd((nodeId, line), _ => new(BreakpointRule.Default)), nodeId, iteration, null,
            name => e.Variables.TryGetValue(name, out var value) && value is not null ? value : throw new KeyNotFoundException("Codevariable '" + name + "' nicht im Schnappschuss vorhanden."));

    /// <summary>
    /// Vom Host aufgerufen, wenn der Worker eine Nutzercode-Zeile meldet. Entscheidet über Anhalten,
    /// meldet die Pause und wartet auf die Steuerung; liefert Befehl und aktualisierten Haltepunktstand zurück.
    /// </summary>
    internal async Task<CodeDebugStop> OnCodeEventAsync(
        string nodeId, string nodeName, string path, int iteration, CodeDebugCapability capability,
        CodeDebugEvent e, CancellationToken abortToken)
    {
        e = await NativeBreakpointContext.PrepareAsync(e, GetCodeBreakpointRule(nodeId, e.Line).Mode == BreakpointMode.Expression, abortToken).ConfigureAwait(false);
        if (!ShouldPauseCode(nodeId, iteration, capability, e)) return new("continue", [.. CodeBreakpoints(nodeId)], Step: false);
        await _codePauseSerial.WaitAsync(abortToken).ConfigureAwait(false);
        try { return await OnCodeEventCoreAsync(nodeId, nodeName, path, iteration, capability, e, abortToken).ConfigureAwait(false); }
        finally { _codePauseSerial.Release(); }
    }

    private async Task<CodeDebugStop> OnCodeEventCoreAsync(
        string nodeId, string nodeName, string path, int iteration, CodeDebugCapability capability,
        CodeDebugEvent e, CancellationToken abortToken)
    {
        IReadOnlyList<int> Bps() => [.. CodeBreakpoints(nodeId)];

        var variables = e.Variables
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => new CodeDebugVariable(v.Key, v.Value))
            .ToList();
        if (e.InspectNativeAsync is not null && e.Kind == "line")
        {
            var native = await e.InspectNativeAsync(abortToken).ConfigureAwait(false);
            foreach (var (name, value) in native.Variables) variables.Add(new CodeDebugVariable("GDB: " + name, value));
            if (native.Frames.Count > 0) variables.Add(new CodeDebugVariable("GDB: Callstack", string.Join(Environment.NewLine, native.Frames)));
            if (native.Error is not null) variables.Add(new CodeDebugVariable("GDB: Diagnose", native.Error));
        }
        var pause = new CodeLinePause(nodeId, nodeName, path, iteration, e.Line, e.Kind, capability, variables);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_codeGate)
        {
            _codeStep = false; // beim Anhalten zurücksetzen; die nächste Aktion entscheidet neu
            _codeWaiting = tcs;
            _codeCurrent = pause;
        }
        CodeLinePaused?.Invoke(pause);
        using var reg = abortToken.Register(() => tcs.TrySetResult("stop"));
        var command = await tcs.Task.ConfigureAwait(false);
        bool step = command == "step";
        lock (_codeGate)
        {
            _codeStep = step;
            _codeWaiting = null;
            _codeCurrent = null;
        }
        if (Run is { } run)
            run.Emit(new DevelopmentEvent("", "Code-Haltepunkt", $"{nodeName}:{e.Line} → {command}") { Time = run.Clock.Now, RunId = run.RunId });
        return new CodeDebugStop(command, Bps(), step);
    }

    /// <summary>Aktive Variablen im pausierten Kontext (Verdeckung berücksichtigt) plus explizite <c>../</c>-Sicht auf Verdecktes.</summary>
    public IReadOnlyList<DebugVariable> ActiveVariables()
    {
        Scope? scope;
        lock (_gate) scope = _current is null ? null : _contextScope;
        if (scope is null) return [];

        var result = new List<DebugVariable>();
        var prefix = "";
        for (var s = scope; s is not null; s = s.Parent)
        {
            foreach (var v in s.Variables.OrderBy(v => v.Name, StringComparer.Ordinal))
            {
                var visible = scope.TryLookup(v.Name, out var found, out _) && ReferenceEquals(found, v);
                var path = visible ? v.Name : prefix + v.Name;
                result.Add(new DebugVariable(v.Name, path, v.Type, v.Value, s.GroupName));
            }
            if (s.IsInterfaceBoundary) break;
            prefix += "../";
        }
        return result;
    }

    /// <summary>
    /// Aktive Variable im pausierten Lauf ändern: Typprüfung, protokolliert, Lauf wird Entwicklungslauf.
    /// </summary>
    /// <exception cref="TypeMismatchException">Text passt nicht zum Variablentyp.</exception>
    public void SetVariable(string lookupPath, string text, string user)
    {
        Scope scope;
        lock (_gate)
        {
            if (_current is null || _contextScope is null) throw new InvalidOperationException("Lauf ist nicht pausiert.");
            scope = _contextScope;
        }
        var variable = scope.Lookup(lookupPath);
        var newValue = TypedValue.Parse(text, variable.Type);
        var old = variable.Value;
        variable.Set(newValue);
        if (Run is { } run)
        {
            run.MarkDevelopmentRun();
            run.Emit(new DevelopmentEvent(user, "Variable geändert",
                $"{lookupPath} = {TypedValue.Format(variable.Value)} (vorher: {TypedValue.Format(old)}) in {variable.Owner.GroupName}")
            { Time = run.Clock.Now, RunId = run.RunId });
        }
    }

    /// <summary>
    /// Pause vorbereiten, bevor das Paused-Ereignis gemeldet wird: Wer auf das Ereignis reagiert
    /// (Fortsetzen, Wiederholen), findet den Lauf damit bereits wartend vor.
    /// </summary>
    internal TaskCompletionSource<DebugCommand> BeginPause(PauseInfo info, Scope contextScope)
    {
        var tcs = new TaskCompletionSource<DebugCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiting = tcs;
            _current = info;
            _contextScope = contextScope;
        }
        return tcs;
    }

    internal async Task<DebugCommand> WaitAsync(TaskCompletionSource<DebugCommand> tcs, PauseInfo info, CancellationToken abortToken)
    {
        Paused?.Invoke(info);

        // Werkerabbruch während der Pause: fortsetzen (Bewertung übergeben), danach greift der Abbruch.
        using var reg = abortToken.Register(() => tcs.TrySetResult(DebugCommand.Continue));
        var command = await tcs.Task.ConfigureAwait(false);
        lock (_gate)
        {
            _waiting = null;
            _current = null;
            _contextScope = null;
        }
        return command;
    }

    private void Release(DebugCommand command)
    {
        TaskCompletionSource<DebugCommand>? tcs;
        lock (_gate) tcs = _waiting;
        if (tcs is null) throw new InvalidOperationException("Lauf ist nicht pausiert.");
        tcs.TrySetResult(command);
    }
}
