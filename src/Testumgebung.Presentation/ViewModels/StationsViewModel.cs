/**
 * @file
 * @brief Bearbeitet Gerätekonfiguration und Zuordnungen und zeigt Besitz, Wartende und Zugriffsdiagnosen.
 */

using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Hardware;
using Testumgebung.Instruments;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>
/// Prüfplatzkonfiguration (Etappe 9, Abbildung „Prüfplatz: Hardware und Ressourcen“): Prüfplätze anlegen/entfernen,
/// Geräte (lokal je Prüfplatz oder gemeinsam), Zuordnung Name → Gerät/Kanal mit Sperrpflicht, Ressourcen-Details,
/// Reservierungen je Gruppe mit Wartestatus, Konfigurationsprüfung (inkl. Zyklusrisiko) und Zugriffsprotokoll.
/// Gespeichert wird je Rechner (<c>%ProgramData%\Testumgebung\pruefplaetze.json</c>); übernommen nur ohne laufende Prüfung.
/// </summary>
public sealed partial class StationsViewModel : ObservableObject
{
    private readonly StationCoordinator _coordinator;
    private readonly EditorViewModel _editor;
    private readonly StationSetupStore? _store;
    private readonly IUiDispatcher _ui;
    private readonly Func<bool> _anyRunning;
    private StationSetup _draft;
    private string? _selectedStation;
    private AssignmentRow? _selectedRow;
    private GroupDefinition? _selectedGroup;
    private bool _isDirty;
    private string? _error;
    private string? _status;
    private int _refreshQueued;
    private IReadOnlyList<ResourceOwnership> _snapshot = [];

    public StationsViewModel(StationCoordinator coordinator, EditorViewModel editor, StationSetupStore? store, IUiDispatcher ui, Func<bool> anyRunning, Func<string?>? wearResetUser = null)
    {
        _coordinator = coordinator;
        _editor = editor;
        _store = store;
        _ui = ui;
        _anyRunning = anyRunning;
        _wearResetUser = wearResetUser;
        _draft = coordinator.Setup.Clone();

        CheckCommand = new RelayCommand(Check);
        SaveCommand = new RelayCommand(Save, () => _isDirty || _coordinator.IsSuggestion || _coordinator.LoadError is not null);
        DiscardCommand = new RelayCommand(Discard, () => _isDirty);
        AddStationCommand = new RelayCommand(AddStation);
        RemoveStationCommand = new RelayCommand(RemoveStation, () => _selectedStation is not null && _draft.Stations.Count > 1);
        AddResourceCommand = new RelayCommand(AddResource, () => _selectedStation is not null);
        RemoveResourceCommand = new RelayCommand(RemoveResource, () => _selectedRow is not null);
        AddMissingCommand = new RelayCommand(AddMissing, () => _selectedStation is not null);
        AddDeviceCommand = new RelayCommand(AddDevice);
        InitDeviceCommands();
        InitProfileCommands();
        RestoreSuggestionCommand = new RelayCommand(RestoreSuggestion);

        if (_coordinator.Resources is ResourceManager rm) rm.Changed += QueueRefresh;
        _coordinator.Journal.Changed += QueueRefresh;
        _coordinator.SetupChanged += () => _ui.Post(() => { if (!_isDirty) Reload(); });
        _editor.ProjectReplaced += () => _ui.Post(() => { ReloadProfiles(); ReloadGroups(); Check(); });
        Reload();
    }

    // ------------------------------------------------------------------ Allgemein

    public string StorePath => _store?.Path ?? "(nicht gespeichert)";

    public string SourceText => _coordinator.LoadError is { } errorText ? "Konfigurationsdatei beschädigt – kein Prüfplatz startet: " + errorText
        : _coordinator.IsSuggestion ? "Keine Konfigurationsdatei: Vorschlag aus dem Projekt (gesperrte Ressourcen auf einem gemeinsamen Gerät)."
        : "Gespeichert in " + StorePath;

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

    public RelayCommand CheckCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand AddStationCommand { get; }
    public RelayCommand RemoveStationCommand { get; }
    public RelayCommand AddResourceCommand { get; }
    public RelayCommand RemoveResourceCommand { get; }
    public RelayCommand AddMissingCommand { get; }
    public RelayCommand AddDeviceCommand { get; }
    public RelayCommand RestoreSuggestionCommand { get; }

    /// <summary>Entwurf der Konfiguration (für Tests).</summary>
    public StationSetup Draft => _draft;

    private TestProject Project => _editor.Project;

    internal void Edited()
    {
        IsDirty = true;
        foreach (var assignmentRow in Rows) assignmentRow.RaiseAll();
        Check();
        UpdateDetails();
        RefreshLive();
    }

