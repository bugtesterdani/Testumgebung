/**
 * @file
 * @brief Bearbeitet rechnerbezogene Auswahlmethoden und Kennungsregeln vor ihrer Übernahme.
 */

using System.Collections.ObjectModel;
using Testumgebung.Contracts;
using Testumgebung.Engine;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Zeile der Kennungsregeln (bearbeitbar) mit Freigabestatus des Ziels.</summary>
public sealed class SelectionRuleRow(SelectionRule rule, SelectionPageViewModel owner) : ObservableObject
{
    public SelectionRule Rule { get; } = rule;

    public int Priority
    {
        get => Rule.Priority;
        set { if (Rule.Priority != value) { Rule.Priority = value; OnPropertyChanged(); owner.Edited(); } }
    }

    public string Pattern
    {
        get => Rule.Pattern;
        set { if (Rule.Pattern != value) { Rule.Pattern = (value ?? "").Trim(); OnPropertyChanged(); owner.Edited(); } }
    }

    public string Test
    {
        get => Rule.Test;
        set { if (Rule.Test != value) { Rule.Test = (value ?? "").Trim(); OnPropertiesChanged(nameof(Test), nameof(ReleasedText), nameof(IsReleased)); owner.Edited(); } }
    }

    /// <summary>Leer = „aktuelle freigegebene“.</summary>
    public string Version
    {
        get => string.IsNullOrWhiteSpace(Rule.Version) ? "aktuelle freigegebene" : Rule.Version;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) || value.Trim().StartsWith("aktuell", StringComparison.OrdinalIgnoreCase) ? null : value.Trim();
            if (Rule.Version == v) return;
            Rule.Version = v;
            OnPropertiesChanged(nameof(Version), nameof(ReleasedText), nameof(IsReleased));
            owner.Edited();
        }
    }

    public string Parameters
    {
        get => Rule.Parameters ?? "";
        set { if (Rule.Parameters != value) { Rule.Parameters = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); OnPropertyChanged(); owner.Edited(); } }
    }

    public bool IsReleased => TestSelector.Resolve(owner.Tests, Rule.Test, Rule.Version) is not null;

    public string ReleasedText => IsReleased ? "Ja" : "Nein";

    internal void Refresh() => OnPropertiesChanged(nameof(ReleasedText), nameof(IsReleased));
}

/// <summary>Feld des Ausgabevertrags (Anzeige).</summary>
public sealed record ContractField(string Name, string Type, string Description);

/// <summary>
/// Seite „Auswahl &amp; Auftrag“ (Abbildung „Auswahl und Auftragsablauf konfigurieren“): je Prüfplatz Auswahlmethoden
/// (manuell, Kennungsregeln, eigener Auswahlablauf – kombinierbar), Regeln mit Priorität, Meldung bei keiner eindeutigen
/// Zuordnung, alternativer Button-Text, Regel-Simulation und Ausgabevertrag. Gespeichert in der Prüfplatzkonfiguration.
/// </summary>
public sealed class SelectionPageViewModel : ObservableObject
{
    private readonly StationCoordinator _coordinator;
    private readonly StationSetupStore? _store;
    private readonly IFileDialogService? _dialogs;
    private readonly Func<IReadOnlyList<AvailableTest>> _tests;
    private readonly Func<bool> _anyRunning;
    private readonly IUiDispatcher _ui;
    private readonly Testumgebung.Domain.Code.ICodeRuntime? _codeRuntime;
    private string? _selectedStation;
    private SelectionConfig _draft = new();
    private bool _useSelection;
    private bool _isDirty;
    private SelectionRuleRow? _selectedRule;
    private string _sampleId = "SN-10482";
    private string _sampleOrder = "";
    private SelectionOutcome? _simulation;
    private string? _error;
    private string? _status;

