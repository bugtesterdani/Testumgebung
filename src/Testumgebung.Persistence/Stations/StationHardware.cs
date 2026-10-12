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
        foreach (var d in old.OfType<IDisposable>()) d.Dispose();
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
        foreach (var a in config.Resources)
        {
            var adapter = pool.Get(setup, station, a.Device, project);
            var key = setup.LockKey(station, a);
            if (adapter is SimulationAdapter sim && pool.FirstConfiguration(sim, a.Channel))
                Configure(sim, a, project, random, seed);
            registry.Bind(a.Name, adapter, a.Channel, a.RequiresLock, key);
        }
        return registry;
    }

    /// <summary>Simulationsverhalten aus dem Projekt (Wert, Rauschen, Sinus, Kurvenausgang) für den zugeordneten Kanal.</summary>
    private static void Configure(SimulationAdapter adapter, ResourceAssignment a, TestProject project, Random random, int seed)
    {
        if (project.SimulationComm.FirstOrDefault(c => c.Name == a.Name) is { } comm)
            adapter.ConfigureCommunication(a.Channel, CommSimulation.Create(comm.Kind, comm.Rules, seed));
        if (project.Simulation.FirstOrDefault(r => r.Name == a.Name) is { } r)
        {
            adapter.Channel(a.Channel, new ChannelCapabilities(r.Unit, r.Min, r.Max));
            if (r.Noise > 0)
            {
                var (value, noise) = (r.Value, r.Noise);
                adapter.SetInput(a.Channel, () =>
                {
                    lock (random) return Math.Round(value + (random.NextDouble() * 2 - 1) * noise, 3);
                });
            }
            else if (r.Value != 0)
            {
                adapter.SetInput(a.Channel, r.Value);
            }
            if (r.Frequency > 0) adapter.SetSine(a.Channel, r.Value, r.Frequency, r.Phase);
        }
        if (project.SimulationOutputs.FirstOrDefault(o => o.Name == a.Name) is { } o)
        {
            var rates = o.SampleRates.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();
            // Rückwirkung über Namen: Kurvenkanal → Messkanal desselben Geräts (laut Zuordnung dieses Prüfplatzes)
            var feedback = o.Feedback.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=', 2, StringSplitOptions.TrimEntries)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
            adapter.ConfigureOutput(a.Channel, new OutputCapabilities($"{o.Name} ({adapter.Name})", o.Unit, o.Min, o.Max,
                o.Channels.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), rates, o.ExplicitTimes, o.MaxSamples), feedback);
        }
    }

    /// <summary>
    /// Konfigurationsprüfung: Zuordnung vollständig, Geräte bekannt, gemeinsame Geräte nur mit Sperre, keine
    /// Zyklusrisiken der Sperrreihenfolge über alle Prüfplätze (mit deren Sperrschlüsseln).
    /// </summary>
    public static IReadOnlyList<ConfigIssue> Check(StationSetup setup, TestProject project)
    {
        var issues = new List<ConfigIssue>();
        foreach (var duplicate in project.DeviceProfiles.GroupBy(p => p.Id).Where(g => g.Count() > 1))
            issues.Add(new(ConfigSeverity.Error, null, null, $"Geräteprofil-ID '{duplicate.Key}' ist mehrfach vergeben."));
        foreach (var profile in project.DeviceProfiles)
            foreach (var error in profile.Validate()) issues.Add(new(ConfigSeverity.Error, null, null, $"{profile.Name}: {error}"));
        foreach (var dup in setup.Devices.GroupBy(d => d.Id).Where(g => g.Count() > 1))
            issues.Add(new(ConfigSeverity.Error, null, null, $"Geräte-ID '{dup.Key}' ist mehrfach vergeben."));
        var channelsOf = new Dictionary<string, IReadOnlyList<InstrumentChannel>?>(StringComparer.Ordinal);
        CheckDevices();
        if (setup.Stations.Count == 0)
            issues.Add(new(ConfigSeverity.Error, null, null, "Kein Prüfplatz konfiguriert."));
        foreach (var dup in setup.Stations.GroupBy(s => s.Name).Where(g => g.Count() > 1 || string.IsNullOrWhiteSpace(g.Key)))
            issues.Add(new(ConfigSeverity.Error, dup.Key, null, string.IsNullOrWhiteSpace(dup.Key) ? "Prüfplatz ohne Namen." : "Prüfplatzname ist mehrfach vergeben."));

        void CheckDevices()
        {
            foreach (var d in setup.Devices.Where(d => !d.IsSimulation))
            {
                if (ProjectInstruments.Family(project, d.Adapter) is not { } family)
                {
                    issues.Add(new(ConfigSeverity.Error, null, null, $"Gerät „{d.Name}“: unbekannter Adapter „{d.Adapter}“."));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(d.Resource))
                    issues.Add(new(ConfigSeverity.Error, null, null, $"Gerät „{d.Name}“: keine Ressource angegeben.", "z. B. " + family.ResourceExample));
                if (!string.IsNullOrWhiteSpace(d.Model) && !family.Models.Contains(d.Model) && family.Adapter != InstrumentCatalog.DaqmxReplica && family.Adapter != DaqmxDevice.AdapterName)
                    issues.Add(new(ConfigSeverity.Warning, null, null, $"Gerät „{d.Name}“: Modell {d.Model} ist nicht erprobt – Kanalsatz und Grenzen der Familie werden verwendet."));
                channelsOf[d.Id] = ChannelsOf(d, project);
                if (channelsOf[d.Id] is null && d.Adapter == DaqmxDevice.AdapterName)
                    issues.Add(new(ConfigSeverity.Warning, null, null, $"Gerät „{d.Name}“: Kanäle nicht prüfbar – NI-DAQmx nicht installiert oder Gerät {d.Resource} nicht vorhanden."));
                // Ein reales Gerät gibt es nur einmal: ohne „gemeinsam“ darf es nur ein Prüfplatz verwenden
                var users = setup.Stations.Where(st => st.Resources.Any(a => a.Device == d.Id)).Select(st => st.Name).ToList();
                if (!d.Shared && users.Count > 1)
                    issues.Add(new(ConfigSeverity.Error, null, null,
                        $"Gerät „{d.Name}“ ist nicht gemeinsam, wird aber von {string.Join(", ", users)} verwendet – widersprüchlicher Zugriff möglich.",
                        "Als „gemeinsam“ markieren (Zugriff dann nur mit Sperre) oder je Prüfplatz ein eigenes Gerät."));
            }
        }

        var used = StationSetup.UsedResources(project);
        CheckStations();

        void CheckStations()
        {
            foreach (var station in setup.Stations)
            {
                foreach (var dup in station.Resources.GroupBy(a => a.Name).Where(g => g.Count() > 1))
                    issues.Add(new(ConfigSeverity.Error, station.Name, dup.Key, $"'{dup.Key}' ist mehrfach zugeordnet."));
                void CheckAssignment(ResourceAssignment a)
                {
                    var device = setup.Device(a.Device);
                    if (device is null)
                    {
                        issues.Add(new(ConfigSeverity.Error, station.Name, a.Name, $"{a.Name}: Gerät '{a.Device}' ist nicht konfiguriert."));
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(a.Channel))
                        issues.Add(new(ConfigSeverity.Error, station.Name, a.Name, $"{a.Name}: kein Kanal angegeben."));
                    else if (InstrumentCatalog.IsCommunication(device.Adapter))
                    {
                        if (!CommChannelOk(device, a.Channel))
                            issues.Add(new(ConfigSeverity.Error, station.Name, a.Name, $"{a.Name}: Kanal „{a.Channel}“ ist für {device.Adapter} ungültig.",
                                "Modbus: Unit-ID (z. B. 1) oder Punkt 1/HR100:float32."));
                    }
                    else if (channelsOf.TryGetValue(device.Id, out var channels) && channels is not null)
                    {
                        if (channels.FirstOrDefault(c => string.Equals(c.Name, a.Channel, StringComparison.OrdinalIgnoreCase)) is not { } channel)
                            issues.Add(new(ConfigSeverity.Error, station.Name, a.Name, $"{a.Name}: Kanal „{a.Channel}“ gibt es bei {device.Model ?? device.Adapter} nicht.",
                                "Vorhanden: " + string.Join(", ", channels.Take(24).Select(c => c.Name)) + (channels.Count > 24 ? " …" : "")));
                        else if (project.Simulation.FirstOrDefault(r => r.Name == a.Name) is { } sim && sim.Unit != channel.Unit
                                 && !(sim.Unit == "bool" && channel.Unit == "bool"))
                            issues.Add(new(ConfigSeverity.Warning, station.Name, a.Name, $"{a.Name}: Ablauf erwartet {sim.Unit}, Gerätekanal liefert {channel.Unit} – keine Umrechnung."));
                    }
                    if (device.Shared && !a.RequiresLock)
                        issues.Add(new(ConfigSeverity.Error, station.Name, a.Name,
                            $"{a.Name} liegt auf dem gemeinsamen Gerät „{device.Name}“ ohne Sperre – widersprüchlicher Zugriff mehrerer Prüfplätze möglich.",
                            "„Sperre erforderlich“ setzen und die Ressource an der Gruppe reservieren."));
                    if (device.IsSimulation && !project.Simulation.Any(r => r.Name == a.Name) && !project.SimulationOutputs.Any(o => o.Name == a.Name) && !project.SimulationComm.Any(c => c.Name == a.Name) && used.Contains(a.Name))
                        issues.Add(new(ConfigSeverity.Warning, station.Name, a.Name, $"{a.Name}: kein Simulationsverhalten im Projekt – liefert 0."));
                }

                foreach (var a in station.Resources)
                {
                    CheckAssignment(a);
                }
                foreach (var missing in used.Where(n => station.Resources.All(a => a.Name != n)))
                    issues.Add(new(ConfigSeverity.Error, station.Name, missing, $"{missing} wird im Ablauf verwendet, ist aber nicht zugeordnet.",
                        "„Fehlende aus Projekt übernehmen“."));
                // Gesperrte Hardware muss im Ablauf an einer Gruppe reserviert werden
                var reserved = project.Definition.AllNodes().OfType<GroupDefinition>().SelectMany(g => g.Resources).ToHashSet(StringComparer.Ordinal);
                foreach (var a in station.Resources.Where(a => a.RequiresLock && used.Contains(a.Name)))
                {
                    var key = setup.LockKey(station.Name, a);
                    var keyOf = setup.KeyOf(station.Name);
                    if (!reserved.Any(r => keyOf(r) == key))
                        issues.Add(new(ConfigSeverity.Warning, station.Name, a.Name, $"{a.Name} erfordert eine Sperre, wird im Ablauf aber an keiner Gruppe reserviert – Zugriff ergibt ERROR."));
                }
            }

        }

        // Gemeinsame Kanäle: Übersicht
        foreach (var shared in setup.Stations
                     .SelectMany(s => s.Resources.Select(a => (Station: s.Name, A: a)))
                     .Where(x => setup.Device(x.A.Device) is { Shared: true })
                     .GroupBy(x => setup.LockKey(x.Station, x.A)))
        {
            var users = shared.Select(x => x.A.Name == shared.First().A.Name ? x.Station : $"{x.Station} als {x.A.Name}").Distinct();
            issues.Add(new(ConfigSeverity.Info, null, shared.First().A.Name,
                $"Gemeinsam: {shared.First().A.Name} ({string.Join(", ", users)}) – eine Sperre für Gerät/Kanal {shared.Key}."));
        }

        if (CycleRisk(setup, setup.Stations.Select(s => (s.Name, project.Definition))) is { } cycle)
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
        => LockOrderAnalysis.FindCycle(runs.SelectMany(r => LockOrderAnalysis.Edges(r.Definition, setup.KeyOf(r.Station), r.Station)));
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
            return StationHardware.CycleRisk(_setup, _running.Where(r => r.Key != station).Select(r => (r.Key, r.Value)).Append((station, definition)));
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
            others = _running.Keys.Where(k => k != station).ToList();
        }
        var devices = (setup.Station(station)?.Resources ?? []).Select(a => setup.Device(a.Device)).OfType<DeviceConfig>().Distinct()
            .Where(d => !(d.Shared || !d.IsSimulation) || others.All(o => setup.Station(o)?.Resources.All(a => a.Device != d.Id) ?? true))
            .ToList();
        return await SafeStateDevicesAsync(devices.Select(d => (d, DevicePool.InstanceKey(d, station))), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Alle angelegten Geräte in den sicheren Zustand (Beenden der Anwendung).</summary>
    public Task<IReadOnlyList<string>> SafeStateAllAsync(CancellationToken cancellationToken = default)
    {
        var setup = Setup;
        var keys = Pool.Adapters.Keys.ToList();
        return SafeStateDevicesAsync(keys.Select(k => (setup.Device(k.Contains('/') ? k[(k.IndexOf('/') + 1)..] : k), k))
            .Where(x => x.Item1 is not null).Select(x => (x.Item1!, x.k)), cancellationToken);
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
