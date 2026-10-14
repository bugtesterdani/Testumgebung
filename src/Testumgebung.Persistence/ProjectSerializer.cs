/**
 * @file
 * @brief Überführt das Prüfmodell in das versionierte Projektformat und validiert eingelesene Definitionen.
 */

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Storage;
using Testumgebung.Domain.Curves;
using Testumgebung.Engine.Blocks;

namespace Testumgebung.Persistence;

/// <summary>Projektdatei konnte nicht gelesen oder geschrieben werden.</summary>
public sealed class ProjectFormatException(string message) : Exception(message);

/// <summary>Simulierte Ressource eines Projekts (bis zur eigentlichen Prüfplatzkonfiguration in späteren Etappen).</summary>
/// <param name="Frequency">&gt; 0: Kanal liefert zusätzlich eine Sinuskurve mit Effektivwert <paramref name="Value"/>.</param>
/// <param name="Phase">Phasenlage der Sinuskurve in Grad.</param>
public sealed record SimulatedResource(string Name, string Channel, string Unit, double Value, bool RequiresLock = false, double Min = -1e9, double Max = 1e9, double Noise = 0,
    double Frequency = 0, double Phase = 0);

/// <summary>Projekt = Prüfablauf plus Simulationskonfiguration.</summary>
public sealed class TestProject(TestDefinition definition)
{
    public TestDefinition Definition { get; set; } = definition;
    public string? Description { get; set; }
    public List<SimulatedResource> Simulation { get; } = [];

    /// <summary>Simulierte Kurvenausgänge (z. B. programmierbare AC-Quelle) mit Rückwirkung auf Messkanäle.</summary>
    public List<SimulatedOutput> SimulationOutputs { get; } = [];

    /// <summary>Simulierte Prüflingskommunikation (Byte-Strom, Modbus, CAN) nach Regeln.</summary>
    public List<SimulatedComm> SimulationComm { get; } = [];
    public List<Testumgebung.Instruments.CustomDeviceProfile> DeviceProfiles { get; } = [];
}

/// <summary>
/// Simulierte Kommunikationsschnittstelle des Prüflings: abstrakter Name, Kanal der Simulation, Art und Regeln
/// (siehe <see cref="Testumgebung.Hardware.CommSimulation"/>).
/// </summary>
public sealed record SimulatedComm(string Name, string Channel, Testumgebung.Hardware.CommKind Kind, string Rules, bool RequiresLock = false);

/// <summary>
/// Simulierter Kurvenausgang: Fähigkeiten (Einheit, Bereich, Abtastraten, Kanäle) und Rückwirkung
/// „Kurvenkanal=Messkanal“ (z. B. „L1=u1;L2=u2;L3=u3“), damit die Ausgabe messbar ist.
/// </summary>
public sealed record SimulatedOutput(string Name, string Channel, string Unit, double Min, double Max, string SampleRates,
    string Channels, string Feedback = "", bool ExplicitTimes = false, int MaxSamples = 1_000_000, bool RequiresLock = false);

/// <summary>
/// Lesbares, versioniertes JSON-Format für Projektdefinitionen (Abschnitt 11).
/// Node-IDs bleiben erhalten; nur Schritte aus dem Bausteinkatalog sind speicherbar.
/// </summary>
public sealed class ProjectSerializer(BlockCatalog catalog)
{
    public const string FormatName = "testumgebung.projekt";
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ProjectSerializer() : this(BlockCatalog.Default)
    {
    }

