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

public sealed partial class StationsViewModel
{
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
}
