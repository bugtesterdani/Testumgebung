/**
 * @file
 * @brief Speichert rechnerbezogene Geräte und Prüfplatzzuordnungen; gemeinsame Geräte werden nur einmal definiert.
 */

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Persistence.Stations;

/// <summary>Gerät des Rechners. Gemeinsame Geräte existieren einmal und werden von allen Prüfplätzen über Sperren geteilt.</summary>
public sealed class DeviceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Adapterfamilie: „Simulation“ oder ein Treiber aus <see cref="Testumgebung.Instruments.InstrumentCatalog"/>.</summary>
    public string Adapter { get; set; } = StationSetup.SimulationAdapter;

    /// <summary>Ein Gerät für alle Prüfplätze (z. B. AC-Leistungsquelle) statt je Prüfplatz eine eigene Instanz.</summary>
    public bool Shared { get; set; }

    public string? Description { get; set; }

    /// <summary>Modell (z. B. 34465A, E36312A, DSOX3034T) – bestimmt Kanäle und Grenzen des Treibers.</summary>
    public string? Model { get; set; }

    /// <summary>VISA-Ressource („USB0::…::INSTR“, „GPIB0::22::INSTR“), LAN-Socket („TCPIP::host::5025::SOCKET“) oder DAQmx-Gerät („Dev1“).</summary>
    public string? Resource { get; set; }

    /// <summary>Zeitlimit je SCPI-Befehl in ms (Standard 5000).</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>Reale Geräte: Sperre je Kanal statt für das ganze Gerät (nur wenn die Kanäle unabhängig sind).</summary>
    public bool LockPerChannel { get; set; }

    /// <summary>Treiberoptionen, z. B. NPLC, Bereich, Kurvenkanäle, Triggerquelle, Sichere Ausgänge.</summary>
    public Dictionary<string, string>? Options { get; set; }

    [JsonIgnore]
    public bool IsSimulation => Adapter == StationSetup.SimulationAdapter;

    /// <summary>Optionen als Text, eine Zeile je Option „Schlüssel=Wert“ (Werte dürfen „=“ und „;“ enthalten, z. B. Kurvenkanäle=L1=1;L2=2).</summary>
    [JsonIgnore]
    public string OptionsText
    {
        get => Options is null ? "" : string.Join(Environment.NewLine, Options.Select(kv => $"{kv.Key}={kv.Value}"));
        set
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var index = line.IndexOf('=');
                if (index > 0) map[line[..index].Trim()] = line[(index + 1)..].Trim();
            }
            Options = map.Count == 0 ? null : map;
        }
    }
}

/// <summary>Abstrakter Name des Ablaufs → Gerät und Kanal an einem Prüfplatz.</summary>
public sealed class ResourceAssignment
{
    public string Name { get; set; } = "";
    public string Device { get; set; } = "";
    public string Channel { get; set; } = "";

    /// <summary>Zugriff nur mit Reservierung an einer Gruppe (bei gemeinsamen Geräten Pflicht).</summary>
    public bool RequiresLock { get; set; }
}

public sealed class StationConfig
{
    public string Name { get; set; } = "";
    public List<ResourceAssignment> Resources { get; set; } = [];

    /// <summary>Auswahl- und Auftragsablauf (Abschnitt 5); null = der Prüfplatz prüft den geöffneten bzw. freigegebenen Ablauf.</summary>
    public SelectionConfig? Selection { get; set; }
}

/// <summary>
/// Prüfplatzkonfiguration des Rechners (<c>%ProgramData%\Testumgebung\pruefplaetze.json</c>): Geräte, Prüfplätze und je
/// Prüfplatz die Zuordnung abstrakter Namen zu Gerät/Kanal. Projekte bleiben geräteunabhängig.
/// Sperrschlüssel = Gerät/Kanal: Zwei Namen für denselben Kanal eines gemeinsamen Geräts teilen eine Sperre.
/// </summary>
public sealed class StationSetup
{
    /// <summary>Nur für den lokalen Simulationsstart; wird nicht als Produktionskonfiguration gespeichert.</summary>
    [JsonIgnore]
    public IReadOnlyList<TestProject> ExampleProjects { get; set; } = [];

    public const string SimulationAdapter = "Simulation";
    public const string LocalSimulation = "sim-lokal";
    public const string SharedSimulation = "sim-gemeinsam";

    public int Format { get; set; } = 1;
    public List<DeviceConfig> Devices { get; set; } = [];
    public List<StationConfig> Stations { get; set; } = [];

    public DeviceConfig? Device(string id) => Devices.FirstOrDefault(device => device.Id == id);

    public StationConfig? Station(string name) => Stations.FirstOrDefault(station => station.Name == name);

    /// <summary>Sperrschlüssel eines zugeordneten Kanals; lokale Geräte sind je Prüfplatz eigene Instanzen.</summary>
    public string LockKey(string station, ResourceAssignment a) => Device(a.Device) switch
    {
        // Reale Geräte gibt es physisch einmal: gesperrt wird das ganze Gerät (Funktionsumschaltung, Ausgänge),
        // auf Wunsch je Kanal (z. B. unabhängige Netzgeräteausgänge).
        { IsSimulation: false, LockPerChannel: false } => a.Device,
        { IsSimulation: false } => $"{a.Device}:{a.Channel}",
        { Shared: true } => $"{a.Device}:{a.Channel}",
        _ => $"{station}/{a.Device}:{a.Channel}",
    };

