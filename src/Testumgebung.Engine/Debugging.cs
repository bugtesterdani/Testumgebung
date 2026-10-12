using System.Collections.Concurrent;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Scopes;

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
    private readonly ConcurrentDictionary<string, byte> _breakpoints = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private TaskCompletionSource<DebugCommand>? _waiting;
    private PauseInfo? _current;
    private Scope? _contextScope;

    /// <summary>Wird ausgelöst, sobald der Lauf an einem Haltepunkt pausiert.</summary>
    public event Action<PauseInfo>? Paused;

    internal TestRun? Run { get; set; }

    public IReadOnlyCollection<string> Breakpoints => _breakpoints.Keys.ToList();

    public PauseInfo? CurrentPause
    {
        get { lock (_gate) return _current; }
    }

    public bool IsPaused => CurrentPause is not null;

    public void SetBreakpoint(string nodeId, string user = "")
    {
        _breakpoints[nodeId] = 0;
        Run?.Emit(new DevelopmentEvent(user, "Breakpoint gesetzt", nodeId) { Time = Run.Clock.Now, RunId = Run.RunId });
    }

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
        if (HasBreakpoint(nodeId)) return true;
        var depth = DepthOf(path);
        return _stepMode switch
        {
            StepMode.Into => true,
            StepMode.Over => depth <= _stepDepth,
            StepMode.Out => depth < _stepDepth,
            _ => false,
        };
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
    private readonly object _codeGate = new();
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
        if (_codeBreakpoints.TryGetValue(nodeId, out var set)) lock (set) set.Remove(line);
    }

    public void ClearCodeBreakpoints(string nodeId) => _codeBreakpoints.TryRemove(nodeId, out _);

    public bool HasCodeBreakpoint(string nodeId, int line)
        => _codeBreakpoints.TryGetValue(nodeId, out var set) && set.Contains(line);

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

    private bool ShouldPauseCode(string nodeId, CodeDebugCapability capability, CodeDebugEvent e)
    {
        lock (_codeGate)
        {
            if (_codeStep) return true;
            if (e.Kind == "entry")
            {
                if (_codeStopOnEntry) return true;
                // Reduzierter Modus: ein beliebiger Haltepunkt am Baustein = Halt bei Eintritt (Zeilen gibt es nicht).
                return capability == CodeDebugCapability.EntryExitOnly
                       && _codeBreakpoints.TryGetValue(nodeId, out var s) && s.Count > 0;
            }
            if (e.Kind == "exit") return false;
        }
        return HasCodeBreakpoint(nodeId, e.Line);
    }

    /// <summary>
    /// Vom Host aufgerufen, wenn der Worker eine Nutzercode-Zeile meldet. Entscheidet über Anhalten,
    /// meldet die Pause und wartet auf die Steuerung; liefert Befehl und aktualisierten Haltepunktstand zurück.
    /// </summary>
    internal async Task<CodeDebugStop> OnCodeEventAsync(
        string nodeId, string nodeName, string path, int iteration, CodeDebugCapability capability,
        CodeDebugEvent e, CancellationToken abortToken)
    {
        IReadOnlyList<int> Bps() => [.. CodeBreakpoints(nodeId)];
        if (!ShouldPauseCode(nodeId, capability, e)) return new CodeDebugStop("continue", Bps(), Step: false);

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
