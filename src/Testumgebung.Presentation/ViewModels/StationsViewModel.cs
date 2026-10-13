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

/// <summary>Gerät in der Konfiguration (bearbeitbar).</summary>
public sealed class DeviceRow : ObservableObject
{
    private readonly Action _changed;
    private string _probeText = "";
    private bool? _probeOk;
    private bool _probing;
    private readonly Func<TestProject?> _project;

    public DeviceRow(DeviceConfig device, Action changed, Func<TestProject?>? project = null)
    {
        Device = device;
        _changed = changed;
        _project = project ?? (() => null);
        ProbeCommand = new RelayCommand(() => _ = ProbeAsync(), () => !_probing);
    }

    public DeviceConfig Device { get; }
    public string Id => Device.Id;

    public string Name
    {
        get => Device.Name;
        set { if (Device.Name != value) { Device.Name = value; OnPropertiesChanged(nameof(Name), nameof(Text)); _changed(); } }
    }

    /// <summary>Ein Gerät für alle Prüfplätze.</summary>
    public bool Shared
    {
        get => Device.Shared;
        set { if (Device.Shared != value) { Device.Shared = value; OnPropertiesChanged(nameof(Shared), nameof(Text)); _changed(); } }
    }

    /// <summary>Adapterfamilie („Simulation“ oder Treiber).</summary>
    public string Adapter
    {
        get => Device.Adapter;
        set
        {
            if (value is null || Device.Adapter == value) return;
            Device.Adapter = value;
            var family = ProjectInstruments.Family(_project(), value);
            Device.Model = family?.Models.FirstOrDefault();
            if (family is not null && string.IsNullOrWhiteSpace(Device.Resource)) Device.Resource = family.ResourceExample;
            OnPropertiesChanged(nameof(Adapter), nameof(Models), nameof(Model), nameof(Resource), nameof(IsReal), nameof(Notes), nameof(Text));
            _changed();
        }
    }

    public bool IsReal => !Device.IsSimulation;
    public IReadOnlyList<string> Models => ProjectInstruments.Family(_project(), Device.Adapter)?.Models ?? [];
    public string Notes => ProjectInstruments.Family(_project(), Device.Adapter)?.Notes ?? "Simuliertes Gerät: Verhalten aus dem Simulationsprofil des Projekts.";

    public string? Model
    {
        get => Device.Model;
        set { if (Device.Model != value) { Device.Model = value; OnPropertiesChanged(nameof(Model), nameof(Text)); _changed(); } }
    }

    public string? Resource
    {
        get => Device.Resource;
        set { if (Device.Resource != value) { Device.Resource = value?.Trim(); OnPropertyChanged(); _changed(); } }
    }

    /// <summary>Optionen, eine Zeile je „Schlüssel=Wert“.</summary>
    public string OptionsText
    {
        get => Device.OptionsText;
        set { if (Device.OptionsText != value) { Device.OptionsText = value; OnPropertyChanged(); _changed(); } }
    }

    public string TimeoutText
    {
        get => Device.TimeoutMs?.ToString(CultureInfo.InvariantCulture) ?? "";
        set
        {
            var t = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0 ? ms : (int?)null;
            if (Device.TimeoutMs == t) return;
            Device.TimeoutMs = t;
            OnPropertyChanged();
            _changed();
        }
    }

    public bool LockPerChannel
    {
        get => Device.LockPerChannel;
        set { if (Device.LockPerChannel != value) { Device.LockPerChannel = value; OnPropertyChanged(); _changed(); } }
    }

    public string Text => $"{Device.Name}{(Device.IsSimulation ? "" : $" – {Device.Model}")}{(Device.Shared ? " (gemeinsam)" : "")}";

    internal void RaiseAllDevice() => OnPropertiesChanged(nameof(Name), nameof(Shared), nameof(Adapter), nameof(Models), nameof(Model), nameof(Resource),
        nameof(OptionsText), nameof(TimeoutText), nameof(IsReal), nameof(Notes), nameof(Text), nameof(LockPerChannel));

    // ------------------------------------------------------------------ Gerät testen

    public RelayCommand ProbeCommand { get; }

