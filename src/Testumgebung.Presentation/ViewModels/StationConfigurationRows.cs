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

/// <summary>Eigenschaft in den Ressourcen-Details.</summary>
public sealed record DetailRow(string Label, string Value);
