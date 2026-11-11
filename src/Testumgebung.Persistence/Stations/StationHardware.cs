/**
 * @file
 * @brief Prüft Gerätezuordnungen und vermittelt reale oder simulierte Geräte zwischen den Prüfplätzen.
 */

using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;
using Testumgebung.Hardware;
using Testumgebung.Instruments;

namespace Testumgebung.Persistence.Stations;

/// <summary>Befund der Konfigurationsprüfung.</summary>
public enum ConfigSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record ConfigIssue(ConfigSeverity Severity, string? Station, string? Resource, string Text, string? Suggestion = null)
{
    public bool IsError => Severity == ConfigSeverity.Error;

    public override string ToString() => $"{Severity}: {(Station is null ? "" : Station + ": ")}{Text}";
}

/// <summary>
/// Geräteinstanzen des Rechners. Ein gemeinsames Gerät gibt es genau einmal (alle Prüfplätze greifen auf dieselbe Instanz zu),
/// lokale Geräte je Prüfplatz einmal. Instanzen leben über Läufe hinweg – wie reale Hardware.
/// </summary>
public sealed class DevicePool
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IHardwareAdapter> _devices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _configured = new(StringComparer.Ordinal);

    public IClock? Clock { get; init; }

    /// <summary>Instanzschlüssel: reale Geräte gibt es physisch einmal, Simulationen lokal je Prüfplatz.</summary>
    public static string InstanceKey(DeviceConfig device, string station)
        => device.Shared || !device.IsSimulation ? device.Id : $"{station}/{device.Id}";

    /// <summary>Gemeinsames Gerät bzw. lokales Gerät eines Prüfplatzes (reale Geräte: Treiber, verbindet beim ersten Zugriff).</summary>
    public IHardwareAdapter Get(StationSetup setup, string station, string deviceId, TestProject? project = null)
    {
        var device = setup.Device(deviceId) ?? throw new StationSetupException($"Gerät '{deviceId}' ist nicht konfiguriert.");
        var key = InstanceKey(device, station);
        lock (_gate)
        {
            if (_devices.TryGetValue(key, out var adapter))
            {
                if (adapter is CustomTextInstrument custom && custom.ProfileSignature != ProjectInstruments.Profile(project, device.Adapter)?.Signature())
                    throw new StationSetupException($"Gerät „{device.Name}“: Geräteprofil wurde geändert. Zuordnung ohne laufende Prüfung erneut speichern.");
                return adapter;
            }
            if (device.IsSimulation)
            {
                adapter = new SimulationAdapter(device.Shared ? device.Name : $"{device.Name} – {station}") { Clock = Clock };
            }
            else
            {
                if (string.IsNullOrWhiteSpace(device.Resource))
                    throw new StationSetupException($"Gerät „{device.Name}“: keine Ressource (Adresse) angegeben.");
                try
                {
                    adapter = ProjectInstruments.Create(project, device);
                }
                catch (InstrumentException ex)
                {
                    throw new StationSetupException($"Gerät „{device.Name}“: {ex.Message}", ex);
                }
            }
            _devices[key] = adapter;
            return adapter;
        }
    }

    /// <summary>Kanal eines Geräts nur einmal einrichten (laufende Ausgaben anderer Prüfplätze bleiben unberührt).</summary>
    internal bool FirstConfiguration(SimulationAdapter adapter, string channel)
    {
        lock (_gate) return _configured.Add($"{adapter.GetHashCode()}|{adapter.Name}|{channel}");
    }

    /// <summary>Alle Instanzen verwerfen (nach Speichern der Konfiguration, nur ohne laufende Prüfung); Verbindungen schließen.</summary>
    public void Reset()
    {
        List<IHardwareAdapter> old;
        lock (_gate)
        {
            old = _devices.Values.ToList();
            _devices.Clear();
            _configured.Clear();
        }
        foreach (var iDisposable in old.OfType<IDisposable>()) iDisposable.Dispose();
    }

    /// <summary>Simulierte Geräte (Tests, Anzeige).</summary>
    public IReadOnlyDictionary<string, SimulationAdapter> Instances
    {
        get { lock (_gate) return _devices.Where(kv => kv.Value is SimulationAdapter).ToDictionary(kv => kv.Key, kv => (SimulationAdapter)kv.Value); }
    }

    /// <summary>Alle angelegten Geräte (Instanzschlüssel → Adapter).</summary>
    public IReadOnlyDictionary<string, IHardwareAdapter> Adapters
    {
        get { lock (_gate) return new Dictionary<string, IHardwareAdapter>(_devices); }
    }
}

