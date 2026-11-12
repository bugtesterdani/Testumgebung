/**
 * @file
 * @brief Verpackt Projekt und Abhängigkeiten mit Manifest und Prüfsummen zur späteren Integritätsprüfung.
 */

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Persistence.Releases;

/// <summary>Zeile des Paketinhalts (Bereich, Name/Version, Hash).</summary>
public sealed record ManifestEntry(string Area, string Name, string Version, string Sha256);

public sealed record ReleaseManifest(
    string Format,
    string ProjectName,
    string Version,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string Application,
    IReadOnlyList<ManifestEntry> Entries,
    Programs.SourceVersion? Source = null);

/// <summary>Geöffnetes, geprüftes Paket.</summary>
public sealed record OpenedPackage(ReleaseManifest Manifest, TestProject Project, string ProjectJson, IReadOnlyList<string> Problems)
{
    public bool IsIntact => Problems.Count == 0;
    public RuntimeSnapshot? Environment { get; init; }
}

public sealed class ReleasePackageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Unveränderliches Release-Paket (ZIP): <c>manifest.json</c> mit Hashes je Bereich (Ablauf, Bausteine, Codeartefakte,
/// Kurven, Definitionen), <c>projekt.tproj.json</c> und die Quelltexte unter <c>code/</c> zur Einsicht.
/// Der SHA-256 der Paketbytes identifiziert das Paket; die Bereichshashes werden beim Öffnen neu berechnet.
/// </summary>
public static class ReleasePackage
{
    public const string Format = "testumgebung.release/1";
    public const string Areas = "Ablauf, Bausteine, Codeartefakte, Kurven, Definitionen";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly DateTimeOffset ZipTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

    /// <summary>Paket bauen. Die Version des Projekts muss bereits gesetzt sein.</summary>
    public static (byte[] Bytes, ReleaseManifest Manifest) Build(TestProject project, string createdBy, DateTimeOffset createdAt, ProjectSerializer? serializer = null,
        RuntimeSnapshot? environment = null, Programs.SourceVersion? source = null, byte[]? sourceArchive = null)
    {
        serializer ??= new ProjectSerializer();
        var json = serializer.Serialize(project);
        var environmentJson = environment is null ? null : JsonSerializer.Serialize(environment, Json);
        var entries = ComputeEntries(json, project).ToList();
        if (environmentJson is not null) entries.Add(new("Laufzeitumgebung", "runtime.json", "1", Sha256(environmentJson)));
        if (source is not null && sourceArchive is null) throw new ArgumentException("Remote-Quellarchiv fehlt.");
        if (sourceArchive is not null) entries.Add(new("Programmquelle", "source.zip", "1", Sha256(sourceArchive)));
        var manifest = new ReleaseManifest(Format, project.Definition.Name, project.Definition.Version, createdBy, createdAt,
            "Testumgebung " + (typeof(ReleasePackage).Assembly.GetName().Version?.ToString(3) ?? "?"), entries, source);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var zipArchiveEntry = zip.CreateEntry(name, CompressionLevel.Optimal);
                zipArchiveEntry.LastWriteTime = ZipTime;
                using var streamWriter = new StreamWriter(zipArchiveEntry.Open(), new UTF8Encoding(false));
                streamWriter.Write(content);
            }
            Add("manifest.json", JsonSerializer.Serialize(manifest, Json));
            Add("projekt.tproj.json", json);
            if (sourceArchive is not null)
            {
                var entry = zip.CreateEntry("source.zip", CompressionLevel.NoCompression); entry.LastWriteTime = ZipTime;
                using var output = entry.Open(); output.Write(sourceArchive);
            }
            if (environmentJson is not null) Add("runtime.json", environmentJson);
            foreach (var (path, code) in CodeFiles(project.Definition)) Add(path, code);
        }
        return (ms.ToArray(), manifest);
    }

    /// <summary>Paket öffnen und jeden Bereichshash nachrechnen (Probleme statt Ausnahme, außer bei unlesbaren Paketen).</summary>
    public static OpenedPackage Open(byte[] bytes, ProjectSerializer? serializer = null)
    {
        serializer ??= new ProjectSerializer();
        string manifestJson, projectJson;
        string? environmentJson;
        byte[]? sourceArchive;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            manifestJson = Read(zip, "manifest.json");
            projectJson = Read(zip, "projekt.tproj.json");
            sourceArchive = ReadSourceArchive(zip);
            environmentJson = zip.GetEntry("runtime.json") is null ? null : Read(zip, "runtime.json");
        }
        catch (InvalidDataException ex)
        {
            throw new ReleasePackageException("Paket ist kein gültiges Archiv: " + ex.Message, ex);
        }
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestJson, Json) ?? throw new ReleasePackageException("Manifest leer.");
        if (manifest.Format != Format) throw new ReleasePackageException($"Unbekanntes Paketformat '{manifest.Format}'.");
        TestProject project;
        try
        {
            project = serializer.Deserialize(projectJson);
        }
        catch (ProjectFormatException ex)
        {
            throw new ReleasePackageException("Ablauf im Paket nicht lesbar: " + ex.Message, ex);
        }

        var problems = new List<string>();
        var actual = ComputeEntries(projectJson, project).ToList();
        if (environmentJson is not null) actual.Add(new("Laufzeitumgebung", "runtime.json", "1", Sha256(environmentJson)));
        if (sourceArchive is not null) actual.Add(new("Programmquelle", "source.zip", "1", Sha256(sourceArchive)));
        if (manifest.Source is not null && sourceArchive is null) problems.Add("Remote-Quellarchiv fehlt.");
        ValidateEntries(manifest, actual, problems);
        if (project.Definition.Version != manifest.Version) problems.Add("Version im Ablauf weicht vom Manifest ab.");
        RuntimeSnapshot? environment = null;
        if (environmentJson is not null)
        {
            try { environment = JsonSerializer.Deserialize<RuntimeSnapshot>(environmentJson, Json); }
            catch (JsonException ex) { problems.Add("Laufzeitumgebung nicht lesbar: " + ex.Message); }
            if (environment is null || environment.Components is null) problems.Add("Laufzeitumgebung fehlt oder ist ungültig.");
        }
        return new OpenedPackage(manifest, project, projectJson, problems) { Environment = environment };
    }

    /// <summary>Read-only provenance for database list views; integrity is checked before use.</summary>
    public static Programs.SourceVersion? ReadSource(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            return JsonSerializer.Deserialize<ReleaseManifest>(Read(zip, "manifest.json"), Json)?.Source;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ReleasePackageException) { return null; }
    }
    private static byte[]? ReadSourceArchive(ZipArchive zip)
    {
        if (zip.GetEntry("source.zip") is not { } entry) return null;
        using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }

    private static void ValidateEntries(ReleaseManifest manifest, List<ManifestEntry> actual, List<string> problems)
    {
        foreach (var expected in manifest.Entries)
        {
            var found = actual.FirstOrDefault(manifestEntry => manifestEntry.Area == expected.Area && manifestEntry.Name == expected.Name && manifestEntry.Version == expected.Version);
            if (found is null) problems.Add($"{expected.Area} '{expected.Name}' fehlt im Paket.");
            else if (found.Sha256 != expected.Sha256) problems.Add($"{expected.Area} '{expected.Name}': Hash stimmt nicht.");
        }
        foreach (var extra in actual.Where(manifestEntry => manifest.Entries.All(manifestEntry2 => manifestEntry2.Area != manifestEntry.Area || manifestEntry2.Name != manifestEntry.Name || manifestEntry2.Version != manifestEntry.Version)))
            problems.Add($"{extra.Area} '{extra.Name}' ist nicht im Manifest.");
    }

    private static string Read(ZipArchive zip, string name)
    {
        var zipArchiveEntry = zip.GetEntry(name) ?? throw new ReleasePackageException($"'{name}' fehlt im Paket.");
        using var reader = new StreamReader(zipArchiveEntry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Bereichshashes aus dem kanonischen Projekt-JSON.</summary>
    public static IReadOnlyList<ManifestEntry> ComputeEntries(string projectJson, TestProject project)
    {
        var root = JsonNode.Parse(projectJson)!.AsObject();
        var definition = project.Definition;
        var list = new List<ManifestEntry>();

        var flow = root.DeepClone().AsObject();
        flow.Remove("library");
        flow.Remove("assemblies");
        flow.Remove("groupLibrary");
        flow.Remove("simulation");
        flow.Remove("simulationOutputs");
        flow.Remove("curves");
        list.Add(new ManifestEntry("Ablauf", definition.Name, definition.Version, Sha256(flow.ToJsonString())));

        AddBlockEntries(root["library"] as JsonArray, list);
        AddBlockEntries(root["groupLibrary"] as JsonArray, list);

        foreach (var managedAssembly in definition.Assemblies.OrderBy(managedAssembly2 => managedAssembly2.Name, StringComparer.Ordinal))
            list.Add(new ManifestEntry("Codeartefakte", "DLL/" + managedAssembly.Name, "", Sha256(managedAssembly.Data)));

        foreach (var lang in CodeFiles(definition).GroupBy(entry => LanguageOf(entry.Path)).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var combined = string.Join("\n\u0000\n", lang.OrderBy(entry => entry.Path, StringComparer.Ordinal).Select(entry => entry.Path + "\n" + entry.Source));
            list.Add(new ManifestEntry("Codeartefakte", $"{lang.Key} ({lang.Count()} Quelle(n))", definition.Version, Sha256(combined)));
        }

        if (root["curves"] is JsonArray curves && curves.Count > 0)
            foreach (var node in curves)
                list.Add(new ManifestEntry("Kurven", (string?)node!["name"] ?? (string)node["id"]!, (string?)node["version"] ?? "", Sha256(node.ToJsonString())));
        else
            list.Add(new ManifestEntry("Kurven", "keine", "", Sha256("")));
        list.Add(new ManifestEntry("Definitionen", "Hardwareprofil (Simulation)", definition.Version,
            Sha256((root["simulation"]?.ToJsonString() ?? "[]") + (root["simulationOutputs"]?.ToJsonString() ?? ""))));
        return list;
    }

    private static void AddBlockEntries(JsonArray? blocks, List<ManifestEntry> list)
    {
        if (blocks is null) return;
        foreach (var node in blocks)
            list.Add(new ManifestEntry("Bausteine", (string?)node!["name"] ?? (string)node["id"]!, (string?)node["version"] ?? "", Sha256(node.ToJsonString())));
    }

    /// <summary>Quelltexte aller Bibliotheksbausteine und CodeBlocks im Ablauf.</summary>
    public static IEnumerable<(string Path, string Source)> CodeFiles(TestDefinition d)
    {
        foreach (var codeBlock in d.Library)
            yield return ($"code/bausteine/{Safe(codeBlock.Id)}{Extension(codeBlock.Language)}", codeBlock.Source);
        foreach (var step in d.AllNodes().OfType<StepDefinition>().Where(step2 => step2.BlockType == "code"))
        {
            var lang = step.Parameters.GetValueOrDefault("Sprache") switch
            {
                "Python" => CodeLanguage.Python,
                "C++" => CodeLanguage.Cpp,
                _ => CodeLanguage.CSharp,
            };
            yield return ($"code/ablauf/{Safe(step.Id)}{Extension(lang)}", step.Parameters.GetValueOrDefault("Quelltext") ?? "");
        }
    }

    private static string Extension(CodeLanguage codeLanguage) => codeLanguage switch
    {
        CodeLanguage.Python => ".py",
        CodeLanguage.Cpp => ".cpp",
        CodeLanguage.LabView => ".labview.txt",
        _ => ".cs",
    };

    private static string LanguageOf(string path) => Path.GetExtension(path) switch
    {
        ".py" => "Python",
        ".cpp" => "C++",
        ".txt" => "LabVIEW (VI)",
        _ => "C#",
    };

    private static string Safe(string id) => new(id.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_').ToArray());
}

/// <summary>
/// Darf eine neue Version am nächsten Prüfling übernommen werden? Nur wenn alles außerhalb des Prüflingstests gleich ist:
/// Hauptablauf-Variablen, Init, Finalization, Loop-Einstellungen sowie Bausteine, die außerhalb des Prüflings verwendet werden.
/// Sonst gilt die neue Version erst ab dem nächsten Hauptablaufstart (Vorschlag Abschnitt 10).
/// </summary>
public static class DefinitionCompatibility
{
    public static bool CanSwapAtDut(TestProject current, TestProject next, out string reason, ProjectSerializer? serializer = null)
    {
        serializer ??= new ProjectSerializer();
        if (current.Definition.Name != next.Definition.Name)
        {
            reason = "anderer Prüfablauf";
            return false;
        }
        var jsonObject = Outer(serializer.Serialize(current), out var dutA, out var usedA, out var curvesA);
        var jsonObject2 = Outer(serializer.Serialize(next), out var dutB, out var usedB, out var curvesB);
        if (dutA is null || dutA != dutB)
        {
            reason = "Prüflingsgruppe geändert";
            return false;
        }
        if (!SameOuterFlow(jsonObject, jsonObject2, out reason)) return false;
        foreach (var id in curvesA.Union(curvesB))
        {
            if (Curve(jsonObject, id) != Curve(jsonObject2, id))
            {
                reason = $"Kurve '{id}' wird außerhalb des Prüflings verwendet und wurde geändert";
                return false;
            }
        }
        foreach (var id in usedA.Union(usedB))
        {
            if (Block(jsonObject, id) != Block(jsonObject2, id))
            {
                reason = $"Baustein '{id}' wird außerhalb des Prüflings verwendet und wurde geändert";
                return false;
            }
        }
        reason = "";
        return true;
    }

    private static bool SameOuterFlow(JsonObject jsonObject, JsonObject jsonObject2, out string reason)
    {
        (string Key, string Reason)[] properties =
        [
            ("variables", "Hauptablauf-Variablen geändert"),
            ("assemblies", ".NET-DLLs geändert; neue Prüfsitzung erforderlich"),
            ("init", "Init geändert"),
            ("finalization", "Finalization geändert"),
            ("loop", "Loop außerhalb des Prüflings geändert"),
        ];
        foreach (var (key, message) in properties)
            if (jsonObject[key]?.ToJsonString() != jsonObject2[key]?.ToJsonString()) { reason = message; return false; }
        reason = "";
        return true;
    }

    private static string? Curve(JsonObject root, string idAtVersion)
    {
        var parts = idAtVersion.Split('@');
        var versions = (root["curves"] as JsonArray)?.Where(node => (string?)node?["id"] == parts[0]).ToList() ?? [];
        var pick = parts.Length > 1 && parts[1].Length > 0 ? versions.FirstOrDefault(node => (string?)node?["version"] == parts[1]) : versions.LastOrDefault();
        return pick?.ToJsonString();
    }

    private static string? Block(JsonObject root, string id)
        => (root["library"] as JsonArray)?.Concat((root["groupLibrary"] as JsonArray) ?? []).FirstOrDefault(node => (string?)node?["id"] == id)?.ToJsonString();

    /// <summary>JSON ohne Prüflingsinhalt; liefert die Id der Prüflingsgruppe und die außerhalb verwendeten Bausteine.</summary>
    private static JsonObject Outer(string json, out string? dutId, out HashSet<string> usedOutside, out HashSet<string> curvesOutside)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var curves = new HashSet<string>(StringComparer.Ordinal);
        root.Remove("version");
        root.Remove("description");
        root.Remove("simulation");
        root.Remove("simulationOutputs");
        string? found = null;
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? node)
        {
            if (node is not JsonObject jsonObject) return;
            if (jsonObject["dutRun"]?.GetValue<bool>() == true)
            {
                found ??= (string?)jsonObject["id"];
                foreach (var key in jsonObject.Select(kv => kv.Key).Where(key2 => key2 is not ("id" or "art")).ToList()) jsonObject.Remove(key);
                return;
            }
            CollectReferences(jsonObject);
            if (jsonObject["children"] is JsonArray children) foreach (var node2 in children) Walk(node2);
            Walk(jsonObject["exit"]);
        }
        void CollectReferences(JsonObject jsonObject2)
        {
            if (((string?)jsonObject2["block"] is Testumgebung.Engine.Blocks.TraceBlocks.Start or Testumgebung.Engine.Blocks.TraceBlocks.Record) && jsonObject2["parameters"] is JsonObject trace)
            {
                foreach (var parameter in new[] { "Grenzkurven", Testumgebung.Engine.Blocks.TraceBlocks.AbortCurvesParameter })
                    foreach (var key in ((string?)trace[parameter] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        curves.Add(key.Contains('@') ? key : key + "@");
            }
            if (jsonObject2["parameters"] is JsonObject jsonObject3 && (string?)jsonObject3["Baustein"] is { Length: > 0 } bufferText) used.Add(bufferText);
            if (jsonObject2["parameters"] is JsonObject pc && (string?)pc["Kurve"] is { Length: > 0 } curve) curves.Add(curve + "@" + ((string?)pc["Version"] ?? ""));
        }

        Walk(root["init"]);
        Walk(root["loop"]);
        Walk(root["finalization"]);
        dutId = found;
        usedOutside = used;
        curvesOutside = curves;
        return root;
    }
}