    public string ProbeText
    {
        get => _probeText;
        private set => SetProperty(ref _probeText, value);
    }

    public bool? ProbeOk
    {
        get => _probeOk;
        private set => SetProperty(ref _probeOk, value);
    }

    /// <summary>„Gerät testen“: *IDN?, Fehlerwarteschlange, Fähigkeiten – Ausgänge bleiben unverändert.</summary>
    public async Task ProbeAsync()
    {
        _probing = true;
        ProbeCommand.RaiseCanExecuteChanged();
        ProbeText = "Teste …";
        ProbeOk = null;
        try
        {
            var probe = await StationCoordinator.ProbeAsync(Device, project: _project()).ConfigureAwait(true);
            ProbeOk = probe.Ok;
            ProbeText = (probe.Ok ? "✓ " : "✗ ") + probe.Identity + Environment.NewLine + string.Join(Environment.NewLine, probe.Details);
        }
        finally
        {
            _probing = false;
            ProbeCommand.RaiseCanExecuteChanged();
        }
    }
}

/// <summary>Zeile „Geräte und Signale“: Name → Gerät/Kanal mit Live-Status aus der Ressourcenvermittlung.</summary>
public sealed class AssignmentRow : ObservableObject
{
    private readonly StationsViewModel _owner;
    private string _status = "Bereit";
    private StationStatusKind _statusKind = StationStatusKind.Running;

    public AssignmentRow(StationsViewModel owner, ResourceAssignment assignment)
    {
        _owner = owner;
        Assignment = assignment;
    }

    public ResourceAssignment Assignment { get; }

    public string Name
    {
        get => Assignment.Name;
        set { if (Assignment.Name != value) { Assignment.Name = value.Trim(); OnPropertyChanged(); _owner.Edited(); } }
    }

    public DeviceRow? Device
    {
        get => _owner.Devices.FirstOrDefault(d => d.Id == Assignment.Device);
        set
        {
            if (value is null || value.Id == Assignment.Device) return;
            Assignment.Device = value.Id;
            if (value.Shared) Assignment.RequiresLock = true; // gemeinsames Gerät nur mit Sperre
            OnPropertiesChanged(nameof(Device), nameof(RequiresLock), nameof(Adapter), nameof(ChannelChoices), nameof(Unit));
            _owner.Edited();
        }
    }

    public string Adapter => Device?.Adapter ?? "—";

    public string Channel
    {
        get => Assignment.Channel;
        set { if (value is not null && Assignment.Channel != value) { Assignment.Channel = value.Trim(); OnPropertiesChanged(nameof(Channel), nameof(Unit)); _owner.Edited(); } }
    }

    public bool RequiresLock
    {
        get => Assignment.RequiresLock;
        set { if (Assignment.RequiresLock != value) { Assignment.RequiresLock = value; OnPropertyChanged(); _owner.Edited(); } }
    }

    public string Unit => _owner.UnitOf(Assignment);

    /// <summary>Kanäle des zugeordneten realen Geräts (Auswahl); Simulation: freie Eingabe.</summary>
    public IReadOnlyList<string> ChannelChoices => _owner.ChannelNames(Assignment.Device);

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public StationStatusKind StatusKind
    {
        get => _statusKind;
        set => SetProperty(ref _statusKind, value);
    }

    internal void RaiseAll() => OnPropertiesChanged(nameof(Name), nameof(Device), nameof(Adapter), nameof(Channel), nameof(RequiresLock), nameof(Unit), nameof(ChannelChoices));
}

/// <summary>Benötigte Ressource einer Gruppe am gewählten Prüfplatz.</summary>
public sealed record ReservationRow(string Name, string Device, string Channel, string Key, string Status, StationStatusKind StatusKind);

/// <summary>Wartestatus je Prüfplatz für eine Gruppe (Eigentümer, in Warteschlange, frei).</summary>
public sealed record WaitRow(string Station, string State, bool IsOwner, bool IsWaiting);

