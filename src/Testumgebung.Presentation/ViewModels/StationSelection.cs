using System.Collections.ObjectModel;
using Testumgebung.Contracts;
using Testumgebung.Engine;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class StationViewModel
{
    public IReadOnlyList<AvailableTest> AvailablePrograms => AvailableTests();

    public void RefreshAvailablePrograms() => OnPropertyChanged(nameof(AvailablePrograms));

    /// <summary>Auswahl- und Auftragsablauf dieses Prüfplatzes (Werkerpanel „Prüfling erfassen“).</summary>
    public StationSelectionViewModel Selection { get; }

    /// <summary>Konfiguration der Auswahl (Prüfplatzkonfiguration); null = ohne Auswahl.</summary>
    internal SelectionConfig? SelectionConfig => _coordinator?.Setup.Station(Name)?.Selection;

    public bool UsesSelection => SelectionConfig is { Enabled: true };

    internal IUiDispatcher Ui => _ui;

    internal void RaiseStartState() => _ui.Post(RaiseRunState);

    internal void ShowError(string? text) => Error = text;

    internal void SetHint(string text) => Hint = text;

    internal void LogSelection(string text, string value, ResultRating? rating, string status)
        => AddProtocol(new ProtocolEntry(DateTimeOffset.Now, ProtocolKind.Info, text, value, "", "", rating, status));

    /// <summary>
    /// Verfügbare Tests: im Werkerbetrieb nur freigegebene (aktuelle und früher freigegebene Versionen),
    /// im Entwicklungsmodus bzw. „Entwurf testen“ der Editorstand.
    /// </summary>
    internal IReadOnlyList<AvailableTest> AvailableTests()
    {
        if (_coordinator?.Setup.ExampleProjects is { Count: > 0 } examples)
            return examples.Select(p => new AvailableTest(p.Definition.Name, p.Definition.Version, true, "Simulation / Beispiel")).ToList();
        if (_releases is not null && !UseDraft)
            return _releases.All.Where(r => r.State is ReleaseState.Released or ReleaseState.Archived)
                .OrderBy(r => r.ProjectName, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.ReleasedAt)
                .Select(r => new AvailableTest(r.ProjectName, r.Version, r.State == ReleaseState.Released,
                    r.State == ReleaseState.Released ? "freigegeben" : "früher freigegeben"))
                .ToList();
        var p = _projectSource().Definition;
        return [new AvailableTest(p.Name, p.Version, true, _releases is null ? "Editor" : "Entwurf")];
    }

    /// <summary>Projekt des ausgewählten Tests laden (Release-Paket geprüft bzw. Kopie des Editorstands).</summary>
    internal async Task<(TestProject Project, ReleaseInfo? Release)> LoadTestAsync(AvailableTest test)
    {
        if (_coordinator?.Setup.ExampleProjects is { Count: > 0 } examples)
        {
            var example = examples.Single(p => p.Definition.Name == test.Name && p.Definition.Version == test.Version);
            return (_serializer.Clone(example), null);
        }
        if (_releases is not null && !UseDraft)
        {
            if (test.IsCurrent && _releases.GetReleased(test.Name) is { } current && current.Info.Version == test.Version)
                return (current.Project, current.Info);
            var info = _releases.All.FirstOrDefault(r => r.ProjectName == test.Name && r.Version == test.Version
                                                        && r.State is ReleaseState.Released or ReleaseState.Archived)
                       ?? throw new InvalidOperationException($"{test.Title} ist nicht (mehr) freigegeben.");
            var opened = await _releases.Service.OpenAsync(info).ConfigureAwait(true);
            if (!opened.IsIntact) throw new InvalidOperationException($"{test.Title}: Paket beschädigt – {string.Join("; ", opened.Problems)}");
            return (opened.Project, info);
        }
        return (_serializer.Clone(_projectSource()), null);
    }

    /// <summary>Optionen für den eigenen Auswahlablauf (Werkerinteraktion, Hardware, CodeBlocks dieses Prüfplatzes).</summary>
    internal TestRunOptions SelectionRunOptions(TestProject selectionProject) => new()
    {
        Clock = _clock,
        Resources = _resources,
        Hardware = _coordinator is not null
            ? _coordinator.BuildRegistry(Name, selectionProject, seed: Name.GetHashCode())
            : SampleProjects.CreateSimulation(selectionProject).Registry,
        Operator = this,
        StationName = Name,
        CodeRuntime = _codeRuntime,
    };
}