    /// <summary>Name → Sperrschlüssel für einen Prüfplatz (nicht zugeordnete Namen sperren unter ihrem Namen).</summary>
    public Func<string, string> KeyOf(string station)
    {
        var map = (Station(station)?.Resources ?? []).ToDictionary(assignment => assignment.Name, assignment => LockKey(station, assignment), StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var key) ? key : name;
    }

    /// <summary>Vorschlag aus dem Projekt: gesperrte Ressourcen auf ein gemeinsames Gerät, alles andere je Prüfplatz.</summary>
    public static StationSetup Default(TestProject project, int stations = 2)
        => Default(project, Enumerable.Range(1, stations).Select(StationName).ToList());

    /// <summary>Vorschlag für die genannten Prüfplätze.</summary>
    public static StationSetup Default(TestProject project, IReadOnlyList<string> stationNames)
    {
        var setup = new StationSetup
        {
            Devices =
            [
                new DeviceConfig { Id = LocalSimulation, Name = "Simulation (je Prüfplatz)", Description = "Messkanäle, Digital-E/A und Kurvenquelle jedes Prüfplatzes" },
                new DeviceConfig { Id = SharedSimulation, Name = "AC-Leistungsquelle (Simulation, gemeinsam)", Shared = true,
                    Description = "Ein Gerät für alle Prüfplätze – Zugriff nur mit Reservierung" },
            ],
        };
        foreach (var name in stationNames)
        {
            var station = new StationConfig { Name = name };
            station.Resources.AddRange(Suggest(project, station.Resources));
            setup.Stations.Add(station);
        }
        return setup;
    }

    public static string StationName(int number) => "Prüfplatz " + number.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>Zuordnungsvorschläge für Projektressourcen, die noch fehlen.</summary>
    public static IEnumerable<ResourceAssignment> Suggest(TestProject project, IEnumerable<ResourceAssignment> existing)
    {
        var have = existing.Select(assignment => assignment.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var resource in project.Simulation.Where(resource2 => have.Add(resource2.Name)))
            yield return new ResourceAssignment { Name = resource.Name, Device = resource.RequiresLock ? SharedSimulation : LocalSimulation, Channel = resource.Channel, RequiresLock = resource.RequiresLock };
        foreach (var output in project.SimulationOutputs.Where(output2 => have.Add(output2.Name)))
            yield return new ResourceAssignment { Name = output.Name, Device = output.RequiresLock ? SharedSimulation : LocalSimulation, Channel = output.Channel, RequiresLock = output.RequiresLock };
        foreach (var communication in project.SimulationComm.Where(communication2 => have.Add(communication2.Name)))
            yield return new ResourceAssignment { Name = communication.Name, Device = communication.RequiresLock ? SharedSimulation : LocalSimulation, Channel = communication.Channel, RequiresLock = communication.RequiresLock };
    }

    /// <summary>Alle Namen, die der Ablauf verwendet (Gruppenreservierungen, Schrittparameter, Simulationsprofil).</summary>
    public static IReadOnlyList<string> UsedResources(TestProject project)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in project.Definition.EnabledNodes())
        {
            if (node is GroupDefinition group) foreach (var resource in group.Resources) names.Add(resource);
            if (node is StepDefinition step)
            {
                if (step.Parameters.GetValueOrDefault("Ressource") is { Length: > 0 } one) names.Add(one.Trim());
                if (step.Parameters.GetValueOrDefault("Ressourcen") is { Length: > 0 } many)
                    foreach (var entry in many.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) names.Add(entry);
            }
        }
        foreach (var resource2 in project.Simulation) names.Add(resource2.Name);
        foreach (var output in project.SimulationOutputs) names.Add(output.Name);
        foreach (var communication in project.SimulationComm) names.Add(communication.Name);
        return names.ToList();
    }

    public StationSetup Clone() => JsonSerializer.Deserialize<StationSetup>(JsonSerializer.Serialize(this, StationSetupStore.Json), StationSetupStore.Json)!;
}

/// <summary>Laden/Speichern der Prüfplatzkonfiguration (für alle Windows-Benutzer des Rechners gleich).</summary>
public sealed class StationSetupStore(string path)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Path { get; } = path;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Testumgebung", "pruefplaetze.json");

    public bool Exists => File.Exists(Path);

    /// <summary>null = keine Datei (Vorschlag aus dem Projekt verwenden).</summary>
    /// <exception cref="StationSetupException">Datei beschädigt oder unbekanntes Format – kein stiller Rückfall auf Standardwerte.</exception>
    public StationSetup? Load()
    {
        if (!File.Exists(Path)) return null;
        try
        {
            var setup = JsonSerializer.Deserialize<StationSetup>(File.ReadAllText(Path), Json)
                        ?? throw new StationSetupException("Prüfplatzkonfiguration ist leer.");
            if (setup.Format != 1) throw new StationSetupException($"Unbekanntes Format {setup.Format} der Prüfplatzkonfiguration.");
            return setup;
        }
        catch (JsonException ex)
        {
            throw new StationSetupException($"Prüfplatzkonfiguration '{Path}' ist beschädigt: {ex.Message}", ex);
        }
    }

    /// <summary>Atomar speichern – scheitert ohne Schreibrecht (typisch: nur Administratoren).</summary>
    public void Save(StationSetup setup)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(setup, Json));
        File.Move(temp, Path, overwrite: true);
    }
}

public sealed class StationSetupException(string message, Exception? inner = null) : Exception(message, inner);