/// <summary>Zeile des Zugriffsprotokolls.</summary>
public sealed record AccessRow(string Time, string Station, string Resource, string Operation, string Key, string? Conflict)
{
    public bool HasConflict => Conflict is not null;
}

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

    public StationsViewModel(StationCoordinator coordinator, EditorViewModel editor, StationSetupStore? store, IUiDispatcher ui, Func<bool> anyRunning)
    {
        _coordinator = coordinator;
        _editor = editor;
        _store = store;
        _ui = ui;
        _anyRunning = anyRunning;
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

    public string SourceText => _coordinator.LoadError is { } e ? "Konfigurationsdatei beschädigt – kein Prüfplatz startet: " + e
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
        foreach (var r in Rows) r.RaiseAll();
        Check();
        UpdateDetails();
        RefreshLive();
    }

    private void Reload()
    {
        ReloadProfiles();
        _draft = _coordinator.Setup.Clone();
        Devices.Clear();
        foreach (var d in _draft.Devices) Devices.Add(new DeviceRow(d, Edited, () => Project));
        StationNames.Clear();
        foreach (var s in _draft.Stations) StationNames.Add(s.Name);
        _selectedStation = StationNames.Contains(_selectedStation ?? "") ? _selectedStation : StationNames.FirstOrDefault();
        IsDirty = false;
        LoadRows();
        ReloadGroups();
        Check();
        OnPropertiesChanged(nameof(SelectedStation), nameof(SourceText));
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
            RemoveStationCommand.RaiseCanExecuteChanged();
            RefreshLive();
        }
    }

    private StationConfig? Station => _selectedStation is null ? null : _draft.Station(_selectedStation);

    private void AddStation()
    {
        var n = 1;
        while (_draft.Stations.Any(s => s.Name == StationSetup.StationName(n))) n++;
        var station = new StationConfig { Name = StationSetup.StationName(n) };
        // Zuordnung des gewählten Prüfplatzes übernehmen (lokale Geräte bekommen eigene Instanzen, gemeinsame bleiben gemeinsam)
        var template = Station?.Resources ?? StationSetup.Suggest(Project, []).ToList();
        station.Resources.AddRange(template.Select(a => new ResourceAssignment { Name = a.Name, Device = a.Device, Channel = a.Channel, RequiresLock = a.RequiresLock }));
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

    // ------------------------------------------------------------------ Geräte und Signale

    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ObservableCollection<AssignmentRow> Rows { get; } = [];

    public AssignmentRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            RemoveResourceCommand.RaiseCanExecuteChanged();
            UpdateDetails();
        }
    }

    private void LoadRows()
    {
        Rows.Clear();
        if (Station is { } s)
            foreach (var a in s.Resources) Rows.Add(new AssignmentRow(this, a));
        SelectedRow = Rows.FirstOrDefault(r => r.Assignment.RequiresLock) ?? Rows.FirstOrDefault();
        AddResourceCommand.RaiseCanExecuteChanged();
        AddMissingCommand.RaiseCanExecuteChanged();
        RefreshLive();
    }

    internal string UnitOf(ResourceAssignment a)
        => Channels(a.Device)?.FirstOrDefault(c => string.Equals(c.Name, a.Channel, StringComparison.OrdinalIgnoreCase))?.Unit
           ?? Project.Simulation.FirstOrDefault(r => r.Name == a.Name)?.Unit
           ?? Project.SimulationOutputs.FirstOrDefault(o => o.Name == a.Name)?.Unit
           ?? "—";

    private readonly Dictionary<string, (string Signature, IReadOnlyList<InstrumentChannel>? Channels)> _channelCache = new(StringComparer.Ordinal);

    /// <summary>Kanäle eines realen Geräts (zwischengespeichert, solange Adapter/Modell/Ressource gleich bleiben).</summary>
    internal IReadOnlyList<InstrumentChannel>? Channels(string deviceId)
    {
        if (_draft.Device(deviceId) is not { IsSimulation: false } d) return null;
        var signature = $"{d.Adapter}|{d.Model}|{d.Resource}|{d.OptionsText}";
        if (_channelCache.TryGetValue(deviceId, out var cached) && cached.Signature == signature) return cached.Channels;
        var channels = StationHardware.ChannelsOf(d, Project);
        _channelCache[deviceId] = (signature, channels);
        return channels;
    }

    internal IReadOnlyList<string> ChannelNames(string deviceId) => Channels(deviceId)?.Select(c => c.Name).ToList() ?? [];

    private void AddResource()
    {
        if (Station is not { } s) return;
        var n = 1;
        while (s.Resources.Any(a => a.Name == $"Neue_Ressource_{n}")) n++;
        var a = new ResourceAssignment { Name = $"Neue_Ressource_{n}", Device = StationSetup.LocalSimulation, Channel = $"ch{n}" };
        if (_draft.Device(a.Device) is null) a.Device = _draft.Devices.FirstOrDefault()?.Id ?? "";
        s.Resources.Add(a);
        var row = new AssignmentRow(this, a);
        Rows.Add(row);
        SelectedRow = row;
        Edited();
    }

    private void RemoveResource()
    {
        if (Station is not { } s || _selectedRow is null) return;
        s.Resources.Remove(_selectedRow.Assignment);
        Rows.Remove(_selectedRow);
        SelectedRow = Rows.FirstOrDefault();
        Edited();
    }

    /// <summary>Vom Ablauf verwendete, noch nicht zugeordnete Namen nach dem Projektvorschlag ergänzen.</summary>
    private void AddMissing()
    {
        if (Station is not { } s) return;
        var added = StationSetup.Suggest(Project, s.Resources).ToList();
        foreach (var n in StationSetup.UsedResources(Project).Where(n => s.Resources.All(a => a.Name != n) && added.All(a => a.Name != n)))
            added.Add(new ResourceAssignment { Name = n, Device = StationSetup.LocalSimulation, Channel = n });
        s.Resources.AddRange(added);
        LoadRows();
        Edited();
        Status = added.Count == 0 ? "Alle verwendeten Ressourcen sind zugeordnet." : $"{added.Count} Ressource(n) ergänzt: {string.Join(", ", added.Select(a => a.Name))}.";
    }

    /// <summary>Auswahl der Adapterfamilie: Simulation und die Treiber aus dem Gerätekatalog.</summary>
    public IReadOnlyList<string> AdapterChoices => [StationSetup.SimulationAdapter, .. InstrumentCatalog.Families.Select(f => f.Adapter), .. Project.DeviceProfiles.Select(p => p.Adapter)];

    private DeviceRow? _selectedDevice;

    public DeviceRow? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value)) return;
            RemoveDeviceCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelectedDevice));
        }
    }

    public bool HasSelectedDevice => _selectedDevice is not null;

    public RelayCommand RemoveDeviceCommand { get; private set; } = null!;
    public RelayCommand DiscoverCommand { get; private set; } = null!;
    public RelayCommand StartLabCommand { get; private set; } = null!;
    public ObservableCollection<string> Discovered { get; } = [];

    private void InitDeviceCommands()
    {
        RemoveDeviceCommand = new RelayCommand(RemoveDevice, () => _selectedDevice is not null);
        DiscoverCommand = new RelayCommand(() => _ = DiscoverAsync());
        StartLabCommand = new RelayCommand(() => _ = StartLabAsync());
    }

    private void RemoveDevice()
    {
        if (_selectedDevice is not { } row) return;
        var users = _draft.Stations.Where(st => st.Resources.Any(a => a.Device == row.Id)).Select(st => st.Name).ToList();
        if (users.Count > 0)
        {
            Error = $"„{row.Name}“ wird noch verwendet ({string.Join(", ", users)}) – erst die Zuordnungen ändern.";
            return;
        }
        _draft.Devices.Remove(row.Device);
        Devices.Remove(row);
        SelectedDevice = Devices.FirstOrDefault();
        Edited();
    }

    /// <summary>„Geräte suchen“: VISA-Ressourcen (USB, GPIB, LAN) und NI-DAQmx-Geräte des Rechners.</summary>
    public async Task DiscoverAsync()
    {
        Discovered.Clear();
        Status = "Suche Geräte …";
        var found = await Task.Run(InstrumentCatalog.Discover).ConfigureAwait(true);
        foreach (var f in found) Discovered.Add(f);
        Status = $"{found.Count(f => f.StartsWith("VISA: ", StringComparison.Ordinal) && f.Contains("::", StringComparison.Ordinal)) + found.Count(f => f.StartsWith("NI-DAQmx: ", StringComparison.Ordinal) && f.Contains(" – ", StringComparison.Ordinal))} Gerät(e) gefunden.";
    }

    /// <summary>
    /// „Laborsimulatoren starten“: Multimeter, Netzgerät, AC-Quelle, Generator und Oszilloskop als SCPI-Simulatoren auf
    /// localhost (echter Treiberpfad über LAN-Socket) und die DAQmx-Nachbildung als Geräte in die Konfiguration übernehmen.
    /// </summary>
    public async Task StartLabAsync()
    {
        var lab = await Task.Run(() => SimulatedLabHost.Start()).ConfigureAwait(true);
        (string Id, string Name, string Adapter, string Model, string Resource, string? Options)[] devices =
        [
            ("lab-dmm", "Multimeter (Laborsimulator)", Keysight3446xDmm.AdapterName, "34465A", lab.Dmm.Resource, null),
            ("lab-netz", "Netzgerät (Laborsimulator)", KeysightE36300Supply.AdapterName, "E36312A", lab.Supply.Resource, null),
            ("lab-ac", "AC-Quelle (Laborsimulator)", KeysightAc6800Source.AdapterName, "AC6802B", lab.AcSource.Resource, "Bereich=310"),
            ("lab-gen", "Generator (Laborsimulator)", Keysight33500Generator.AdapterName, "33522B", lab.Generator.Resource, "Kurvenkanäle=L1=1;L2=2"),
            ("lab-oszi", "Oszilloskop (Laborsimulator)", KeysightInfiniiVisionScope.AdapterName, "DSOX3034T", lab.Scope.Resource, null),
            ("lab-daq", "NI-DAQmx-Nachbildung", InstrumentCatalog.DaqmxReplica, "USB-6211 + NI 9225", "Dev1,cDAQ1Mod1", null),
        ];
        foreach (var (id, name, adapter, model, resource, options) in devices)
        {
            var d = _draft.Device(id);
            if (d is null)
            {
                d = new DeviceConfig { Id = id };
                _draft.Devices.Add(d);
                Devices.Add(new DeviceRow(d, Edited, () => Project));
            }
            (d.Name, d.Adapter, d.Model, d.Resource, d.Shared) = (name, adapter, model, resource, true);
            d.OptionsText = options ?? "";
        }
        foreach (var row in Devices) row.RaiseAllDevice();
        Edited();
        Status = "Laborsimulatoren laufen (localhost) und sind als gemeinsame Geräte eingetragen – Zuordnung anpassen und speichern. " +
                 "Verdrahtung: DMM VAC/IAC ← AC-Quelle, VDC/IDC ← Netzgerät CH1, Oszilloskop CHAN1 ← AC-Quelle, CHAN2/3 ← Generator.";
    }

    private void AddDevice()
    {
        var n = 1;
        while (_draft.Devices.Any(d => d.Id == $"geraet-{n}")) n++;
        var d = new DeviceConfig { Id = $"geraet-{n}", Name = $"Gerät {n} (Simulation)" };
        _draft.Devices.Add(d);
        Devices.Add(new DeviceRow(d, Edited, () => Project));
        Edited();
    }

    private void RestoreSuggestion()
    {
        _draft = StationSetup.Default(Project, _draft.Stations.Select(s => s.Name).ToList());
        Devices.Clear();
        foreach (var d in _draft.Devices) Devices.Add(new DeviceRow(d, Edited, () => Project));
        LoadRows();
        Edited();
        Status = "Vorschlag aus dem Projekt wiederhergestellt (noch nicht gespeichert).";
    }

    // ------------------------------------------------------------------ Ressourcen-Details

    /// <summary>Eigenschaften der gewählten Ressource (Adapter, Gerät, Kanal, Bereiche, Fähigkeiten, Sperrschlüssel).</summary>
    public ObservableCollection<DetailRow> Details { get; } = [];

    public string DetailsTitle => _selectedRow is null ? "Ressourcen-Details" : "Ressourcen-Details: " + _selectedRow.Name;

    private void UpdateDetails()
    {
        Details.Clear();
        OnPropertyChanged(nameof(DetailsTitle));
        if (_selectedRow is not { } row || _selectedStation is null) return;
        var a = row.Assignment;
        var device = _draft.Device(a.Device);
        var sim = Project.Simulation.FirstOrDefault(r => r.Name == a.Name);
        var output = Project.SimulationOutputs.FirstOrDefault(o => o.Name == a.Name);
        Details.Add(new("Adapter", device is null ? "—" : $"{device.Adapter}"));
        Details.Add(new("Gerät", device?.Name ?? $"unbekannt ({a.Device})"));
        Details.Add(new("Kanal", a.Channel));
        AddCapabilities();

        void AddCapabilities()
        {
            if (sim is not null)
            {
                var range = sim.Min > -1e8 || sim.Max < 1e8
                    ? string.Create(CultureInfo.InvariantCulture, $"{sim.Unit}: {sim.Min:0.##} – {sim.Max:0.##}")
                    : $"{sim.Unit}: unbegrenzt";
                Details.Add(new("Unterstützte Bereiche", range + (sim.Frequency > 0 ? string.Create(CultureInfo.InvariantCulture, $", {sim.Frequency:0.##} Hz") : "")));
            }
            if (output is not null)
                Details.Add(new("Unterstützte Bereiche", string.Create(CultureInfo.InvariantCulture, $"{output.Unit}: {output.Min:0.##} – {output.Max:0.##}, Abtastraten {output.SampleRates} Hz")));
            Details.Add(new("Synchrone Zeitbasis", sim is { Frequency: > 0 } || output is not null ? "Unterstützt" : "—"));
            Details.Add(new("Waveform-Speicher", output is not null ? $"Verfügbar (Kanäle {output.Channels})" : "—"));
            var actions = new List<string> { "Lesen", "Schreiben" };
            if (sim is { Frequency: > 0 }) actions.Add("Kurve aufnehmen");
            if (output is not null) actions.AddRange(["Laden", "Übertragen", "Starten", "Stoppen"]);
            Details.Add(new("Unterstützte Aktionen", string.Join(", ", actions)));
        }

        Details.Add(new("Sperre", a.RequiresLock ? "erforderlich (Reservierung an einer Gruppe)" : "keine"));
        Details.Add(new("Sperrschlüssel", _draft.LockKey(_selectedStation, a)));
        if (device is { Shared: true })
        {
            var key = _draft.LockKey(_selectedStation, a);
            var users = _draft.Stations.SelectMany(s => s.Resources.Where(x => _draft.LockKey(s.Name, x) == key).Select(x => x.Name == a.Name ? s.Name : $"{s.Name} ({x.Name})")).Distinct();
            Details.Add(new("Gemeinsam mit", string.Join(", ", users)));
        }
    }

    // ------------------------------------------------------------------ Reservierungen je Gruppe

    public ObservableCollection<GroupDefinition> Groups { get; } = [];

    public GroupDefinition? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value)) return;
            OnPropertiesChanged(nameof(TimeoutEnabled), nameof(TimeoutSeconds), nameof(GroupPath));
            RefreshLive();
        }
    }

    public string GroupPath => _selectedGroup is null ? "" : PathOf(_selectedGroup);

    private string PathOf(GroupDefinition g)
    {
        var parts = new List<string>();
        foreach (var root in Project.Definition.AllRoots)
            if (Find(root, g, parts)) break;
        return string.Join(" › ", parts);

        static bool Find(GroupDefinition current, GroupDefinition target, List<string> path)
        {
            path.Add(current.Name);
            if (ReferenceEquals(current, target)) return true;
            foreach (var child in current.Children.OfType<GroupDefinition>().Concat(current.ExitGroup is { } e ? [e] : []))
                if (Find(child, target, path)) return true;
            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    private void ReloadGroups()
    {
        var keep = _selectedGroup?.Id;
        Groups.Clear();
        foreach (var g in Project.Definition.AllNodes().OfType<GroupDefinition>().Where(g => g.Resources.Count > 0)) Groups.Add(g);
        _selectedGroup = Groups.FirstOrDefault(g => g.Id == keep) ?? Groups.FirstOrDefault();
        OnPropertiesChanged(nameof(SelectedGroup), nameof(TimeoutEnabled), nameof(TimeoutSeconds), nameof(GroupPath), nameof(HasGroups));
    }

    public bool HasGroups => Groups.Count > 0;

    /// <summary>Optionale maximale Wartezeit der Gruppe (Ablaufeigenschaft; Änderung markiert das Projekt als geändert).</summary>
    public bool TimeoutEnabled
    {
        get => _selectedGroup?.ResourceTimeout is not null;
        set
        {
            if (_selectedGroup is null || value == TimeoutEnabled) return;
            _selectedGroup.ResourceTimeout = value ? TimeSpan.FromSeconds(30) : null;
            _editor.MarkChanged();
            OnPropertiesChanged(nameof(TimeoutEnabled), nameof(TimeoutSeconds));
        }
    }

    public string TimeoutSeconds
    {
        get => _selectedGroup?.ResourceTimeout is { } t ? t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) : "";
        set
        {
            if (_selectedGroup is null) return;
            if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s <= 0)
            {
                Error = "Wartezeit: positive Zahl in Sekunden angeben.";
                return;
            }
            _selectedGroup.ResourceTimeout = TimeSpan.FromSeconds(s);
            _editor.MarkChanged();
            Error = null;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<ReservationRow> Reservations { get; } = [];
    public ObservableCollection<WaitRow> WaitStatus { get; } = [];

    /// <summary>„RessourcenErhalten“ des gewählten Prüfplatzes für die Gruppe (live).</summary>
    public string AcquiredText { get; private set; } = "Nein";

    public bool Acquired { get; private set; }

    /// <summary>Gültigkeitsbereich der aktuellen Sperre (Besitzer / Gruppe).</summary>
    public string ScopeText { get; private set; } = "—";

    // ------------------------------------------------------------------ Live-Status

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            RefreshLive();
        });
    }

    /// <summary>Status aus der Ressourcenvermittlung und dem Zugriffsprotokoll übernehmen.</summary>
    public void RefreshLive()
    {
        _snapshot = _coordinator.Resources.Snapshot();
        var station = _selectedStation;
        RefreshAssignmentStatus();

        void RefreshAssignmentStatus()
        {
            foreach (var row in Rows)
            {
                if (station is null) continue;
                var key = _draft.LockKey(station, row.Assignment);
                var o = _snapshot.FirstOrDefault(x => x.Resource == key);
                (row.Status, row.StatusKind) = o switch
                {
                    { OwnerRunId: not null } when o.OwnerName == station => ($"Reserviert (eigener Lauf){Waiting(o)}", StationStatusKind.Paused),
                    { OwnerRunId: not null } => ($"Reserviert – {o.OwnerName ?? o.OwnerRunId}{Waiting(o)}", StationStatusKind.Waiting),
                    { WaitingNames.Count: > 0 } => ($"Frei – wartend: {string.Join(", ", o.WaitingNames!)}", StationStatusKind.Waiting),
                    _ when _draft.Device(row.Assignment.Device) is null => ("Gerät fehlt", StationStatusKind.Aborting),
                    _ => ("Bereit", StationStatusKind.Running),
                };
            }

        }

        Reservations.Clear();
        WaitStatus.Clear();
        Acquired = false;
        ScopeText = "—";
        if (_selectedGroup is { } g && station is not null)
        {
            var keys = AddReservations(g, station);
            Acquired = keys.Count > 0 && keys.All(k => _snapshot.Any(x => x.Resource == k && x.OwnerName == station));
            RefreshWaitStatus(g);
            var owner = _snapshot.FirstOrDefault(x => keys.Contains(x.Resource) && x.OwnerRunId is not null);
            if (owner is not null)
                ScopeText = $"{owner.OwnerRunId} / {owner.ScopeName ?? "Gruppe"}" +
                            (owner.Since is { } since ? $" seit {since.ToLocalTime():HH:mm:ss}" : "");
        }
        List<string> AddReservations(GroupDefinition g, string station)
        {
            var keyOf = _draft.KeyOf(station);
            var keys = new List<string>();
            foreach (var name in g.Resources)
            {
                var a = _draft.Station(station)?.Resources.FirstOrDefault(x => x.Name == name);
                var key = keyOf(name);
                keys.Add(key);
                var o = _snapshot.FirstOrDefault(x => x.Resource == key);
                var (text, kind) = o switch
                {
                    { OwnerRunId: not null } when o.OwnerName == station => ("Reserviert (eigener Lauf)", StationStatusKind.Paused),
                    { OwnerRunId: not null } => ($"Reserviert ({o.OwnerName ?? o.OwnerRunId})", StationStatusKind.Waiting),
                    _ when a is null => ("nicht zugeordnet", StationStatusKind.Aborting),
                    _ => ("Bereit", StationStatusKind.Running),
                };
                Reservations.Add(new ReservationRow(name, a is null ? "—" : _draft.Device(a.Device)?.Name ?? a.Device, a?.Channel ?? "—", key, text, kind));
            }
            return keys;
        }

        void RefreshWaitStatus(GroupDefinition g)
        {
            foreach (var st in _draft.Stations)
            {
                var stKeys = g.Resources.Select(_draft.KeyOf(st.Name)).ToList();
                var owns = stKeys.Any(k => _snapshot.Any(x => x.Resource == k && x.OwnerName == st.Name));
                var waits = stKeys.Any(k => _snapshot.Any(x => x.Resource == k && (x.WaitingNames ?? []).Contains(st.Name)));
                WaitStatus.Add(new WaitRow(st.Name, owns ? "Eigentümer" : waits ? "In Warteschlange" : "—", owns, waits));
            }
        }

        AcquiredText = Acquired ? "Ja" : "Nein";
        OnPropertiesChanged(nameof(Acquired), nameof(AcquiredText), nameof(ScopeText));

        Accesses.Clear();
        foreach (var a in _coordinator.Journal.Recent().Take(60))
            Accesses.Add(new AccessRow(a.Start.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), a.Station, a.Resource, a.Operation, a.Key, a.Conflict));
        OnPropertiesChanged(nameof(AccessSummary), nameof(HasConflicts));
    }

    private static string Waiting(ResourceOwnership o) => o.WaitingNames is { Count: > 0 } w ? " · wartend: " + string.Join(", ", w) : "";

    // ------------------------------------------------------------------ Zugriffsprotokoll

    public ObservableCollection<AccessRow> Accesses { get; } = [];

    public bool HasConflicts => _coordinator.Journal.Conflicts.Count > 0;

    public string AccessSummary
    {
        get
        {
            var conflicts = _coordinator.Journal.Conflicts.Count;
            return $"{_coordinator.Journal.Count} Gerätezugriffe · " +
                   (conflicts == 0 ? "keine widersprüchlichen Zugriffe" : $"{conflicts} WIDERSPRÜCHLICHE Zugriffe");
        }
    }

    // ------------------------------------------------------------------ Prüfung und Speichern

    public ObservableCollection<ConfigIssue> Issues { get; } = [];
    public bool HasConfigErrors => Issues.Any(i => i.IsError);

    public string CheckSummary
    {
        get
        {
            var errors = Issues.Count(i => i.IsError);
            var warnings = Issues.Count(i => i.Severity == ConfigSeverity.Warning);
            return errors > 0 ? $"{errors} Fehler – Konfiguration verweigert" : warnings > 0 ? $"In Ordnung, {warnings} Hinweis(e)" : "Konfiguration in Ordnung";
        }
    }

    /// <summary>„Simulation prüfen“: Zuordnung, gemeinsame Geräte, Zyklusrisiko.</summary>
    public void Check()
    {
        Issues.Clear();
        foreach (var i in StationHardware.Check(_draft, Project).OrderByDescending(i => i.Severity)) Issues.Add(i);
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
            Error = "Nicht gespeichert: " + string.Join(" ", Issues.Where(i => i.IsError).Select(i => i.Text));
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

/// <summary>Eigenschaft in den Ressourcen-Details.</summary>
public sealed record DetailRow(string Label, string Value);