/// <summary>
/// Fertigungsauftrag auswählen und eine durchgehende Prüfsitzung starten. Im optionalen Einzelprüfling-Modus
/// erfolgt die Zuordnung je Gerät; im Auftragsmodus wird die Seriennummer erst im Prüflingstest erfasst.
/// </summary>
public sealed class StationSelectionViewModel : ObservableObject
{
    private readonly StationViewModel _station;
    private bool _isActive;
    private bool _busy;
    private bool _orderRunActive;
    private bool _starting;
    private string _kennung = "";
    private string _auftrag = "";
    private SelectionOutcome? _outcome;
    private AvailableTest? _manualChoice;
    private int _completed;
    private string? _lastResult;
    private CancellationTokenSource? _assignmentCancellation;
    private TaskCompletionSource? _assignmentCompletion;
    private TaskCompletionSource? _startCompletion;

    public Task WaitForCompletionAsync() => Task.WhenAll(_assignmentCompletion?.Task ?? Task.CompletedTask, _startCompletion?.Task ?? Task.CompletedTask);

    public StationSelectionViewModel(StationViewModel station)
    {
        _station = station;
        AssignCommand = new RelayCommand(() => _ = ProcessScanAsync(), () => (_isActive || OrderSession) && CanEditScan && ScanText.Trim().Length > 0);
    }

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (!SetProperty(ref _isActive, value)) return;
            OnPropertyChanged(nameof(ShowPanel));
            _station.RaiseStartState();
        }
    }

    /// <summary>Panel sichtbar: aktiv und gerade kein Prüfling in Prüfung.</summary>
    public bool OrderSession => _station.PairContext is not null || _station.SelectionConfig is { Enabled: true, OrderSession: true };
    public bool ShowPanel => (_isActive || OrderSession) && !_station.IsRunning;
    public bool ShowOrderField => !OrderSession;
    public bool CanEditScan => !_busy && !_starting && !_orderRunActive && !_station.IsRunning;
    public string ScanTitle => OrderSession ? "Fertigungsauftrag erfassen" : "Prüfling erfassen";
    public string ScanLabel => OrderSession ? "Fertigungsauftrag *" : "Kennung (Seriennummer) *";
    public string ScanHint => OrderSession ? "Fertigungsauftrag scannen und Enter drücken. Nach der Auswahl startet die Prüfsitzung; die Seriennummer wird in jeder Loop-Runde erfasst." : "Kennung scannen und mit Enter zuordnen.";
    public string ScanText { get => OrderSession ? Auftrag : Kennung; set { if (OrderSession) Auftrag = value; else Kennung = value; } }

    public async Task ProcessScanAsync()
    {
        if (!CanEditScan || ScanText.Trim().Length == 0) return;
        if (!IsActive) Activate();
        await AssignAsync().ConfigureAwait(true);
        if (OrderSession && CanStart) await StartSelectedAsync().ConfigureAwait(true);
    }

    internal void RecordDut(DutRunCompleted result)
    {
        if (!_orderRunActive) return;
        if (!string.IsNullOrWhiteSpace(result.DutId)) CompletedDuts++;
        LastResult = $"{result.DutId}: {result.Status}";
    }

    public RelayCommand AssignCommand { get; }

    public string Kennung
    {
        get => _kennung;
        set
        {
            if (!SetProperty(ref _kennung, value ?? "")) return;
            SetOutcome(null);
            AssignCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ScanText));
        }
    }

    /// <summary>Auftrag bleibt für weitere Prüflinge stehen, bis er geändert wird.</summary>
    public string Auftrag
    {
        get => _auftrag;
        set
        {
            if (!SetProperty(ref _auftrag, value ?? "")) return;
            SetOutcome(null);
            OnPropertyChanged(nameof(ScanText));
            AssignCommand.RaiseCanExecuteChanged();
        }
    }

    public SelectionOutcome? Outcome => _outcome;
    public bool IsUnique => _outcome?.CanStart == true;
    public bool HasProblem => _outcome is { CanStart: false };
    public string ResultText => _outcome?.Message ?? (OrderSession ? "Fertigungsauftrag scannen (Enter)." : "Kennung erfassen und zuordnen (Enter).");
    public string TestText => _outcome?.Test?.Title ?? "—";
    public string SourceText => _outcome is { Test: not null } o ? o.Source : "";
    public string ParametersText => _outcome is { Parameters.Count: > 0 } o ? string.Join(", ", o.Parameters.Select(kv => $"{kv.Key} = {kv.Value}")) : "";
    public string AlternativeText => _station.SelectionConfig?.AlternativeText ?? "Manuell auswählen";
    public ObservableCollection<AvailableTest> Candidates { get; } = [];
    public bool ShowManual => _outcome is { CanStart: false, ManualAllowed: true } && Candidates.Count > 0;

    /// <summary>Manuelle Auswahl aus den Kandidaten.</summary>
    public AvailableTest? ManualChoice
    {
        get => _manualChoice;
        set
        {
            if (!SetProperty(ref _manualChoice, value) || value is null || _outcome is null) return;
            var previous = _outcome;
            // Nach dem Auswahlvorgang der Liste anwenden (die Kandidatenliste wird dabei geleert).
            _station.Ui.Post(() =>
            {
                if (!ReferenceEquals(previous, _outcome)) return;
                try
                {
                    SetOutcome(TestSelector.Manual(previous, value, ScanText.Trim()));
                    _station.LogSelection("Auswahl · manuell zugeordnet", $"{_kennung} → {value.Title}", ResultRating.NResult, "OK");
                }
                catch (InvalidOperationException ex)
                {
                    _station.ShowError(ex.Message);
                }
            });
        }
    }

    public bool CanStart => _isActive && !_busy && !_starting && IsUnique && !_station.IsRunning;
    public bool CanSubmitOrder => OrderSession && CanEditScan && ScanText.Trim().Length > 0;

    internal void RaiseRunning()
    {
        OnPropertiesChanged(nameof(ShowPanel), nameof(CanStart), nameof(CanEditScan), nameof(ScanTitle), nameof(ScanLabel), nameof(ScanHint), nameof(ScanText), nameof(ShowOrderField));
        AssignCommand.RaiseCanExecuteChanged();
    }

    public int CompletedDuts
    {
        get => _completed;
        private set => SetProperty(ref _completed, value);
    }

    public string? LastResult
    {
        get => _lastResult;
        private set => SetProperty(ref _lastResult, value);
    }

    private void SetOutcome(SelectionOutcome? outcome)
    {
        _outcome = outcome;
        _manualChoice = null;
        Candidates.Clear();
        if (outcome is { CanStart: false })
            foreach (var c in outcome.Candidates) Candidates.Add(c);
        OnPropertiesChanged(nameof(Outcome), nameof(IsUnique), nameof(HasProblem), nameof(ResultText), nameof(TestText), nameof(SourceText),
            nameof(ParametersText), nameof(ShowManual), nameof(ManualChoice), nameof(CanStart), nameof(AlternativeText));
        _station.RaiseStartState();
    }

    /// <summary>„Start“ ohne laufende Auswahl: Erfassung öffnen.</summary>
    public void Activate()
    {
        if (_station.PairContext is null && _station.SelectionConfig is not { Enabled: true }) return;
        IsActive = true;
        Kennung = "";
        SetOutcome(null);
        _station.ShowError(null);
        _station.SetHint(OrderSession ? "Fertigungsauftrag scannen. Start einmal, Loop je Seriennummer, End bei Abbruch." : "Kennung erfassen, zuordnen, Start.");
        _station.LogSelection("Auswahl-/Auftragsablauf gestartet", _auftrag.Length > 0 ? "Auftrag " + _auftrag : "—", null, "Aktiv");
    }

    /// <summary>Werkerabbruch: Auswahl beenden (ein laufender Prüfling wird über den Abbruch der Engine beendet).</summary>
    public void End(string reason)
    {
        if (!IsActive) return;
        _assignmentCancellation?.Cancel();
        IsActive = false;
        if (OrderSession) { Auftrag = ""; Kennung = ""; }
        SetOutcome(null);
        _station.RaiseStartState();
        _station.SetHint(reason);
        _station.LogSelection("Auswahl-/Auftragsablauf beendet", $"{CompletedDuts} Prüfling(e)", null, "Beendet");
    }

    /// <summary>Test zuordnen: eigener Auswahlablauf (falls aktiv) → Kennungsregeln → manuelle Auswahl.</summary>
    public async Task AssignAsync()
    {
        if (!_isActive || _busy) return;
        var config = _station.SelectionConfig ?? new SelectionConfig();
        var id = ScanText.Trim();
        _busy = true;
        OnPropertyChanged(nameof(CanEditScan));
        using var cancellation = _assignmentCancellation = new CancellationTokenSource();
        var completion = _assignmentCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AssignCommand.RaiseCanExecuteChanged();
        try
        {
            var tests = _station.AvailableTests();
            var (custom, customError) = await RunCustomSelectionAsync(config, id, cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || !_isActive) return;
            var outcome = TestSelector.Select(config, id, _auftrag.Trim().Length > 0 ? _auftrag.Trim() : null, tests, custom, customError);
            if (OrderSession && config.UseCustom && !outcome.CanStart) outcome = outcome with { ManualAllowed = false };
            if (outcome.Order is { Length: > 0 } order && order != _auftrag) _auftrag = order;
            SetOutcome(outcome);
            OnPropertiesChanged(nameof(Auftrag), nameof(ScanText));
            LogOutcome(outcome, id);
        }
        finally
        {
            _assignmentCancellation = null;
            _busy = false;
            AssignCommand.RaiseCanExecuteChanged();
            OnPropertiesChanged(nameof(CanStart), nameof(CanEditScan));
            _station.RaiseStartState();
            completion.TrySetResult();
        }
    }

    private void LogOutcome(SelectionOutcome outcome, string id)
    {
        _station.LogSelection(outcome.CanStart ? "Auswahl · Kennung zugeordnet" : "Auswahl · keine eindeutige Zuordnung",
            outcome.CanStart ? $"{id} → {outcome.Test!.Title}" : id, outcome.CanStart ? ResultRating.NResult : ResultRating.Error,
            outcome.CanStart ? "OK" : "Kein Start");
    }

    private async Task<(TestSelectionValue? Value, string? Error)> RunCustomSelectionAsync(SelectionConfig config, string id, CancellationToken token)
    {
        TestSelectionValue? custom = null;
        string? customError = null;
        if (config.UseCustom && id.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(config.CustomProject) || !File.Exists(config.CustomProject))
            {
                customError = $"Projekt „{config.CustomProject}“ nicht gefunden.";
            }
            else
            {
                try
                {
                    var project = new ProjectSerializer().Load(config.CustomProject);
                    (custom, customError) = await TestSelector.RunCustomAsync(project, id, _auftrag, _station.SelectionRunOptions(project), token).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is ProjectFormatException or IOException or StationSetupException)
                {
                    customError = ex.Message;
                }
            }
        }
        return (custom, customError);
    }

    internal void AdoptSharedOrder(SelectionOutcome outcome)
    {
        Activate();
        _auftrag = outcome.Order ?? "";
        SetOutcome(outcome);
        OnPropertiesChanged(nameof(Auftrag), nameof(ScanText));
    }

    /// <summary>Zugeordneten Test für genau diesen Prüfling ausführen (Vorbelegung Kennung, Auftrag, Prüfparameter).</summary>
    public async Task StartSelectedAsync()
    {
        if (_startCompletion is { Task.IsCompleted: false }) return;
        var completion = _startCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await StartSelectedCoreAsync().ConfigureAwait(true); }
        finally { completion.TrySetResult(); }
    }

    private bool ValidateOrderProgram(TestProject project)
    {
        var duts = project.Definition.Loop.Descendants().OfType<GroupDefinition>().Where(g => g.IsDutRun).ToList();
        if (duts.Count == 0 || duts.Any(dut => !dut.Variables.Any(v => v.Name == dut.DutIdVariable && v.Type == VarType.String)))
        {
            _station.ShowError("Der Auftragsmodus benötigt einen Prüflingstest im Loop mit einer eigenen String-Variable für die Seriennummer (standardmäßig Kennung). Diese wird je Prüfling neu erfasst.");
            return false;
        }
        if (duts.Any(dut => dut.Children.FirstOrDefault() is not StepDefinition scan
            || scan.BlockType != BlockCatalog.Operator
            || scan.Parameters.GetValueOrDefault("Ziel") != dut.DutIdVariable
            || scan.Parameters.GetValueOrDefault("Art") != "Eingabe"
            || !bool.TryParse(scan.Parameters.GetValueOrDefault("Pflicht"), out var required) || !required))
        {
            _station.ShowError("Jeder Prüflingstest muss mit einer verpflichtenden Werkereingabe seiner Seriennummer beginnen. Bitte als ersten Schritt eine Eingabe mit Ziel Kennung (bzw. DutIdVariable) und Pflicht=true anlegen.");
            return false;
        }
        if (!project.Definition.Root.Variables.Any(v => v.Name == "Auftrag" && v.Type == VarType.String))
        {
            _station.ShowError("Der gewählte Test benötigt im Hauptablauf die String-Variable Auftrag, damit Start, Loop und End auf den Fertigungsauftrag zugreifen können.");
            return false;
        }
        return true;
    }

    private Dictionary<string, object?> SelectionPresets(TestProject project, bool session)
    {
        var presets = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (_station.PairContext is { } paired) presets["Pruefseite"] = (long)paired.Side;
        foreach (var (k, v) in _outcome!.Parameters) presets[k] = v;
        if (session)
        {
            // Seriennummern dürfen weder vom Auftrag noch von Auswahlparametern vorbelegt werden.
            presets.Remove("Kennung");
            foreach (var dut in project.Definition.AllNodes().OfType<GroupDefinition>().Where(g => g.IsDutRun))
                presets.Remove(dut.DutIdVariable);
        }
        else presets["Kennung"] = _kennung.Trim();
        if (_outcome.Order is { Length: > 0 } order) presets["Auftrag"] = order;
        return presets;
    }

    private async Task StartSelectedCoreAsync()
    {
        if (_outcome is null && ScanText.Trim().Length > 0) await AssignAsync().ConfigureAwait(true);
        if (!CanStart || _outcome?.Test is not { } test) return;
        var selectedOutcome = _outcome;
        (TestProject Project, ReleaseInfo? Release) loaded;
        _starting = true;
        RaiseRunning();
        try
        {
            loaded = await _station.LoadTestAsync(test).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ProjectFormatException or ReleasePackageException)
        {
            _station.ShowError(ex.Message);
            return;
        }
        finally { _starting = false; RaiseRunning(); }
        if (!_isActive || !ReferenceEquals(_outcome, selectedOutcome)) return;
        var session = OrderSession;
        var presets = SelectionPresets(loaded.Project, session);
        if (session && !ValidateOrderProgram(loaded.Project)) return;
        var id = ScanText.Trim();
        OnPropertyChanged(nameof(ShowPanel));
        _orderRunActive = session;
        try
        {
            await _station.RunProjectAsync(loaded.Project, loaded.Release, presets, dutLimit: session ? null : 1,
                $"{id} → {test.Title} · {_outcome.Source}", orderSession: session, pythonVenv: _outcome.PythonVenv).ConfigureAwait(true);
        }
        finally
        {
            if (session)
            {
                _station.Ui.Post(() =>
                {
                    _orderRunActive = false;
                    IsActive = false;
                    Auftrag = "";
                    Kennung = "";
                    SetOutcome(null);
                    RaiseRunning();
                    _station.SetHint("Prüfsitzung beendet. Fertigungsauftrag erneut scannen, um weiterzuprüfen oder einen anderen Auftrag zu wählen.");
                });
            }
        }
        if (session) return;
        _station.Ui.Post(() =>
        {
            CompletedDuts++;
            LastResult = $"{id}: {test.Title} – {_station.LastStatus?.ToString() ?? "beendet"}";
            if (!IsActive) return;
            Kennung = "";
            SetOutcome(null);
            OnPropertyChanged(nameof(ShowPanel));
            _station.SetHint("Nächsten Prüfling erfassen (Auftrag bleibt). Werkerabbruch beendet die Auswahl.");
        });
    }
}