    public SelectionPageViewModel(StationCoordinator coordinator, StationSetupStore? store, IFileDialogService? dialogs,
        Func<IReadOnlyList<AvailableTest>> tests, Func<bool> anyRunning, IUiDispatcher ui, Testumgebung.Domain.Code.ICodeRuntime? codeRuntime = null)
    {
        _coordinator = coordinator;
        _store = store;
        _dialogs = dialogs;
        _tests = tests;
        _anyRunning = anyRunning;
        _ui = ui;
        _codeRuntime = codeRuntime;
        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand(RemoveRule, () => _selectedRule is not null);
        SimulateCommand = new RelayCommand(() => _ = SimulateAsync());
        SaveCommand = new RelayCommand(Save, () => _isDirty);
        DiscardCommand = new RelayCommand(Load, () => _isDirty);
        BrowseCustomCommand = new RelayCommand(BrowseCustom);
        coordinator.SetupChanged += () => _ui.Post(() =>
        {
            RefreshStations();
            if (!_isDirty) Load();
        });
        RefreshStations();
        Load();
    }

    public ObservableCollection<string> StationNames { get; } = [];

    public string? SelectedStation
    {
        get => _selectedStation;
        set
        {
            if (value is null || !SetProperty(ref _selectedStation, value)) return;
            Load();
        }
    }

    public IReadOnlyList<AvailableTest> Tests => _tests();
    public IReadOnlyList<string> TestNames => Tests.Select(t => t.Name).Distinct().ToList();

    public RelayCommand AddRuleCommand { get; }
    public RelayCommand RemoveRuleCommand { get; }
    public RelayCommand SimulateCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand BrowseCustomCommand { get; }

