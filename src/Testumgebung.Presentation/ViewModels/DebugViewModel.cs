/**
 * @file
 * @brief Stellt pausierte Ausführung, Variablenbearbeitung und sichere Run-Patches in der Oberfläche dar.
 */

using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Scopes;
using Testumgebung.Engine;
using PatchChange = Testumgebung.Engine.PatchChange;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Aktive Variable im pausierten Kontext – editierbar mit Typprüfung, Herkunftsordner sichtbar.</summary>
public sealed class DebugVariableRowViewModel : ObservableObject
{
    private readonly DebugViewModel _owner;
    private string _valueText;
    private string? _error;

    public DebugVariableRowViewModel(DebugViewModel owner, DebugVariable variable)
    {
        _owner = owner;
        Variable = variable;
        _valueText = DebugViewModel.FormatValue(variable.Value);
        CommitCommand = new RelayCommand(Commit);
    }

    public DebugVariable Variable { get; }
    public string Name => Variable.LookupPath;
    public string Type => Variable.Type.ToString().ToLowerInvariant();
    public string Origin => Variable.OriginGroup;
    public bool IsShadowed => Variable.LookupPath.StartsWith("../", StringComparison.Ordinal);
    public RelayCommand CommitCommand { get; }

    public string ValueText
    {
        get => _valueText;
        set
        {
            if (SetProperty(ref _valueText, value)) Error = null;
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            SetProperty(ref _error, value);
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => Error ?? "OK";

    /// <summary>Änderung übernehmen (Enter / Fokusverlust).</summary>
    public void Commit()
    {
        if (ValueText == DebugViewModel.FormatValue(Variable.Value)) return;
        Error = _owner.TrySetVariable(Variable.LookupPath, ValueText);
    }
}

public sealed record DevelopmentLogEntry(DateTimeOffset Time, string User, string Kind, string Details)
{
    public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>Debugger eines Prüfplatzes: Haltepunkte, Pause vor Bewertungsrückgabe, Variablen, gezielte Ausführung.</summary>
public sealed class DebugViewModel : ObservableObject
{
    private readonly StationViewModel _station;
    private readonly IUiDispatcher _ui;
    private readonly Func<string> _user;
    private readonly Dictionary<string, StepCompleted> _lastStep = new(StringComparer.Ordinal);
    private PauseInfo? _pause;
    private string _contextText = "";
    private string? _targetResult;
    private string? _lastMarkedNode;

    public DebugViewModel(StationViewModel station, IUiDispatcher ui, Func<string> user)
    {
        _station = station;
        _ui = ui;
        _user = user;
        Patch = new PatchEditorViewModel(station, user);
        ContinueCommand = new RelayCommand(() => Controller.Continue(), () => IsPaused);
        RerunCommand = new RelayCommand(() => Controller.Rerun(), () => IsPaused);
        StepIntoCommand = new RelayCommand(() => Controller.StepInto(), () => IsPaused);
        StepOverCommand = new RelayCommand(() => Controller.StepOver(), () => IsPaused);
        StepOutCommand = new RelayCommand(() => Controller.StepOut(), () => IsPaused);
        CodeContinueCommand = new RelayCommand(() => { Controller.CodeContinue(); ClearCodePause(); }, () => IsCodePaused);
        CodeStepCommand = new RelayCommand(() => Controller.CodeStepLine(), () => IsCodePaused);
        CodeStopCommand = new RelayCommand(() => { Controller.CodeStop(); ClearCodePause(); }, () => IsCodePaused);
        Controller.CodeLinePaused += p => _ui.Post(() => OnCodePaused(p));
    }

    public DebugController Controller { get; } = new();

    /// <summary>Run-Patch-Editor (nur im pausierten Lauf).</summary>
    public PatchEditorViewModel Patch { get; }
    public RelayCommand ContinueCommand { get; }
    public RelayCommand RerunCommand { get; }

    /// <summary>Einzelschritt (F11), Prozedurschritt (F10), bis Gruppenende (Umschalt+F11).</summary>
    public RelayCommand StepIntoCommand { get; }
    public RelayCommand StepOverCommand { get; }
    public RelayCommand StepOutCommand { get; }

    public ObservableCollection<DebugVariableRowViewModel> Variables { get; } = [];
    public ObservableCollection<VariableSnapshot> Snapshot { get; } = [];
    public ObservableCollection<DevelopmentLogEntry> DevelopmentLog { get; } = [];

    // ------------------------------------------------------------------ Zeilen-Debugger für CodeBlocks (ADR 0017)

    private CodeLinePause? _codePause;

    /// <summary>Fortsetzen / Einzelschritt (nächste Zeile) / Stoppen im Nutzercode eines CodeBlocks.</summary>
    public RelayCommand CodeContinueCommand { get; }
    public RelayCommand CodeStepCommand { get; }
    public RelayCommand CodeStopCommand { get; }

    /// <summary>Am Code-Haltepunkt sichtbare lokale Variablen (schreibgeschützt; vom Worker erfasst).</summary>
    public ObservableCollection<CodeDebugVariable> CodeVariables { get; } = [];

    public bool IsCodePaused => _codePause is not null;
    public int CodeLine => _codePause?.Line ?? 0;
    public string CodeNodeName => _codePause?.NodeName ?? "";

    public string CodePositionText => _codePause is null
        ? ""
        : _codePause.Kind switch
        {
            "entry" => $"{_codePause.NodeName}: Eintritt",
            "exit" => $"{_codePause.NodeName}: Austritt",
            _ => $"{_codePause.NodeName}: Zeile {_codePause.Line}",
        };

    /// <summary>Hinweis, wie tief die Sprache debuggbar ist (einheitliche Oberfläche, je Sprache unterschiedliche Tiefe).</summary>
    public string CodeCapabilityText => _codePause?.Capability switch
    {
        Testumgebung.Domain.Code.CodeDebugCapability.FullLineStepping => "Zeilen-Stepping",
        Testumgebung.Domain.Code.CodeDebugCapability.EntryExitOnly => "Nur Eintritt/Austritt (reduziert)",
        _ => "",
    };

    /// <summary>Bei Eintritt jedes CodeBlocks anhalten (gilt für alle Sprachen).</summary>
    public bool StopOnCodeEntry
    {
        get => Controller.StopOnCodeEntry;
        set { if (Controller.StopOnCodeEntry != value) { Controller.StopOnCodeEntry = value; OnPropertyChanged(); } }
    }

    /// <summary>Zeilen-Haltepunkt eines CodeBlocks umschalten (Zeile 1-basiert).</summary>
    public void ToggleCodeBreakpoint(string nodeId, int line)
    {
        if (Controller.HasCodeBreakpoint(nodeId, line)) Controller.ClearCodeBreakpoint(nodeId, line);
        else Controller.SetCodeBreakpoint(nodeId, line);
    }

    public bool HasCodeBreakpoint(string nodeId, int line) => Controller.HasCodeBreakpoint(nodeId, line);

    private void OnCodePaused(CodeLinePause pause)
    {
        _codePause = pause;
        CodeVariables.Clear();
        foreach (var v in pause.Variables) CodeVariables.Add(v);
        Mark(pause.NodeId, RunMarker.Paused);
        OnPropertiesChanged(nameof(IsCodePaused), nameof(CodeLine), nameof(CodeNodeName), nameof(CodePositionText), nameof(CodeCapabilityText));
        CodeContinueCommand.RaiseCanExecuteChanged();
        CodeStepCommand.RaiseCanExecuteChanged();
        CodeStopCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Nach Fortsetzen/Stoppen den Code-Haltepunktzustand zurücksetzen (von der Shell beim Resumed-Ereignis aufgerufen).</summary>
    public void ClearCodePause()
    {
        if (_codePause is null) return;
        _codePause = null;
        CodeVariables.Clear();
        OnPropertiesChanged(nameof(IsCodePaused), nameof(CodeLine), nameof(CodeNodeName), nameof(CodePositionText), nameof(CodeCapabilityText));
        CodeContinueCommand.RaiseCanExecuteChanged();
        CodeStepCommand.RaiseCanExecuteChanged();
        CodeStopCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Wird von der Shell gesetzt, um Karten im Editor zu markieren.</summary>
    public Action<string, RunMarker>? MarkNode { get; set; }

    public bool IsPaused => _pause is not null;

    /// <summary>Im Editor gewähltes Element; beim Pausieren Ziel des Patch-Editors.</summary>
    public string? PatchNodeId { get; private set; }

    public void SelectPatchNode(string? nodeId)
    {
        PatchNodeId = nodeId;
        if (IsPaused) Patch.Select(nodeId ?? _pause!.NodeId);
    }
    public PauseInfo? Pause => _pause;
    public string NodeName => _pause?.NodeName ?? "";
    public IReadOnlyList<string> Breadcrumb => _pause?.Path.Split(" > ") ?? [];
    public ResultRating PendingRating => _pause?.PendingRating ?? ResultRating.NResult;
    public ResultRating ParentRating => _pause?.ParentRating ?? ResultRating.NResult;
    public bool IsGroupPause => _pause?.IsGroup ?? false;

    public string PositionText => _pause is null
        ? ""
        : $"Iteration {_pause.Iteration} • Ausführung {_pause.ExecutionCount} • {(_pause.IsGroup ? "Gruppe inkl. Exit fertig" : "POST-Ausführung (Haltepunkt)")}";

    /// <summary>„Ausgänge bereits wirksam“: Wert des pausierten Schritts.</summary>
    public string OutputText
    {
        get
        {
            if (_pause is null) return "";
            if (_pause.IsGroup) return $"{_pause.LeftScopeSnapshot?.Variables.Count ?? 0} lokale Variablen (Schnappschuss)";
            return _lastStep.TryGetValue(_pause.NodeId, out var s)
                ? $"{_pause.NodeName} = {FormatValue(s.Value)}{(s.Unit is null ? "" : " " + s.Unit)}"
                : "—";
        }
    }

    public string ContextText
    {
        get => _contextText;
        set => SetProperty(ref _contextText, value);
    }

    public string? TargetResult
    {
        get => _targetResult;
        private set => SetProperty(ref _targetResult, value);
    }

    public bool CanExecuteTargeted => !_station.IsRunning;

    internal void RaiseRunState() => OnPropertyChanged(nameof(CanExecuteTargeted));

    internal void Handle(EngineEvent e)
    {
        switch (e)
        {
            case StepCompleted sc:
                _lastStep[sc.NodeId] = sc;
                Mark(sc.NodeId, RunMarker.Done);
                break;
            case StepStarted st:
                Mark(st.NodeId, RunMarker.Running);
                break;
            case Paused p:
                _pause = p.Info;
                Mark(p.Info.NodeId, RunMarker.Paused);
                RefreshVariables();
                Snapshot.Clear();
                foreach (var v in p.Info.LeftScopeSnapshot?.Variables ?? []) Snapshot.Add(v);
                Patch.Select(PatchNodeId ?? p.Info.NodeId);
                RaisePause();
                break;
            case Resumed:
                if (_pause is not null) Mark(_pause.NodeId, RunMarker.Done);
                _pause = null;
                Variables.Clear();
                Patch.Select(null);
                RaisePause();
                break;
            case DevelopmentEvent d:
                DevelopmentLog.Insert(0, new DevelopmentLogEntry(d.Time, d.User, d.Kind, d.Details));
                break;
        }
    }

    private void Mark(string nodeId, RunMarker marker)
    {
        if (marker == RunMarker.Running && _lastMarkedNode is not null && _lastMarkedNode != nodeId)
            MarkNode?.Invoke(_lastMarkedNode, RunMarker.None);
        _lastMarkedNode = nodeId;
        MarkNode?.Invoke(nodeId, marker);
    }

    private void RaisePause()
    {
        OnPropertiesChanged(nameof(IsPaused), nameof(Pause), nameof(NodeName), nameof(Breadcrumb), nameof(PendingRating),
            nameof(ParentRating), nameof(IsGroupPause), nameof(PositionText), nameof(OutputText));
        ContinueCommand.RaiseCanExecuteChanged();
        RerunCommand.RaiseCanExecuteChanged();
        StepIntoCommand.RaiseCanExecuteChanged();
        StepOverCommand.RaiseCanExecuteChanged();
        StepOutCommand.RaiseCanExecuteChanged();
    }

    public void RefreshVariables()
    {
        Variables.Clear();
        foreach (var v in Controller.ActiveVariables()) Variables.Add(new DebugVariableRowViewModel(this, v));
    }

    /// <summary>Setzt eine Variable; liefert Fehlertext oder null.</summary>
    internal string? TrySetVariable(string path, string text)
    {
        try
        {
            Controller.SetVariable(path, text, _user());
            _ui.Post(RefreshVariables);
            return null;
        }
        catch (Exception ex) when (ex is TypeMismatchException or VariableNotFoundException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Gezielte Ausführung eines Schritts oder einer Gruppe (Abschnitt 6.3) außerhalb eines laufenden Tests.
    /// Fehlender Kontext als „Name = Wert; …“.
    /// </summary>
    public async Task ExecuteTargetedAsync(string nodeId, Func<TestRun> createRun)
    {
        if (_station.IsRunning)
        {
            TargetResult = "Nur möglich, solange kein Test läuft.";
            return;
        }
        Dictionary<string, object> context;
        try
        {
            context = ParseContext(ContextText);
        }
        catch (ExpressionException ex)
        {
            TargetResult = $"Kontext ungültig: {ex.Message}";
            return;
        }

        var run = createRun();
        run.EventRaised += e => _ui.Post(() => Handle(e));
        try
        {
            var isGroup = run.Definition.Find(nodeId) is Domain.Definitions.GroupDefinition;
            var result = isGroup
                ? await run.ExecuteGroupTargetedAsync(nodeId, context, _user()).ConfigureAwait(false)
                : await run.ExecuteStepTargetedAsync(nodeId, context, _user()).ConfigureAwait(false);
            var text = result.Executed
                ? $"Ausgeführt: {result.Rating.ToDisplay()}" + (_lastStepText(run, nodeId) is { } v ? $" ({v})" : "")
                : $"Verweigert – {result.RefusalReason}";
            _ui.Post(() => TargetResult = text);
        }
        catch (Exception ex) when (ex is TypeMismatchException or VariableNotFoundException or KeyNotFoundException)
        {
            _ui.Post(() => TargetResult = $"Kontext ungültig: {ex.Message}");
        }
    }

    private static string? _lastStepText(TestRun run, string nodeId)
    {
        var s = run.Events.OfType<StepCompleted>().LastOrDefault(e => e.NodeId == nodeId);
        if (s is null) return null;
        var text = FormatValue(s.Value) + (s.Unit is null ? "" : " " + s.Unit);
        return s.Message is null ? text : $"{text}; {s.Message}";
    }

    internal static Dictionary<string, object> ParseContext(string text)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var part in text.Split([';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) throw new ExpressionException($"'{part}': Format Name = Wert erwartet.");
            var name = part[..eq].Trim();
            var valueText = part[(eq + 1)..].Trim();
            object value;
            try
            {
                value = Expression.Parse(valueText).Evaluate(EmptyResolver.Instance);
            }
            catch (ExpressionException)
            {
                value = valueText; // unquotierter Text
            }
            result[name] = value;
        }
        return result;
    }

    internal static string FormatValue(object? value) => value switch
    {
        null => "—",
        double d => d.ToString("0.###", CultureInfo.GetCultureInfo("de-DE")),
        string s => s,
        _ => TypedValue.Format(value),
    };

    private sealed class EmptyResolver : IValueResolver
    {
        public static EmptyResolver Instance { get; } = new();

        public object Resolve(string name) => throw new ExpressionException($"Unbekannter Name '{name}'.");
    }
}

/// <summary>Bearbeitbarer Wert eines Run-Patches.</summary>
public sealed class PatchRowViewModel(string property, string label, Engine.Blocks.ParameterKind kind, string original, IReadOnlyList<string>? choices = null) : ObservableObject
{
    private string _value = original;

    public string Property { get; } = property;
    public string Label { get; } = label;
    public string Original { get; } = original;
    public IReadOnlyList<string> Choices { get; } = choices ?? [];
    public bool IsCode => kind == Engine.Blocks.ParameterKind.Code;
    public bool IsChoice => kind == Engine.Blocks.ParameterKind.Choice;
    public bool IsText => !IsCode && !IsChoice;
    public bool IsChanged => _value != Original;

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value)) OnPropertyChanged(nameof(IsChanged));
        }
    }
}

/// <summary>
/// Run-Patch im pausierten Lauf (Abbildung 8): Code und Parameter noch nicht ausgeführter Elemente ändern,
/// prüfen und übernehmen. Gilt nur für diesen Lauf, nicht für den Projektentwurf.
/// </summary>
public sealed class PatchEditorViewModel : ObservableObject
{
    private readonly StationViewModel _station;
    private readonly Func<string> _user;
    private string? _nodeId;
    private string _title = "";
    private string? _status;
    private bool? _valid;

    public PatchEditorViewModel(StationViewModel station, Func<string> user)
    {
        _station = station;
        _user = user;
        ValidateCommand = new RelayCommand(() => Validate(), () => HasTarget);
        ApplyCommand = new RelayCommand(() => Apply(), () => HasTarget);
    }

    public ObservableCollection<PatchRowViewModel> Rows { get; } = [];
    public RelayCommand ValidateCommand { get; }
    public RelayCommand ApplyCommand { get; }

    public bool HasTarget => _nodeId is not null && Rows.Count > 0;
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool? Valid { get => _valid; private set => SetProperty(ref _valid, value); }
    public int PatchCount => _station.Run?.PatchCount ?? 0;

    /// <summary>Element des laufenden Ablaufs zum Patchen wählen (Kopie im Lauf, nicht der Entwurf).</summary>
    public void Select(string? nodeId)
    {
        Rows.Clear();
        _nodeId = null;
        Status = null;
        Valid = null;
        if (nodeId is null || _station.Run?.Definition.Find(nodeId) is not { } node)
        {
            Title = "";
            Raise();
            return;
        }
        _nodeId = nodeId;
        Rows.Add(new PatchRowViewModel("name", "Bezeichnung", Engine.Blocks.ParameterKind.Text, node.Name));
        switch (node)
        {
            case Domain.Definitions.StepDefinition { BlockType: { } type } step when Engine.Blocks.BlockCatalog.Default.TryGet(type, out var d):
                foreach (var p in d.Parameters)
                    Rows.Add(new PatchRowViewModel("param:" + p.Name, p.Label, p.Kind, step.Parameters.GetValueOrDefault(p.Name) ?? p.Default, p.Choices));
                foreach (var (k, v) in step.Parameters.Where(kv => d.Parameters.All(p => p.Name != kv.Key)))
                    Rows.Add(new PatchRowViewModel("param:" + k, k, Engine.Blocks.ParameterKind.Text, v));
                Title = $"{d.DisplayName}: {node.Name}";
                break;
            case Domain.Definitions.GroupDefinition g:
                Rows.Add(new PatchRowViewModel("condition", "Eintrittsbedingung", Engine.Blocks.ParameterKind.Expression, g.EntryCondition ?? ""));
                Title = $"Gruppe: {node.Name}";
                break;
            default:
                Title = $"{node.Name} (nicht patchbar)";
                break;
        }
        Raise();
    }

    private List<PatchChange> Changes()
        => Rows.Where(r => r.IsChanged).Select(r => new PatchChange(_nodeId!, r.Property, r.Value)).ToList();

    /// <summary>„Patch prüfen“.</summary>
    public bool Validate()
    {
        if (_station.Run is not { } run || _nodeId is null) return false;
        var result = run.ValidatePatch(Changes());
        Valid = result.IsValid;
        Status = result.IsValid ? "Patch gültig – noch nicht übernommen." : string.Join(" ", result.Diagnostics);
        return result.IsValid;
    }

    /// <summary>„Änderungen übernehmen“: protokolliert, Lauf wird Entwicklungslauf.</summary>
    public bool Apply()
    {
        if (_station.Run is not { } run || _nodeId is null) return false;
        var result = run.ApplyPatch(Changes(), _user());
        Valid = result.Applied;
        Status = result.Applied
            ? $"Run-Patch {result.PatchNumber} übernommen. Bereits ausgeführte Schritte werden nicht wiederholt."
            : string.Join(" ", result.Diagnostics);
        if (result.Applied) Select(_nodeId);
        OnPropertyChanged(nameof(PatchCount));
        return result.Applied;
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(PatchCount));
        ValidateCommand.RaiseCanExecuteChanged();
        ApplyCommand.RaiseCanExecuteChanged();
    }
}