    public string Serialize(TestProject project)
    {
        var def = project.Definition;
        var dto = new ProjectDto
        {
            Format = FormatName,
            FormatVersion = FormatVersion,
            Name = def.Name,
            Version = def.Version,
            Description = project.Description,
            Assemblies = def.Assemblies.Count == 0 ? null : def.Assemblies.OrderBy(a => a.Name, StringComparer.Ordinal).ToList(),
            Variables = def.Root.Variables.Select(ToDto).ToList(),
            Init = ToDto(def.Init),
            Loop = ToDto(def.Loop),
            Finalization = ToDto(def.Finalization),
            Simulation = project.Simulation.ToList(),
            SimulationOutputs = project.SimulationOutputs.Count == 0 ? null : project.SimulationOutputs.ToList(),
            SimulationComm = project.SimulationComm.Count == 0 ? null : project.SimulationComm.ToList(),
            DeviceProfiles = project.DeviceProfiles.Count == 0 ? null : project.DeviceProfiles.ToList(),
            Library = def.Library.Count == 0 ? null : def.Library.Select(ToDto).ToList(),
            GroupLibrary = def.GroupLibrary.Count == 0 ? null : def.GroupLibrary.Select(b => new GroupBlockDto
            {
                Id = b.Id,
                Name = b.Name,
                Version = b.Version,
                Description = b.Description,
                Ports = b.Ports.Select(ToDto).ToList(),
                Body = ToDto(b.Body),
            }).ToList(),
            Curves = def.Curves.Versions.Count == 0 ? null : def.Curves.Versions.Select(ToDto).ToList(),
            OperatorLayout = def.Layout.IsDefault ? null : def.Layout.Clone(),
            BusDatabases = def.BusDatabases.Count == 0 ? null : def.BusDatabases.Select(b => new BusDatabaseDto
            {
                Name = b.Name,
                Format = b.Format == Testumgebung.Contracts.BusDatabaseFormat.Ldf ? "LDF" : "DBC",
                FileName = b.FileName,
                Content = b.Content,
            }).ToList(),
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    public TestProject Deserialize(string json)
    {
        ProjectDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProjectDto>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException($"Projektdatei ist kein gültiges JSON: {ex.Message}");
        }
        if (dto is null || dto.Format != FormatName) throw new ProjectFormatException("Keine Projektdatei der Testumgebung.");
        if (dto.FormatVersion > FormatVersion) throw new ProjectFormatException($"Formatversion {dto.FormatVersion} wird von dieser Anwendung nicht unterstützt.");
        if (dto.Init is null || dto.Loop is null || dto.Finalization is null) throw new ProjectFormatException("Init, Loop oder Finalization fehlt.");

        var def = new TestDefinition(dto.Name ?? "Unbenannt", dto.Version ?? "0.1", FromDto(dto.Init), FromDto(dto.Loop), FromDto(dto.Finalization));
        foreach (var v in dto.Variables ?? []) def.Root.Variables.Add(FromDto(v, "Hauptablauf"));
        ReadLibraries(dto, def);
        var project = new TestProject(def) { Description = dto.Description };
        if (dto.Simulation is not null) project.Simulation.AddRange(dto.Simulation);
        if (dto.SimulationOutputs is not null) project.SimulationOutputs.AddRange(dto.SimulationOutputs);
        if (dto.SimulationComm is not null) project.SimulationComm.AddRange(dto.SimulationComm);
        ReadDeviceProfiles(dto.DeviceProfiles, project);
        return project;
    }

    private static void ReadDeviceProfiles(List<Testumgebung.Instruments.CustomDeviceProfile>? profiles, TestProject project)
    {
        if (profiles is null) return;
        foreach (var profile in profiles)
        {
            if (profile is null) throw new ProjectFormatException("Ungültiges Geräteprofil.");
            var errors = profile.Validate();
            if (errors.Count > 0) throw new ProjectFormatException(string.Join(" ", errors));
            if (project.DeviceProfiles.Any(p => p.Id == profile.Id)) throw new ProjectFormatException("Geräteprofil-ID ist mehrfach vergeben.");
            project.DeviceProfiles.Add(profile);
        }
    }

    private void ReadLibraries(ProjectDto dto, TestDefinition def)
    {
        ReadManagedAssemblies(dto, def);
        foreach (var b in dto.Library ?? []) def.Library.Add(FromDto(b));
        ReadGroupLibrary(dto, def);
        foreach (var c in dto.Curves ?? []) def.Curves.Add(FromDto(c));
        if (dto.OperatorLayout is { } layout) def.Layout = layout.Clone();
        foreach (var b in dto.BusDatabases ?? [])
        {
            var name = Require(b.Name, "Busdatenbank-name");
            if (def.BusDatabases.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ProjectFormatException($"Busdatenbank '{name}' ist doppelt.");
            var format = string.Equals(b.Format, "LDF", StringComparison.OrdinalIgnoreCase) ? Testumgebung.Contracts.BusDatabaseFormat.Ldf : Testumgebung.Contracts.BusDatabaseFormat.Dbc;
            def.BusDatabases.Add(new BusDatabaseDefinition(name, format, b.Content ?? "", b.FileName));
        }
    }

    private void ReadGroupLibrary(ProjectDto dto, TestDefinition def)
    {
        foreach (var g in dto.GroupLibrary ?? [])
        {
            if (g.Body is null) throw new ProjectFormatException($"Grafischer Baustein '{g.Name}': Körper fehlt.");
            var block = new GroupBlockDefinition(Require(g.Id, "Baustein-id"), g.Name ?? g.Id!, FromDto(g.Body))
            {
                Version = g.Version ?? "0.1",
                Description = g.Description,
            };
            foreach (var p in g.Ports ?? []) block.Ports.Add(FromDto(p, block.Name));
            def.GroupLibrary.Add(block);
        }
    }

    private static void ReadManagedAssemblies(ProjectDto dto, TestDefinition def)
    {
        foreach (var assembly in dto.Assemblies ?? [])
        {
            try { ManagedAssemblyImport.Validate(assembly); }
            catch (ArgumentException ex) { throw new ProjectFormatException(ex.Message); }
            if (def.Assemblies.Any(a => a.Name.Equals(assembly.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ProjectFormatException("DLL doppelt: " + assembly.Name);
            def.Assemblies.Add(assembly);
        }
    }

    public void Save(TestProject project, string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(project));
        File.Move(temp, path, overwrite: true);
    }

    public TestProject Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>Unveränderliche Kopie für einen Lauf: laufende Prüflinge behalten ihre gestartete Version.</summary>
    public TestProject Clone(TestProject project) => Deserialize(Serialize(project));

    // ------------------------------------------------------------------ Abbildung

    private static GroupDto ToDto(GroupDefinition g) => new()
    {
        Id = g.Id,
        Name = g.Name,
        Description = g.Description,
        Variables = g.Variables.Select(ToDto).ToList(),
        Condition = g.EntryCondition,
        Repeat = new RepeatDto
        {
            Mode = g.Repetition.Mode,
            Count = g.Repetition.Mode == RepeatMode.Count ? g.Repetition.Count : null,
            DurationMs = g.Repetition.Mode == RepeatMode.Duration ? g.Repetition.Duration.TotalMilliseconds : null,
            Expression = g.Repetition.Expression,
            StopWithinIteration = g.Repetition.StopWithinIteration ? true : null,
        },
        ResultBeforeRepeat = g.ResultBeforeRepeat,
        Exit = g.ExitGroup is null ? null : ToDto(g.ExitGroup),
        Resources = g.Resources.Count == 0 ? null : g.Resources.ToList(),
        ResourceTimeoutMs = g.ResourceTimeout?.TotalMilliseconds,
        InterfaceBoundary = g.IsInterfaceBoundary ? true : null,
        DutRun = g.IsDutRun ? true : null,
        DutIdVariable = g.IsDutRun ? g.DutIdVariable : null,
        Children = g.Children.Select(ToDto).ToList(),
        Storage = ToDto(g.Storage),
    };

    private static StorageDto? ToDto(StorageSettings s) => s.IsInherit ? null : new StorageDto
    {
        Display = s.Display,
        File = s.File,
        FileWaveforms = s.FileWaveforms,
        Database = s.Database,
        DatabaseWaveforms = s.DatabaseWaveforms,
    };

    private static StorageSettings FromDto(StorageDto? d)
        => d is null ? StorageSettings.Inherit : new StorageSettings(d.Display, d.File, d.FileWaveforms, d.Database, d.DatabaseWaveforms);

    private static LibraryBlockDto ToDto(CodeBlockDefinition b) => new()
    {
        Id = b.Id,
        Name = b.Name,
        Version = b.Version,
        Description = b.Description,
        Language = b.Language,
        Source = b.Source,
        Ports = b.Ports.Select(ToDto).ToList(),
    };

    private static PortDto ToDto(CodePort p) => new()
    {
        Name = p.Name,
        Type = p.Type,
        Direction = p.Direction,
        Unit = p.Unit,
        Description = p.Description,
        TestValue = p.TestValue is null ? null : FormatInitial(TypedValue.Coerce(p.TestValue, p.Type)),
    };

    private static CodePort FromDto(PortDto p, string blockName)
    {
        object? test = null;
        if (p.TestValue is not null)
        {
            try { test = TypedValue.Parse(p.TestValue, p.Type); }
            catch (TypeMismatchException ex) { throw new ProjectFormatException($"Baustein '{blockName}', Port '{p.Name}': {ex.Message}"); }
        }
        return new CodePort(Require(p.Name, "Portname"), p.Type, p.Direction, p.Unit, p.Description, test);
    }

    private static CodeBlockDefinition FromDto(LibraryBlockDto dto)
    {
        var block = new CodeBlockDefinition(Require(dto.Id, "Baustein-id"), dto.Name ?? dto.Id!)
        {
            Version = dto.Version ?? "0.1",
            Description = dto.Description,
            Language = dto.Language,
            Source = dto.Source ?? "",
        };
        foreach (var p in dto.Ports ?? []) block.Ports.Add(FromDto(p, block.Name));
        return block;
    }

    private static VariableDto ToDto(VariableDefinition v) => new()
    {
        Name = v.Name,
        Type = v.Type,
        Initial = v.Initial is null ? null : FormatInitial(TypedValue.Coerce(v.Initial, v.Type)),
    };

    private static VariableDefinition FromDto(VariableDto v, string group)
    {
        object? initial = null;
        if (v.Initial is not null)
        {
            try { initial = TypedValue.Parse(v.Initial, v.Type); }
            catch (TypeMismatchException ex) { throw new ProjectFormatException($"Gruppe '{group}', Variable '{v.Name}': {ex.Message}"); }
        }
        return new VariableDefinition(Require(v.Name, "Variablenname"), v.Type, initial);
    }

    private static string FormatInitial(object value) => value is string text ? text : TypedValue.Format(value);

    private static NodeDto ToDto(NodeDefinition node) => node switch
    {
        GroupDefinition g => ToDto(g),
        StepDefinition s when s.BlockType is not null => new StepDto
        {
            Id = s.Id,
            Name = s.Name,
            Description = s.Description,
            Block = s.BlockType,
            Parameters = new Dictionary<string, string>(s.Parameters),
            Storage = ToDto(s.Storage),
        },
        StepDefinition s => throw new ProjectFormatException($"Schritt '{s.Name}' ({s.Id}) stammt nicht aus dem Bausteinkatalog und kann nicht gespeichert werden."),
        _ => throw new ProjectFormatException($"Unbekannter Knotentyp {node.GetType().Name}."),
    };

    private GroupDefinition FromDto(GroupDto dto)
    {
        var g = new GroupDefinition(Require(dto.Id, "id"), dto.Name ?? dto.Id!)
        {
            Description = dto.Description,
            ResultBeforeRepeat = dto.ResultBeforeRepeat,
            ResourceTimeout = dto.ResourceTimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            IsInterfaceBoundary = dto.InterfaceBoundary ?? false,
            IsDutRun = dto.DutRun ?? false,
            DutIdVariable = dto.DutIdVariable ?? "Kennung",
            Storage = FromDto(dto.Storage),
        };
        try
        {
            g.EntryCondition = string.IsNullOrWhiteSpace(dto.Condition) ? null : dto.Condition;
        }
        catch (Exception ex)
        {
            throw new ProjectFormatException($"Gruppe '{g.Name}': Bedingung ungültig – {ex.Message}");
        }

        var r = dto.Repeat ?? new RepeatDto();
        g.Repetition = new Repetition(r.Mode, r.Count ?? 1, TimeSpan.FromMilliseconds(r.DurationMs ?? 0), r.Expression, r.StopWithinIteration == true);

        foreach (var v in dto.Variables ?? []) g.Variables.Add(FromDto(v, g.Name));
        if (dto.Resources is not null) g.Resources.AddRange(dto.Resources);
        if (dto.Exit is not null) g.ExitGroup = FromDto(dto.Exit);
        foreach (var child in dto.Children ?? [])
        {
            g.Children.Add(child switch
            {
                GroupDto cg => FromDto(cg),
                StepDto s => FromStep(s),
                _ => throw new ProjectFormatException("Unbekannter Knotentyp."),
            });
        }
        return g;
    }

    private StepDefinition FromStep(StepDto dto)
    {
        var type = Require(dto.Block, "block");
        if (!catalog.TryGet(type, out _)) throw new ProjectFormatException($"Schritt '{dto.Name}': unbekannter Baustein '{type}'.");
        var step = catalog.Create(type, Require(dto.Id, "id"), dto.Name, dto.Parameters);
        step.Description = dto.Description;
        step.Storage = FromDto(dto.Storage);
        return step;
    }

    private static CurveDto ToDto(CurveDefinition c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Version = c.Version,
        Description = c.Description,
        SampleRate = c.SampleRate,
        Times = c.Times is null ? null : Doubles(c.Times),
        Channels = c.Channels.Select(ch => new CurveChannelDto { Id = ch.Id, Unit = ch.Unit, Samples = Doubles(ch.Samples) }).ToList(),
        Origin = c.Origin,
        Source = c.SourceText,
        History = c.History.Count == 0 ? null : c.History.ToList(),
        CreatedBy = c.CreatedBy,
        CreatedAt = c.CreatedAt,
        Quality = c.Quality,
        Sha256 = c.ContentHash(),
    };

    private static CurveDefinition FromDto(CurveDto d)
    {
        var name = d.Name ?? d.Id ?? "?";
        double[] Read(string? b64, string what)
        {
            try
            {
                var bytes = Convert.FromBase64String(b64 ?? "");
                if (bytes.Length % 8 != 0) throw new FormatException();
                var v = new double[bytes.Length / 8];
                for (var i = 0; i < v.Length; i++) v[i] = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(i * 8));
                return v;
            }
            catch (FormatException)
            {
                throw new ProjectFormatException($"Kurve '{name}': {what} nicht lesbar.");
            }
        }
        var c = new CurveDefinition
        {
            Id = Require(d.Id, "Kurven-id"),
            Name = name,
            Version = Require(d.Version, "Kurvenversion"),
            Description = d.Description,
            SampleRate = d.SampleRate,
            Times = d.Times is null ? null : Read(d.Times, "Zeitpunkte"),
            Channels = (d.Channels ?? []).Select(ch => new CurveChannel(Require(ch.Id, "Kanal"), ch.Unit ?? "", Read(ch.Samples, $"Kanal {ch.Id}"))).ToList(),
            Origin = d.Origin,
            SourceText = d.Source ?? "",
            History = d.History ?? [],
            CreatedBy = d.CreatedBy ?? "",
            CreatedAt = d.CreatedAt,
            Quality = d.Quality,
        };
        if (d.Sha256 is { } hash && hash != c.ContentHash())
            throw new ProjectFormatException($"Kurve '{name}' v{c.Version}: Inhalt passt nicht zum gespeicherten Hash (verändert?).");
        return c;
    }

    private static string Doubles(double[] values)
    {
        var bytes = new byte[values.Length * 8];
        for (var i = 0; i < values.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), values[i]);
        return Convert.ToBase64String(bytes);
    }

    private static string Require(string? value, string what)
        => string.IsNullOrWhiteSpace(value) ? throw new ProjectFormatException($"Pflichtfeld '{what}' fehlt.") : value;

    // ------------------------------------------------------------------ DTOs

    private sealed class ProjectDto
    {
        public string? Format { get; set; }
        public int FormatVersion { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public List<VariableDto>? Variables { get; set; }
        public GroupDto? Init { get; set; }
        public GroupDto? Loop { get; set; }
        public GroupDto? Finalization { get; set; }
        public List<SimulatedResource>? Simulation { get; set; }
        public List<SimulatedOutput>? SimulationOutputs { get; set; }
        public List<SimulatedComm>? SimulationComm { get; set; }
        public List<Testumgebung.Instruments.CustomDeviceProfile>? DeviceProfiles { get; set; }
        public List<LibraryBlockDto>? Library { get; set; }
        public List<GroupBlockDto>? GroupLibrary { get; set; }
        public List<CurveDto>? Curves { get; set; }
        public OperatorLayout? OperatorLayout { get; set; }
        public List<BusDatabaseDto>? BusDatabases { get; set; }
        public List<ManagedAssembly>? Assemblies { get; set; }
    }

    /// <summary>Busbeschreibung im Originaltext (DBC/LDF).</summary>
    private sealed class BusDatabaseDto
    {
        public string? Name { get; set; }
        public string? Format { get; set; }
        public string? FileName { get; set; }
        public string? Content { get; set; }
    }

    /// <summary>Kurvenversion; Samples als float64 little-endian, Base64.</summary>
    private sealed class CurveDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public double SampleRate { get; set; }
        public string? Times { get; set; }
        public List<CurveChannelDto>? Channels { get; set; }
        public CurveOrigin Origin { get; set; }
        public string? Source { get; set; }
        public List<string>? History { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? Quality { get; set; }
        public string? Sha256 { get; set; }
    }

    private sealed class CurveChannelDto
    {
        public string? Id { get; set; }
        public string? Unit { get; set; }
        public string? Samples { get; set; }
    }

    private sealed class GroupBlockDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public List<PortDto>? Ports { get; set; }
        public GroupDto? Body { get; set; }
    }

    private sealed class LibraryBlockDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public CodeLanguage Language { get; set; }
        public string? Source { get; set; }
        public List<PortDto>? Ports { get; set; }
    }

    private sealed class PortDto
    {
        public string? Name { get; set; }
        public VarType Type { get; set; }
        public PortDirection Direction { get; set; }
        public string? Unit { get; set; }
        public string? Description { get; set; }
        public string? TestValue { get; set; }
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "art")]
    [JsonDerivedType(typeof(GroupDto), "gruppe")]
    [JsonDerivedType(typeof(StepDto), "schritt")]
    private abstract class NodeDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public StorageDto? Storage { get; set; }
    }

    /// <summary>Filter (Abschnitt 9.1); fehlende Werte = erben.</summary>
    private sealed class StorageDto
    {
        public bool? Display { get; set; }
        public bool? File { get; set; }
        public bool? FileWaveforms { get; set; }
        public bool? Database { get; set; }
        public bool? DatabaseWaveforms { get; set; }
    }

    private sealed class GroupDto : NodeDto
    {
        public List<VariableDto>? Variables { get; set; }
        public string? Condition { get; set; }
        public RepeatDto? Repeat { get; set; }
        public RepeatResultPolicy ResultBeforeRepeat { get; set; }
        public List<string>? Resources { get; set; }
        public double? ResourceTimeoutMs { get; set; }
        public bool? InterfaceBoundary { get; set; }
        public bool? DutRun { get; set; }
        public string? DutIdVariable { get; set; }
        public List<NodeDto>? Children { get; set; }
        public GroupDto? Exit { get; set; }
    }

    private sealed class StepDto : NodeDto
    {
        public string? Block { get; set; }
        public Dictionary<string, string>? Parameters { get; set; }
    }

    private sealed class VariableDto
    {
        public string? Name { get; set; }
        public VarType Type { get; set; }
        public string? Initial { get; set; }
    }

    private sealed class RepeatDto
    {
        public RepeatMode Mode { get; set; } = RepeatMode.Once;
        public int? Count { get; set; }
        public double? DurationMs { get; set; }
        public string? Expression { get; set; }
        public bool? StopWithinIteration { get; set; }
    }
}