    private void Reload()
    {
        ReloadProfiles();
        _draft = _coordinator.Setup.Clone();
        Devices.Clear();
        foreach (var device in _draft.Devices) Devices.Add(new DeviceRow(device, Edited, () => Project));
        StationNames.Clear();
        foreach (var station in _draft.Stations) StationNames.Add(station.Name);
        _selectedStation = StationNames.Contains(_selectedStation ?? "") ? _selectedStation : StationNames.FirstOrDefault();
        IsDirty = false;
        LoadRows();
        ReloadGroups();
        Check();
        OnPropertiesChanged(nameof(SelectedStation), nameof(SourceText));
        NotifyReferenceSettings();
        ReloadWearCounters();
        SaveCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ Prüfplätze

    public ObservableCollection<string> StationNames { get; } = [];

    public string? SelectedStation
    {
        get => _selectedStation;
        set
        {
            if (value is null || !SetProperty(ref _selectedStation, value)) return;
            LoadRows();
            NotifyReferenceSettings();
            ReloadWearCounters();
            RemoveStationCommand.RaiseCanExecuteChanged();
            RefreshLive();
        }
    }

    private StationConfig? Station => _selectedStation is null ? null : _draft.Station(_selectedStation);

    private ReferenceCheckConfig? ReferenceCheck => Station?.ReferenceCheck;

    public bool ReferenceEnabled { get => ReferenceCheck?.Enabled ?? false; set => EditReference(config => config.Enabled = value); }
    public string ReferenceGoodId { get => ReferenceCheck?.GoodId ?? ""; set => EditReference(config => config.GoodId = value); }
    public string ReferenceBadId { get => ReferenceCheck?.BadId ?? ""; set => EditReference(config => config.BadId = value); }
    public string ReferenceShiftStarts { get => ReferenceCheck?.ShiftStarts ?? "06:00;14:00;22:00"; set => EditReference(config => config.ShiftStarts = value); }
    public int ReferenceDutInterval { get => ReferenceCheck?.DutInterval ?? 0; set => EditReference(config => config.DutInterval = value); }
    public double ReferenceHoursInterval { get => ReferenceCheck?.HoursInterval ?? 0; set => EditReference(config => config.HoursInterval = value); }

    private void EditReference(Action<ReferenceCheckConfig> change)
    {
        if (Station is not { } station) return;
        change(station.ReferenceCheck ??= new());
        Edited();
        NotifyReferenceSettings();
    }

    private void NotifyReferenceSettings() => OnPropertiesChanged(nameof(ReferenceEnabled), nameof(ReferenceGoodId), nameof(ReferenceBadId),
        nameof(ReferenceShiftStarts), nameof(ReferenceDutInterval), nameof(ReferenceHoursInterval));

    private void AddStation()
    {
        var index = 1;
        while (_draft.Stations.Any(station2 => station2.Name == StationSetup.StationName(index))) index++;
        var station = new StationConfig { Name = StationSetup.StationName(index) };
        // Zuordnung des gewählten Prüfplatzes übernehmen (lokale Geräte bekommen eigene Instanzen, gemeinsame bleiben gemeinsam)
        var template = Station?.Resources ?? StationSetup.Suggest(Project, []).ToList();
        station.Resources.AddRange(template.Select(assignment => new ResourceAssignment { Name = assignment.Name, Device = assignment.Device, Channel = assignment.Channel, RequiresLock = assignment.RequiresLock }));
        _draft.Stations.Add(station);
        StationNames.Add(station.Name);
        SelectedStation = station.Name;
        Edited();
        Status = $"{station.Name} angelegt (Zuordnung von der Vorlage übernommen).";
    }

    private void RemoveStation()
    {
        if (Station is not { } station || _draft.Stations.Count <= 1) return;
        _draft.Stations.Remove(station);
        StationNames.Remove(station.Name);
        SelectedStation = StationNames.FirstOrDefault();
        Edited();
        Status = $"{station.Name} entfernt (wirksam nach dem Speichern).";
    }

    // ------------------------------------------------------------------ Prüfung und Speichern

    public ObservableCollection<ConfigIssue> Issues { get; } = [];
    public bool HasConfigErrors => Issues.Any(issue => issue.IsError);

    public string CheckSummary
    {
        get
        {
            var errors = Issues.Count(issue => issue.IsError);
            var warnings = Issues.Count(issue => issue.Severity == ConfigSeverity.Warning);
            return errors > 0 ? $"{errors} Fehler – Konfiguration verweigert" : warnings > 0 ? $"In Ordnung, {warnings} Hinweis(e)" : "Konfiguration in Ordnung";
        }
    }

    /// <summary>„Simulation prüfen“: Zuordnung, gemeinsame Geräte, Zyklusrisiko.</summary>
    public void Check()
    {
        Issues.Clear();
        foreach (var issue in WearConfigIssues()) Issues.Add(issue);
        foreach (var station in _draft.Stations)
            if (station.ReferenceCheck?.Validate() is { } message)
                Issues.Add(new(ConfigSeverity.Error, station.Name, null, message));
        foreach (var issue in StationHardware.Check(_draft, Project).OrderByDescending(issue2 => issue2.Severity)) Issues.Add(issue);
        OnPropertiesChanged(nameof(HasConfigErrors), nameof(CheckSummary));
    }

    private void Discard()
    {
        Reload();
        Status = "Änderungen verworfen.";
    }

    /// <summary>„Zuordnung speichern“: nur fehlerfrei und ohne laufende Prüfung; danach gilt sie für alle Prüfplätze.</summary>
    public void Save()
    {
        Check();
        if (HasConfigErrors)
        {
            Error = "Nicht gespeichert: " + string.Join(" ", Issues.Where(issue => issue.IsError).Select(issue => issue.Text));
            return;
        }
        if (_anyRunning())
        {
            Error = "Nicht gespeichert: Während einer Prüfung kann die Prüfplatzkonfiguration nicht geändert werden.";
            return;
        }
        try
        {
            _store?.Save(_draft);
            _coordinator.Replace(_draft.Clone());
            _coordinator.IsSuggestion = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex is UnauthorizedAccessException
                ? $"Keine Schreibrechte für {StorePath} – die Prüfplatzkonfiguration gilt für alle Benutzer und wird von Administratoren gepflegt."
                : "Nicht gespeichert: " + ex.Message;
            return;
        }
        Error = null;
        IsDirty = false;
        Status = $"Prüfplatzkonfiguration gespeichert ({_draft.Stations.Count} Prüfplätze, {_draft.Devices.Count} Geräte).";
        OnPropertyChanged(nameof(SourceText));
        SaveCommand.RaiseCanExecuteChanged();
    }
}
