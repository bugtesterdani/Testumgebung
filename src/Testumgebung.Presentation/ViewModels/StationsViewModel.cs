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
            NormalizeDaqConnection();
            OnPropertiesChanged(nameof(Adapter), nameof(Models), nameof(Model), nameof(Resource), nameof(IsReal), nameof(HasCommandTimeout), nameof(Notes), nameof(Text), nameof(ConnectionChoices), nameof(HasConnectionChoices), nameof(Connection), nameof(OptionsText));
            _changed();
        }
    }

    public bool IsReal => !Device.IsSimulation;
    public bool HasCommandTimeout => IsReal && !DaqModels.IsDaq(Adapter);
    public IReadOnlyList<string> Models => ProjectInstruments.Family(_project(), Device.Adapter)?.Models ?? [];
    public string Notes => ProjectInstruments.Family(_project(), Device.Adapter)?.Notes ?? "Simuliertes Gerät: Verhalten aus dem Simulationsprofil des Projekts.";

    public string? Model
    {
        get => Device.Model;
        set { if (Device.Model != value) { Device.Model = value; NormalizeDaqConnection(); OnPropertiesChanged(nameof(Model), nameof(Text), nameof(ConnectionChoices), nameof(HasConnectionChoices), nameof(Connection), nameof(OptionsText)); _changed(); } }
    }

    public string? Resource
    {
        get => Device.Resource;
        set { if (Device.Resource != value) { Device.Resource = value?.Trim(); OnPropertyChanged(); _changed(); } }
    }

    /// <summary>Optionen, eine Zeile je „Schlüssel=Wert“.</summary>
    public string OptionsText
    {
        get => DaqModels.IsDaq(Adapter) ? string.Join(Environment.NewLine, (Device.Options ?? []).Where(pair => !pair.Key.Equals("Verbindung", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key + "=" + pair.Value)) : Device.OptionsText;
        set
        {
            if (OptionsText == value) return;
            var connection = Connection;
            Device.OptionsText = value;
            if (HasConnectionChoices && connection is not null) (Device.Options ??= new(StringComparer.OrdinalIgnoreCase))["Verbindung"] = connection;
            OnPropertiesChanged(nameof(OptionsText), nameof(Connection)); _changed();
        }
    }

    public IReadOnlyList<string> ConnectionChoices => Adapter == DaqModels.Simulation ? ["Simulation"] : DaqModels.All.FirstOrDefault(model => model.Name.Equals(Model, StringComparison.OrdinalIgnoreCase) && (Adapter == DaqModels.Simulation || (model.Sdk == DaqSdk.UniversalLibrary) == (Adapter == DaqModels.RedLab)) && DaqModels.IsDaq(Adapter))?.Connections ?? [];
    public bool HasConnectionChoices => ConnectionChoices.Count > 0;
    public string? Connection
    {
        get => new InstrumentOptions(Device.Options).Text("Verbindung") ?? ConnectionChoices.FirstOrDefault();
        set
        {
            if (value is null || !ConnectionChoices.Contains(value) || value == Connection) return;
            (Device.Options ??= new(StringComparer.OrdinalIgnoreCase))["Verbindung"] = value;
            OnPropertiesChanged(nameof(Connection), nameof(OptionsText)); _changed();
        }
    }
    private void NormalizeDaqConnection()
    {
        if (!HasConnectionChoices || ConnectionChoices.Contains(Connection!)) return;
        (Device.Options ??= new(StringComparer.OrdinalIgnoreCase))["Verbindung"] = ConnectionChoices[0];
    }

    public string TimeoutText
    {
        get => Device.TimeoutMs?.ToString(CultureInfo.InvariantCulture) ?? "";
        set
        {
            var parsedValue = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0 ? ms : (int?)null;
            if (Device.TimeoutMs == parsedValue) return;
            Device.TimeoutMs = parsedValue;
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
        nameof(OptionsText), nameof(TimeoutText), nameof(IsReal), nameof(HasCommandTimeout), nameof(Notes), nameof(Text), nameof(LockPerChannel));

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
        get => _owner.Devices.FirstOrDefault(deviceRow => deviceRow.Id == Assignment.Device);
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
        if (Station is { } station)
            foreach (var assignment in station.Resources) Rows.Add(new AssignmentRow(this, assignment));
        SelectedRow = Rows.FirstOrDefault(assignmentRow => assignmentRow.Assignment.RequiresLock) ?? Rows.FirstOrDefault();
        AddResourceCommand.RaiseCanExecuteChanged();
        AddMissingCommand.RaiseCanExecuteChanged();
        RefreshLive();
    }

    internal string UnitOf(ResourceAssignment a)
        => Channels(a.Device)?.FirstOrDefault(channel => string.Equals(channel.Name, a.Channel, StringComparison.OrdinalIgnoreCase))?.Unit
           ?? Project.Simulation.FirstOrDefault(resource => resource.Name == a.Name)?.Unit
           ?? Project.SimulationOutputs.FirstOrDefault(output => output.Name == a.Name)?.Unit
           ?? "—";

    private readonly Dictionary<string, (string Signature, IReadOnlyList<InstrumentChannel>? Channels)> _channelCache = new(StringComparer.Ordinal);

    /// <summary>Kanäle eines realen Geräts (zwischengespeichert, solange Adapter/Modell/Ressource gleich bleiben).</summary>
    internal IReadOnlyList<InstrumentChannel>? Channels(string deviceId)
    {
        if (_draft.Device(deviceId) is not { IsSimulation: false } device) return null;
        var signature = $"{device.Adapter}|{device.Model}|{device.Resource}|{device.OptionsText}";
        if (_channelCache.TryGetValue(deviceId, out var cached) && cached.Signature == signature) return cached.Channels;
        var channels = StationHardware.ChannelsOf(device, Project);
        _channelCache[deviceId] = (signature, channels);
        return channels;
    }

    internal IReadOnlyList<string> ChannelNames(string deviceId) => Channels(deviceId)?.Select(channel => channel.Name).ToList() ?? [];

    private void AddResource()
    {
        if (Station is not { } station) return;
        var index = 1;
        while (station.Resources.Any(assignment => assignment.Name == $"Neue_Ressource_{index}")) index++;
        var assignment = new ResourceAssignment { Name = $"Neue_Ressource_{index}", Device = StationSetup.LocalSimulation, Channel = $"ch{index}" };
        if (_draft.Device(assignment.Device) is null) assignment.Device = _draft.Devices.FirstOrDefault()?.Id ?? "";
        station.Resources.Add(assignment);
        var row = new AssignmentRow(this, assignment);
        Rows.Add(row);
        SelectedRow = row;
        Edited();
    }

    private void RemoveResource()
    {
        if (Station is not { } station || _selectedRow is null) return;
        station.Resources.Remove(_selectedRow.Assignment);
        Rows.Remove(_selectedRow);
        SelectedRow = Rows.FirstOrDefault();
        Edited();
    }

    /// <summary>Vom Ablauf verwendete, noch nicht zugeordnete Namen nach dem Projektvorschlag ergänzen.</summary>
    private void AddMissing()
    {
        if (Station is not { } station) return;
        var added = StationSetup.Suggest(Project, station.Resources).ToList();
        foreach (var name in StationSetup.UsedResources(Project).Where(usedResource => station.Resources.All(assignment => assignment.Name != usedResource) && added.All(assignment => assignment.Name != usedResource)))
            added.Add(new ResourceAssignment { Name = name, Device = StationSetup.LocalSimulation, Channel = name });
        station.Resources.AddRange(added);
        LoadRows();
        Edited();
        Status = added.Count == 0 ? "Alle verwendeten Ressourcen sind zugeordnet." : $"{added.Count} Ressource(n) ergänzt: {string.Join(", ", added.Select(assignment => assignment.Name))}.";
    }

    /// <summary>Auswahl der Adapterfamilie: Simulation und die Treiber aus dem Gerätekatalog.</summary>
    public IReadOnlyList<string> AdapterChoices => [StationSetup.SimulationAdapter, .. InstrumentCatalog.Families.Select(instrumentFamily => instrumentFamily.Adapter), .. Project.DeviceProfiles.Select(customDeviceProfile => customDeviceProfile.Adapter)];

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
        var users = _draft.Stations.Where(st => st.Resources.Any(assignment => assignment.Device == row.Id)).Select(st => st.Name).ToList();
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
        foreach (var found2 in found) Discovered.Add(found2);
        Status = $"{found.Count(found3 => found3.StartsWith("VISA: ", StringComparison.Ordinal) && found3.Contains("::", StringComparison.Ordinal)) + found.Count(found3 => found3.StartsWith("NI-DAQmx: ", StringComparison.Ordinal) && found3.Contains(" – ", StringComparison.Ordinal))} Gerät(e) gefunden.";
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
            var device = _draft.Device(id);
            if (device is null)
            {
                device = new DeviceConfig { Id = id };
                _draft.Devices.Add(device);
                Devices.Add(new DeviceRow(device, Edited, () => Project));
            }
            (device.Name, device.Adapter, device.Model, device.Resource, device.Shared) = (name, adapter, model, resource, true);
            device.OptionsText = options ?? "";
        }
        foreach (var row in Devices) row.RaiseAllDevice();
        Edited();
        Status = "Laborsimulatoren laufen (localhost) und sind als gemeinsame Geräte eingetragen – Zuordnung anpassen und speichern. " +
                 "Verdrahtung: DMM VAC/IAC ← AC-Quelle, VDC/IDC ← Netzgerät CH1, Oszilloskop CHAN1 ← AC-Quelle, CHAN2/3 ← Generator.";
    }

    private void AddDevice()
    {
        var index = 1;
        while (_draft.Devices.Any(device => device.Id == $"geraet-{index}")) index++;
        var device = new DeviceConfig { Id = $"geraet-{index}", Name = $"Gerät {index} (Simulation)" };
        _draft.Devices.Add(device);
        Devices.Add(new DeviceRow(device, Edited, () => Project));
        Edited();
    }

    private void RestoreSuggestion()
    {
        _draft = StationSetup.Default(Project, _draft.Stations.Select(station => station.Name).ToList());
        Devices.Clear();
        foreach (var device in _draft.Devices) Devices.Add(new DeviceRow(device, Edited, () => Project));
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
        var assignment = row.Assignment;
        var device = _draft.Device(assignment.Device);
        var sim = Project.Simulation.FirstOrDefault(resource => resource.Name == assignment.Name);
        var output = Project.SimulationOutputs.FirstOrDefault(output2 => output2.Name == assignment.Name);
        Details.Add(new("Adapter", device is null ? "—" : $"{device.Adapter}"));
        Details.Add(new("Gerät", device?.Name ?? $"unbekannt ({assignment.Device})"));
        Details.Add(new("Kanal", assignment.Channel));
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

        Details.Add(new("Sperre", assignment.RequiresLock ? "erforderlich (Reservierung an einer Gruppe)" : "keine"));
        Details.Add(new("Sperrschlüssel", _draft.LockKey(_selectedStation, assignment)));
        if (device is { Shared: true })
        {
            var key = _draft.LockKey(_selectedStation, assignment);
            var users = _draft.Stations.SelectMany(station => station.Resources.Where(assignment2 => _draft.LockKey(station.Name, assignment2) == key).Select(assignment2 => assignment2.Name == assignment.Name ? station.Name : $"{station.Name} ({assignment2.Name})")).Distinct();
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

    private string PathOf(GroupDefinition group2)
    {
        var parts = new List<string>();
        foreach (var root in Project.Definition.AllRoots)
            if (Find(root, group2, parts)) break;
        return string.Join(" › ", parts);

        static bool Find(GroupDefinition current, GroupDefinition target, List<string> path)
        {
            path.Add(current.Name);
            if (ReferenceEquals(current, target)) return true;
            foreach (var child in current.Children.OfType<GroupDefinition>().Concat(current.ExitGroup is { } group ? [group] : []))
                if (Find(child, target, path)) return true;
            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    private void ReloadGroups()
    {
        var keep = _selectedGroup?.Id;
        Groups.Clear();
        foreach (var group in Project.Definition.AllNodes().OfType<GroupDefinition>().Where(group2 => group2.Resources.Count > 0)) Groups.Add(group);
        _selectedGroup = Groups.FirstOrDefault(group2 => group2.Id == keep) ?? Groups.FirstOrDefault();
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
        get => _selectedGroup?.ResourceTimeout is { } duration ? duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) : "";
        set
        {
            if (_selectedGroup is null) return;
            if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var sample) || sample <= 0)
            {
                Error = "Wartezeit: positive Zahl in Sekunden angeben.";
                return;
            }
            _selectedGroup.ResourceTimeout = TimeSpan.FromSeconds(sample);
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
                var ownership = _snapshot.FirstOrDefault(ownership2 => ownership2.Resource == key);
                (row.Status, row.StatusKind) = ownership switch
                {
                    { OwnerRunId: not null } when ownership.OwnerName == station => ($"Reserviert (eigener Lauf){Waiting(ownership)}", StationStatusKind.Paused),
                    { OwnerRunId: not null } => ($"Reserviert – {ownership.OwnerName ?? ownership.OwnerRunId}{Waiting(ownership)}", StationStatusKind.Waiting),
                    { WaitingNames.Count: > 0 } => ($"Frei – wartend: {string.Join(", ", ownership.WaitingNames!)}", StationStatusKind.Waiting),
                    _ when _draft.Device(row.Assignment.Device) is null => ("Gerät fehlt", StationStatusKind.Aborting),
                    _ => ("Bereit", StationStatusKind.Running),
                };
            }

        }

        Reservations.Clear();
        WaitStatus.Clear();
        Acquired = false;
        ScopeText = "—";
        if (_selectedGroup is { } group && station is not null)
        {
            var keys = AddReservations(group, station);
            Acquired = keys.Count > 0 && keys.All(key2 => _snapshot.Any(ownership2 => ownership2.Resource == key2 && ownership2.OwnerName == station));
            RefreshWaitStatus(group);
            var owner = _snapshot.FirstOrDefault(ownership2 => keys.Contains(ownership2.Resource) && ownership2.OwnerRunId is not null);
            if (owner is not null)
                ScopeText = $"{owner.OwnerRunId} / {owner.ScopeName ?? "Gruppe"}" +
                            (owner.Since is { } since ? $" seit {since.ToLocalTime():HH:mm:ss}" : "");
        }
        List<string> AddReservations(GroupDefinition group2, string station)
        {
            var keyOf = _draft.KeyOf(station);
            var keys = new List<string>();
            foreach (var name in group2.Resources)
            {
                var assignment = _draft.Station(station)?.Resources.FirstOrDefault(assignment2 => assignment2.Name == name);
                var key = keyOf(name);
                keys.Add(key);
                var ownership2 = _snapshot.FirstOrDefault(ownership3 => ownership3.Resource == key);
                var (text, kind) = ownership2 switch
                {
                    { OwnerRunId: not null } when ownership2.OwnerName == station => ("Reserviert (eigener Lauf)", StationStatusKind.Paused),
                    { OwnerRunId: not null } => ($"Reserviert ({ownership2.OwnerName ?? ownership2.OwnerRunId})", StationStatusKind.Waiting),
                    _ when assignment is null => ("nicht zugeordnet", StationStatusKind.Aborting),
                    _ => ("Bereit", StationStatusKind.Running),
                };
                Reservations.Add(new ReservationRow(name, assignment is null ? "—" : _draft.Device(assignment.Device)?.Name ?? assignment.Device, assignment?.Channel ?? "—", key, text, kind));
            }
            return keys;
        }

        void RefreshWaitStatus(GroupDefinition group3)
        {
            foreach (var st in _draft.Stations)
            {
                var stKeys = group3.Resources.Select(_draft.KeyOf(st.Name)).ToList();
                var owns = stKeys.Any(stKey => _snapshot.Any(ownership3 => ownership3.Resource == stKey && ownership3.OwnerName == st.Name));
                var waits = stKeys.Any(stKey => _snapshot.Any(ownership3 => ownership3.Resource == stKey && (ownership3.WaitingNames ?? []).Contains(st.Name)));
                WaitStatus.Add(new WaitRow(st.Name, owns ? "Eigentümer" : waits ? "In Warteschlange" : "—", owns, waits));
            }
        }

        AcquiredText = Acquired ? "Ja" : "Nein";
        OnPropertiesChanged(nameof(Acquired), nameof(AcquiredText), nameof(ScopeText));

        Accesses.Clear();
        foreach (var hardwareAccess in _coordinator.Journal.Recent().Take(60))
            Accesses.Add(new AccessRow(hardwareAccess.Start.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), hardwareAccess.Station, hardwareAccess.Resource, hardwareAccess.Operation, hardwareAccess.Key, hardwareAccess.Conflict));
        OnPropertiesChanged(nameof(AccessSummary), nameof(HasConflicts));
    }

    private static string Waiting(ResourceOwnership ownership) => ownership.WaitingNames is { Count: > 0 } items ? " · wartend: " + string.Join(", ", items) : "";

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

/// <summary>Eigenschaft in den Ressourcen-Details.</summary>
public sealed record DetailRow(string Label, string Value);