    public string? Error
    {
        get => _error;
        private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!SetProperty(ref _isDirty, value)) return;
            SaveCommand.RaiseCanExecuteChanged();
            DiscardCommand.RaiseCanExecuteChanged();
        }
    }

    internal void Edited()
    {
        IsDirty = true;
        OnPropertiesChanged(nameof(MethodsText));
    }

    private void RefreshStations()
    {
        StationNames.Clear();
        foreach (var s in _coordinator.Setup.Stations) StationNames.Add(s.Name);
        if (_selectedStation is null || !StationNames.Contains(_selectedStation)) _selectedStation = StationNames.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedStation));
    }

    private void Load()
    {
        var current = _selectedStation is null ? null : _coordinator.Setup.Station(_selectedStation)?.Selection;
        _useSelection = current is not null;
        _draft = current is null
            ? new SelectionConfig { UseManual = true, UseRules = true }
            : System.Text.Json.JsonSerializer.Deserialize<SelectionConfig>(System.Text.Json.JsonSerializer.Serialize(current))!;
        Rules.Clear();
        foreach (var r in _draft.Rules.OrderBy(r => r.Priority)) Rules.Add(new SelectionRuleRow(r, this));
        IsDirty = false;
        Error = null;
        _simulation = null;
        OnPropertiesChanged(nameof(UseSelection), nameof(OrderSession), nameof(UseManual), nameof(UseRules), nameof(UseCustom), nameof(NoMatchMessage),
            nameof(AlternativeText), nameof(CustomProject), nameof(MethodsText), nameof(TestNames), nameof(Tests));
        RaiseSimulation();
    }

    /// <summary>Auswahl für diesen Prüfplatz verwenden (sonst prüft er den geöffneten bzw. freigegebenen Ablauf).</summary>
    public bool UseSelection
    {
        get => _useSelection;
        set { if (SetProperty(ref _useSelection, value)) Edited(); }
    }

    public bool OrderSession
    {
        get => _draft.OrderSession;
        set { if (_draft.OrderSession != value) { _draft.OrderSession = value; OnPropertyChanged(); Edited(); } }
    }

    public bool UseManual
    {
        get => _draft.UseManual;
        set { if (_draft.UseManual != value) { _draft.UseManual = value; OnPropertyChanged(); Edited(); } }
    }

    public bool UseRules
    {
        get => _draft.UseRules;
        set { if (_draft.UseRules != value) { _draft.UseRules = value; OnPropertyChanged(); Edited(); } }
    }

    public bool UseCustom
    {
        get => _draft.UseCustom;
        set { if (_draft.UseCustom != value) { _draft.UseCustom = value; OnPropertyChanged(); Edited(); } }
    }

    public string MethodsText => !_useSelection ? "Keine Auswahl: der Prüfplatz prüft den geöffneten bzw. freigegebenen Ablauf."
        : !_draft.Enabled ? "Keine Methode aktiv – kein Start möglich."
        : "Reihenfolge: " + string.Join(" → ", new[] { _draft.UseCustom ? "eigener Auswahlablauf" : null, _draft.UseRules ? "Kennungsregeln" : null, _draft.UseManual ? "manuelle Auswahl" : null }.OfType<string>());

    public string NoMatchMessage
    {
        get => _draft.NoMatchMessage;
        set { if (_draft.NoMatchMessage != value) { _draft.NoMatchMessage = value ?? ""; OnPropertyChanged(); Edited(); } }
    }

    public string AlternativeText
    {
        get => _draft.AlternativeText;
        set { if (_draft.AlternativeText != value) { _draft.AlternativeText = value ?? ""; OnPropertyChanged(); Edited(); } }
    }

    public string CustomProject
    {
        get => _draft.CustomProject ?? "";
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_draft.CustomProject == v) return;
            _draft.CustomProject = v;
            OnPropertyChanged();
            Edited();
        }
    }

    private void BrowseCustom()
    {
        var path = _dialogs?.OpenFile("Auswahlablauf wählen", "Testumgebung-Projekt (*.tproj.json)|*.tproj.json|Alle Dateien (*.*)|*.*");
        if (path is not null) CustomProject = path;
    }

    // ------------------------------------------------------------------ Kennungsregeln

    public ObservableCollection<SelectionRuleRow> Rules { get; } = [];

    public SelectionRuleRow? SelectedRule
    {
        get => _selectedRule;
        set { if (SetProperty(ref _selectedRule, value)) RemoveRuleCommand.RaiseCanExecuteChanged(); }
    }

    private void AddRule()
    {
        var rule = new SelectionRule
        {
            Priority = _draft.Rules.Count == 0 ? 1 : _draft.Rules.Max(r => r.Priority) + 1,
            Pattern = "SN-*",
            Test = TestNames.FirstOrDefault() ?? "",
        };
        _draft.Rules.Add(rule);
        var row = new SelectionRuleRow(rule, this);
        Rules.Add(row);
        SelectedRule = row;
        Edited();
    }

    private void RemoveRule()
    {
        if (_selectedRule is not { } row) return;
        _draft.Rules.Remove(row.Rule);
        Rules.Remove(row);
        SelectedRule = Rules.FirstOrDefault();
        Edited();
    }

    // ------------------------------------------------------------------ Regel-Simulation

    public string SampleId
    {
        get => _sampleId;
        set => SetProperty(ref _sampleId, value ?? "");
    }

    public string SampleOrder
    {
        get => _sampleOrder;
        set => SetProperty(ref _sampleOrder, value ?? "");
    }

    public SelectionOutcome? Simulation => _simulation;
    public bool SimulationOk => _simulation?.CanStart == true;
    public bool HasSimulation => _simulation is not null;
    public string SimulationText => _simulation?.Message ?? "";
    public string SimulationTest => _simulation?.Test?.Title ?? "—";
    public string SimulationOrder => _simulation?.Order ?? "—";
    public string SimulationSource => _simulation?.Source ?? "";
    public string SimulationParameters => _simulation is { Parameters.Count: > 0 } s ? string.Join(", ", s.Parameters.Select(kv => $"{kv.Key} = {kv.Value}")) : "—";

    private void RaiseSimulation() => OnPropertiesChanged(nameof(Simulation), nameof(SimulationOk), nameof(HasSimulation), nameof(SimulationText),
        nameof(SimulationTest), nameof(SimulationOrder), nameof(SimulationSource), nameof(SimulationParameters));

    /// <summary>„Prüfen“: Zuordnung für eine Beispielkennung mit dem Entwurf berechnen (eigener Ablauf ohne Werker/Hardware).</summary>
    public async Task SimulateAsync()
    {
        TestSelectionValue? custom = null;
        string? customError = null;
        if (_draft.UseCustom)
        {
            if (_draft.CustomProject is not { } path || !File.Exists(path))
            {
                customError = $"Projekt „{_draft.CustomProject}“ nicht gefunden.";
            }
            else
            {
                try
                {
                    var project = new ProjectSerializer().Load(path);
                    (custom, customError) = await TestSelector.RunCustomAsync(project, _sampleId.Trim(), _draft.OrderSession ? _sampleId.Trim() : _sampleOrder.Trim(),
                        new TestRunOptions { CodeRuntime = _codeRuntime, Hardware = SampleProjects.CreateSimulation(project).Registry, StationName = _selectedStation ?? "Simulation" }).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is ProjectFormatException or IOException)
                {
                    customError = ex.Message;
                }
            }
        }
        _simulation = TestSelector.Select(_draft, _sampleId.Trim(), _draft.OrderSession ? _sampleId.Trim() : (_sampleOrder.Trim().Length > 0 ? _sampleOrder.Trim() : null), Tests, custom, customError);
        RaiseSimulation();
    }

    /// <summary>Ausgabevertrag (Ergebnis der Auswahl) – Übergabe an den Prüfling über Variablen gleichen Namens.</summary>
    public IReadOnlyList<ContractField> Contract { get; } =
    [
        new("Testkennung", "string", "Ausgewählter Test (Name des Ablaufs, z. B. AC-Prüfung)"),
        new("Version", "string (optional)", "Freigegebene Version (leer = aktuelle Freigabe)"),
        new("Auftrag", "string", "Auftragsnummer (z. B. FA-2038) → Variable „Auftrag“"),
        new("Prüfparameter", "Name=Wert;…", "Setzt Variablen gleichen Namens im Prüfling (z. B. GrenzwertMin=220)"),
        new("Kennung", "string", "Im Auftragsmodus: neue Seriennummer in jeder Loop-Runde. Im Einzelprüfling-Modus: Vorbelegung der erfassten Kennung."),
    ];

    // ------------------------------------------------------------------ Speichern

    public void Save()
    {
        if (_selectedStation is null) return;
        if (_anyRunning())
        {
            Error = "Nicht gespeichert: Während einer Prüfung kann die Prüfplatzkonfiguration nicht geändert werden.";
            return;
        }
        if (_useSelection && !_draft.Enabled)
        {
            Error = "Mindestens eine Auswahlmethode aktivieren oder die Auswahl für diesen Prüfplatz ausschalten.";
            return;
        }
        if (_useSelection && _draft.UseCustom && string.IsNullOrWhiteSpace(_draft.CustomProject))
        {
            Error = "Für den eigenen Auswahlablauf eine Projektdatei wählen.";
            return;
        }
        var bad = _draft.Rules.Where(r => string.IsNullOrWhiteSpace(r.Pattern) || string.IsNullOrWhiteSpace(r.Test)).ToList();
        if (_useSelection && _draft.UseRules && bad.Count > 0)
        {
            Error = "Jede Regel braucht ein Kennungsmuster und eine Testkennung.";
            return;
        }
        var setup = _coordinator.Setup.Clone();
        var station = setup.Station(_selectedStation);
        if (station is null)
        {
            Error = $"{_selectedStation} gibt es nicht mehr.";
            return;
        }
        station.Selection = _useSelection ? _draft : null;
        try
        {
            _store?.Save(setup);
            _coordinator.Replace(setup);
            _coordinator.IsSuggestion = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = "Nicht gespeichert: " + ex.Message;
            return;
        }
        IsDirty = false;
        Error = null;
        Status = $"Auswahl für {_selectedStation} gespeichert.";
        Load();
    }
}