/// <summary>Prüfplatzspezifische Hardwarezuordnung aus Konfiguration, Gerätepool und Simulationsprofil des Projekts.</summary>
public static class StationHardware
{
    /// <summary>Registry eines Prüfplatzes: Namen → Gerät/Kanal mit Sperrschlüssel.</summary>
    public static HardwareRegistry Build(StationSetup setup, string station, TestProject project, DevicePool pool, int seed = 1)
    {
        var config = setup.Station(station) ?? throw new StationSetupException($"Prüfplatz '{station}' ist nicht konfiguriert.");
        var random = new Random(seed);
        var registry = new HardwareRegistry();
        foreach (var assignment in config.Resources)
        {
            var adapter = pool.Get(setup, station, assignment.Device, project);
            var key = setup.LockKey(station, assignment);
            if (adapter is SimulationAdapter sim && pool.FirstConfiguration(sim, assignment.Channel))
                Configure(sim, assignment, project, random, seed);
            registry.Bind(assignment.Name, adapter, assignment.Channel, assignment.RequiresLock, key);
        }
        return registry;
    }

    /// <summary>Simulationsverhalten aus dem Projekt (Wert, Rauschen, Sinus, Kurvenausgang) für den zugeordneten Kanal.</summary>
    private static void Configure(SimulationAdapter adapter, ResourceAssignment assignment, TestProject project, Random random, int seed)
    {
        if (project.SimulationComm.FirstOrDefault(communication => communication.Name == assignment.Name) is { } comm)
            adapter.ConfigureCommunication(assignment.Channel, CommSimulation.Create(comm.Kind, comm.Rules, seed));
        if (project.Simulation.FirstOrDefault(resource => resource.Name == assignment.Name) is { } resource)
        {
            adapter.Channel(assignment.Channel, new ChannelCapabilities(resource.Unit, resource.Min, resource.Max));
            if (resource.Noise > 0)
            {
                var (value, noise) = (resource.Value, resource.Noise);
                adapter.SetInput(assignment.Channel, () =>
                {
                    lock (random) return Math.Round(value + (random.NextDouble() * 2 - 1) * noise, 3);
                });
            }
            else if (resource.Value != 0)
            {
                adapter.SetInput(assignment.Channel, resource.Value);
            }
            if (resource.Frequency > 0) adapter.SetSine(assignment.Channel, resource.Value, resource.Frequency, resource.Phase);
        }
        if (project.SimulationOutputs.FirstOrDefault(output => output.Name == assignment.Name) is { } output)
        {
            var rates = output.SampleRates.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => double.Parse(entry, CultureInfo.InvariantCulture)).ToList();
            // Rückwirkung über Namen: Kurvenkanal → Messkanal desselben Geräts (laut Zuordnung dieses Prüfplatzes)
            var feedback = output.Feedback.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Split('=', 2, StringSplitOptions.TrimEntries)).Where(parts => parts.Length == 2).ToDictionary(parts => parts[0], parts => parts[1]);
            adapter.ConfigureOutput(assignment.Channel, new OutputCapabilities($"{output.Name} ({adapter.Name})", output.Unit, output.Min, output.Max,
                output.Channels.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), rates, output.ExplicitTimes, output.MaxSamples), feedback);
        }
    }

    /// <summary>
    /// Konfigurationsprüfung: Zuordnung vollständig, Geräte bekannt, gemeinsame Geräte nur mit Sperre, keine
    /// Zyklusrisiken der Sperrreihenfolge über alle Prüfplätze (mit deren Sperrschlüsseln).
    /// </summary>
    private static void CheckProfiles(TestProject project, List<ConfigIssue> issues)
    {
        foreach (var duplicate in project.DeviceProfiles.GroupBy(customDeviceProfile => customDeviceProfile.Id).Where(group => group.Count() > 1))
            issues.Add(new(ConfigSeverity.Error, null, null, $"Geräteprofil-ID '{duplicate.Key}' ist mehrfach vergeben."));
        foreach (var profile in project.DeviceProfiles)
            foreach (var error in profile.Validate()) issues.Add(new(ConfigSeverity.Error, null, null, $"{profile.Name}: {error}"));
    }

    public static IReadOnlyList<ConfigIssue> Check(StationSetup setup, TestProject project)
    {
        var issues = new List<ConfigIssue>();
        CheckProfiles(project, issues);
        foreach (var dup in setup.Devices.GroupBy(device => device.Id).Where(group => group.Count() > 1))
            issues.Add(new(ConfigSeverity.Error, null, null, $"Geräte-ID '{dup.Key}' ist mehrfach vergeben."));
        var channelsOf = new Dictionary<string, IReadOnlyList<InstrumentChannel>?>(StringComparer.Ordinal);
        CheckDevices();
        if (setup.Stations.Count == 0)
            issues.Add(new(ConfigSeverity.Error, null, null, "Kein Prüfplatz konfiguriert."));
        foreach (var dup in setup.Stations.GroupBy(station => station.Name).Where(group => group.Count() > 1 || string.IsNullOrWhiteSpace(group.Key)))
            issues.Add(new(ConfigSeverity.Error, dup.Key, null, string.IsNullOrWhiteSpace(dup.Key) ? "Prüfplatz ohne Namen." : "Prüfplatzname ist mehrfach vergeben."));

        void CheckDevices()
        {
            foreach (var device2 in setup.Devices.Where(device3 => !device3.IsSimulation))
            {
                if (ProjectInstruments.Family(project, device2.Adapter) is not { } family)
                {
                    issues.Add(new(ConfigSeverity.Error, null, null, $"Gerät „{device2.Name}“: unbekannter Adapter „{device2.Adapter}“."));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(device2.Resource))
                    issues.Add(new(ConfigSeverity.Error, null, null, $"Gerät „{device2.Name}“: keine Ressource angegeben.", "z. B. " + family.ResourceExample));
                else if (ProjectInstruments.Profile(project, device2.Adapter) is { } profile &&
                         ProjectInstruments.ResourceError(profile.Interface, device2.Resource) is { } resourceError)
                    issues.Add(new(ConfigSeverity.Error, null, null, $"Gerät „{device2.Name}“: {resourceError}"));
                if (!string.IsNullOrWhiteSpace(device2.Model) && !family.Models.Contains(device2.Model) && family.Adapter != InstrumentCatalog.DaqmxReplica && family.Adapter != DaqmxDevice.AdapterName)
                    issues.Add(new(ConfigSeverity.Warning, null, null, $"Gerät „{device2.Name}“: Modell {device2.Model} ist nicht erprobt – Kanalsatz und Grenzen der Familie werden verwendet."));
                channelsOf[device2.Id] = ChannelsOf(device2, project);
                if (channelsOf[device2.Id] is null && device2.Adapter == DaqmxDevice.AdapterName)
                    issues.Add(new(ConfigSeverity.Warning, null, null, $"Gerät „{device2.Name}“: Kanäle nicht prüfbar – NI-DAQmx nicht installiert oder Gerät {device2.Resource} nicht vorhanden."));
                // Ein reales Gerät gibt es nur einmal: ohne „gemeinsam“ darf es nur ein Prüfplatz verwenden
                var users = setup.Stations.Where(st => st.Resources.Any(assignment => assignment.Device == device2.Id)).Select(st => st.Name).ToList();
                if (!device2.Shared && users.Count > 1)
                    issues.Add(new(ConfigSeverity.Error, null, null,
                        $"Gerät „{device2.Name}“ ist nicht gemeinsam, wird aber von {string.Join(", ", users)} verwendet – widersprüchlicher Zugriff möglich.",
                        "Als „gemeinsam“ markieren (Zugriff dann nur mit Sperre) oder je Prüfplatz ein eigenes Gerät."));
            }
        }

        var used = StationSetup.UsedResources(project);
        CheckStations();

        void CheckStations()
        {
            foreach (var station in setup.Stations)
            {
                foreach (var dup in station.Resources.GroupBy(assignment => assignment.Name).Where(group => group.Count() > 1))
                    issues.Add(new(ConfigSeverity.Error, station.Name, dup.Key, $"'{dup.Key}' ist mehrfach zugeordnet."));
                void CheckAssignment(ResourceAssignment assignment)
                {
                    var device = setup.Device(assignment.Device);
                    if (device is null)
                    {
                        issues.Add(new(ConfigSeverity.Error, station.Name, assignment.Name, $"{assignment.Name}: Gerät '{assignment.Device}' ist nicht konfiguriert."));
                        return;
                    }
                    CheckChannel(assignment, device);
                    if (device.Shared && !assignment.RequiresLock)
                        issues.Add(new(ConfigSeverity.Error, station.Name, assignment.Name,
                            $"{assignment.Name} liegt auf dem gemeinsamen Gerät „{device.Name}“ ohne Sperre – widersprüchlicher Zugriff mehrerer Prüfplätze möglich.",
                            "„Sperre erforderlich“ setzen und die Ressource an der Gruppe reservieren."));
                    if (device.IsSimulation && !project.Simulation.Any(resource => resource.Name == assignment.Name) && !project.SimulationOutputs.Any(output => output.Name == assignment.Name) && !project.SimulationComm.Any(communication => communication.Name == assignment.Name) && used.Contains(assignment.Name))
                        issues.Add(new(ConfigSeverity.Warning, station.Name, assignment.Name, $"{assignment.Name}: kein Simulationsverhalten im Projekt – liefert 0."));
                }

                void CheckChannel(ResourceAssignment assignment2, DeviceConfig device)
                {
                    if (string.IsNullOrWhiteSpace(assignment2.Channel))
                        issues.Add(new(ConfigSeverity.Error, station.Name, assignment2.Name, $"{assignment2.Name}: kein Kanal angegeben."));
                    else if (InstrumentCatalog.IsCommunication(device.Adapter))
                    {
                        if (!CommChannelOk(device, assignment2.Channel))
                            issues.Add(new(ConfigSeverity.Error, station.Name, assignment2.Name, $"{assignment2.Name}: Kanal „{assignment2.Channel}“ ist für {device.Adapter} ungültig.",
                                "Modbus: Unit-ID (z. B. 1) oder Punkt 1/HR100:float32."));
                    }
                    else if (channelsOf.TryGetValue(device.Id, out var channels) && channels is not null)
                    {
                        if (channels.FirstOrDefault(channel2 => string.Equals(channel2.Name, assignment2.Channel, StringComparison.OrdinalIgnoreCase)) is not { } channel)
                            issues.Add(new(ConfigSeverity.Error, station.Name, assignment2.Name, $"{assignment2.Name}: Kanal „{assignment2.Channel}“ gibt es bei {device.Model ?? device.Adapter} nicht.",
                                "Vorhanden: " + string.Join(", ", channels.Take(24).Select(channel2 => channel2.Name)) + (channels.Count > 24 ? " …" : "")));
                        else if (project.Simulation.FirstOrDefault(resource => resource.Name == assignment2.Name) is { } sim && sim.Unit != channel.Unit
                                 && !(sim.Unit == "bool" && channel.Unit == "bool"))
                            issues.Add(new(ConfigSeverity.Warning, station.Name, assignment2.Name, $"{assignment2.Name}: Ablauf erwartet {sim.Unit}, Gerätekanal liefert {channel.Unit} – keine Umrechnung."));
                    }
                }

                foreach (var assignment3 in station.Resources)
                {
                    CheckAssignment(assignment3);
                }
                foreach (var missing in used.Where(used2 => station.Resources.All(assignment4 => assignment4.Name != used2)))
                    issues.Add(new(ConfigSeverity.Error, station.Name, missing, $"{missing} wird im Ablauf verwendet, ist aber nicht zugeordnet.",
                        "„Fehlende aus Projekt übernehmen“."));
                // Gesperrte Hardware muss im Ablauf an einer Gruppe reserviert werden
                var reserved = project.Definition.EnabledNodes().OfType<GroupDefinition>().SelectMany(group => group.Resources).ToHashSet(StringComparer.Ordinal);
                foreach (var assignment4 in station.Resources.Where(assignment5 => assignment5.RequiresLock && used.Contains(assignment5.Name)))
                {
                    var key = setup.LockKey(station.Name, assignment4);
                    var keyOf = setup.KeyOf(station.Name);
                    if (!reserved.Any(reserved2 => keyOf(reserved2) == key))
                        issues.Add(new(ConfigSeverity.Warning, station.Name, assignment4.Name, $"{assignment4.Name} erfordert eine Sperre, wird im Ablauf aber an keiner Gruppe reserviert – Zugriff ergibt ERROR."));
                }
            }

        }

        // Gemeinsame Kanäle: Übersicht
        foreach (var shared in setup.Stations
                     .SelectMany(station2 => station2.Resources.Select(assignment5 => (Station: station2.Name, A: assignment5)))
                     .Where(entry => setup.Device(entry.A.Device) is { Shared: true })
                     .GroupBy(entry => setup.LockKey(entry.Station, entry.A)))
        {
            var users = shared.Select(entry => entry.A.Name == shared.First().A.Name ? entry.Station : $"{entry.Station} als {entry.A.Name}").Distinct();
            issues.Add(new(ConfigSeverity.Info, null, shared.First().A.Name,
                $"Gemeinsam: {shared.First().A.Name} ({string.Join(", ", users)}) – eine Sperre für Gerät/Kanal {shared.Key}."));
        }

        if (CycleRisk(setup, setup.Stations.Select(station2 => (station2.Name, project.Definition))) is { } cycle)
            issues.Add(new(ConfigSeverity.Error, null, null, cycle.Text + ". Start wird verweigert.", cycle.Suggestion));
        return issues;
    }

    /// <summary>Kommunikationsgeräte: freie Kanalnamen, Modbus prüft Unit-ID bzw. Punktsyntax.</summary>
    private static bool CommChannelOk(DeviceConfig device, string channel)
    {
        if (string.IsNullOrWhiteSpace(device.Resource)) return true;
        try
        {
            using var driver = InstrumentCatalog.Create(device.Adapter, device.Model ?? "", device.Resource, device.Options, device.TimeoutMs);
            return driver is not Testumgebung.Instruments.Communication.CommDevice comm || comm.AcceptsChannel(channel);
        }
        catch (InstrumentException)
        {
            return true; // Einstellungsfehler meldet „Gerät testen“
        }
    }

    /// <summary>Kanäle eines realen Geräts laut Treiber (ohne Verbindung; DAQmx: aus dem Treiber) – null, wenn nicht ermittelbar.</summary>
    public static IReadOnlyList<InstrumentChannel>? ChannelsOf(DeviceConfig device, TestProject? project = null)
    {
        if (device.IsSimulation || string.IsNullOrWhiteSpace(device.Resource) || ProjectInstruments.Family(project, device.Adapter) is null) return null;
        if (device.Adapter == DaqmxDevice.AdapterName && !NativeDaqmxApi.Instance.IsAvailable) return null;
        try
        {
            using var driver = ProjectInstruments.Create(project, device);
            return driver.Channels;
        }
        catch (InstrumentException)
        {
            return null;
        }
    }

    /// <summary>Zyklusrisiko der Sperrreihenfolge für die Abläufe der genannten Prüfplätze.</summary>
    public static LockCycle? CycleRisk(StationSetup setup, IEnumerable<(string Station, TestDefinition Definition)> runs)
        => LockOrderAnalysis.FindCycle(runs.SelectMany(entry => LockOrderAnalysis.Edges(entry.Definition, setup.KeyOf(entry.Station), entry.Station)));
}

/// <summary>
/// Gemeinsamer Zustand aller Prüfplätze eines Rechners: Konfiguration, Gerätepool, Ressourcenvermittlung, Zugriffsprotokoll
/// und die laufenden Abläufe für die Startprüfung (Zyklusrisiko → Start verweigert).
/// </summary>
public sealed class StationCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TestDefinition> _running = new(StringComparer.Ordinal);
    private StationSetup _setup;

    public StationCoordinator(StationSetup setup, IResourceManager resources, IClock? clock = null)
    {
        _setup = setup;
        Resources = resources;
        Pool = new DevicePool { Clock = clock };
        Journal = new HardwareAccessJournal(resources, clock);
    }

    public IResourceManager Resources { get; }
    public DevicePool Pool { get; }

    /// <summary>Konfigurationsdatei war beschädigt: kein Prüfplatz startet, bis sie korrigiert und gespeichert ist.</summary>
    public string? LoadError { get; set; }

    /// <summary>Keine Datei: Vorschlag aus dem Projekt (folgt dem geöffneten Projekt).</summary>
    public bool IsSuggestion { get; set; }
    public HardwareAccessJournal Journal { get; }

    public StationSetup Setup
    {
        get { lock (_gate) return _setup; }
    }

    /// <summary>Konfiguration ersetzt (gespeichert).</summary>
    public event Action? SetupChanged;

    /// <summary>Neue Konfiguration übernehmen – nur ohne laufende Prüfung (Geräteinstanzen werden neu angelegt).</summary>
    /// <exception cref="InvalidOperationException">Ein Prüfplatz prüft gerade.</exception>
    public void Replace(StationSetup setup)
    {
        lock (_gate)
        {
            if (_running.Count > 0)
                throw new InvalidOperationException("Konfiguration kann nicht übernommen werden, solange geprüft wird: " + string.Join(", ", _running.Keys) + ".");
            _setup = setup;
            LoadError = null;
            Pool.Reset();
        }
        SetupChanged?.Invoke();
    }

    public HardwareRegistry BuildRegistry(string station, TestProject project, int seed = 1)
        => StationHardware.Build(Setup, station, project, Pool, seed);

    /// <summary>
    /// Start anmelden: verweigert bei Zyklusrisiko mit den bereits laufenden Abläufen der anderen Prüfplätze
    /// (und im eigenen Ablauf). Rückgabe null = zugelassen, sonst Begründung.
    /// </summary>
    public string? TryBeginRun(string station, TestDefinition definition)
    {
        lock (_gate)
        {
            if (_setup.Station(station) is null) return $"{station} ist in der Prüfplatzkonfiguration nicht vorhanden.";
            if (LoadError is { } error) return error;
            if (CycleRiskWithRunning(station, definition) is { } cycle)
                return cycle.Text + ". Start verweigert. " + cycle.Suggestion;
            _running[station] = definition;
            return null;
        }
    }

    /// <summary>Zyklusrisiko eines Ablaufs zusammen mit den laufenden Abläufen der anderen Prüfplätze.</summary>
    public LockCycle? CycleRiskWithRunning(string station, TestDefinition definition)
    {
        lock (_gate)
            return StationHardware.CycleRisk(_setup, _running.Where(entry => entry.Key != station).Select(entry => (entry.Key, entry.Value)).Append((station, definition)));
    }

    /// <summary>Lauf eines Prüfplatzes beendet; Versionswechsel am Prüfling: neue Definition melden.</summary>
    public void EndRun(string station)
    {
        lock (_gate) _running.Remove(station);
    }

    public void UpdateRun(string station, TestDefinition definition)
    {
        lock (_gate)
            if (_running.ContainsKey(station)) _running[station] = definition;
    }

    /// <summary>
    /// Sicherer Zustand nach Prüfungsende/Abbruch eines Prüfplatzes: alle von ihm verwendeten Geräte mit definiertem sicheren
    /// Zustand (Ausgänge aus, AO 0 V). Gemeinsame Geräte nur, wenn kein anderer Prüfplatz gerade prüft. Fehler werden
    /// gesammelt zurückgegeben (ein nicht erreichbares Gerät verhindert die übrigen nicht).
    /// </summary>
    public async Task<IReadOnlyList<string>> SafeStateAsync(string station, CancellationToken cancellationToken = default)
    {
        StationSetup setup;
        List<string> others;
        lock (_gate)
        {
            setup = _setup;
            others = _running.Keys.Where(key => key != station).ToList();
        }
        var devices = (setup.Station(station)?.Resources ?? []).Select(assignment => setup.Device(assignment.Device)).OfType<DeviceConfig>().Distinct()
            .Where(device => !(device.Shared || !device.IsSimulation) || others.All(other => setup.Station(other)?.Resources.All(assignment => assignment.Device != device.Id) ?? true))
            .ToList();
        return await SafeStateDevicesAsync(devices.Select(device => (d: device, DevicePool.InstanceKey(device, station))), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Alle angelegten Geräte in den sicheren Zustand (Beenden der Anwendung).</summary>
    public Task<IReadOnlyList<string>> SafeStateAllAsync(CancellationToken cancellationToken = default)
    {
        var setup = Setup;
        var keys = Pool.Adapters.Keys.ToList();
        return SafeStateDevicesAsync(keys.Select(key => (setup.Device(key.Contains('/') ? key[(key.IndexOf('/') + 1)..] : key), k: key))
            .Where(entry => entry.Item1 is not null).Select(entry => (entry.Item1!, entry.k)), cancellationToken);
    }

    private async Task<IReadOnlyList<string>> SafeStateDevicesAsync(IEnumerable<(DeviceConfig Device, string Key)> devices, CancellationToken ct)
    {
        var problems = new List<string>();
        var adapters = Pool.Adapters;
        foreach (var (device, key) in devices)
        {
            if (!adapters.TryGetValue(key, out var adapter) || adapter is not ISafeStateAdapter safe) continue;
            try
            {
                await safe.SafeStateAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InstrumentException or TimeoutException or OperationCanceledException or IOException or OutputSequenceException)
            {
                problems.Add($"{device.Name}: sicherer Zustand nicht bestätigt – {ex.Message}");
            }
        }
        if (problems.Count > 0) SafeStateFailed?.Invoke(problems);
        return problems;
    }

    /// <summary>Sicherer Zustand konnte nicht für alle Geräte hergestellt werden (Anzeige, Meldung).</summary>
    public event Action<IReadOnlyList<string>>? SafeStateFailed;

    /// <summary>„Gerät testen“: verbinden, identifizieren, Fehlerwarteschlange und Fähigkeiten – Ausgänge bleiben unverändert.</summary>
    public static async Task<InstrumentProbe> ProbeAsync(DeviceConfig device, CancellationToken cancellationToken = default, TestProject? project = null)
    {
        if (device.IsSimulation)
            return new InstrumentProbe(true, "Simulation", [device.Shared ? "Gemeinsames simuliertes Gerät" : "Simuliertes Gerät je Prüfplatz"], []);
        if (string.IsNullOrWhiteSpace(device.Resource))
            return new InstrumentProbe(false, "keine Ressource", ["Ressource (Adresse) angeben, z. B. " + (InstrumentCatalog.Family(device.Adapter)?.ResourceExample ?? "")], []);
        try
        {
            using var driver = ProjectInstruments.Create(project, device);
            return await driver.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InstrumentException ex)
        {
            return new InstrumentProbe(false, "Fehler", [ex.Message], []);
        }
    }

    public IReadOnlyCollection<string> RunningStations
    {
        get { lock (_gate) return _running.Keys.ToList(); }
    }
}
