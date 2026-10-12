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
    IReadOnlyList<ManifestEntry> Entries);

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
        RuntimeSnapshot? environment = null)
    {
        serializer ??= new ProjectSerializer();
        var json = serializer.Serialize(project);
        var environmentJson = environment is null ? null : JsonSerializer.Serialize(environment, Json);
        var entries = ComputeEntries(json, project).ToList();
        if (environmentJson is not null) entries.Add(new("Laufzeitumgebung", "runtime.json", "1", Sha256(environmentJson)));
        var manifest = new ReleaseManifest(Format, project.Definition.Name, project.Definition.Version, createdBy, createdAt,
            "Testumgebung " + (typeof(ReleasePackage).Assembly.GetName().Version?.ToString(3) ?? "?"), entries);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                e.LastWriteTime = ZipTime;
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("manifest.json", JsonSerializer.Serialize(manifest, Json));
            Add("projekt.tproj.json", json);
            if (environmentJson is not null) Add("runtime.json", environmentJson);
            foreach (var (path, source) in CodeFiles(project.Definition)) Add(path, source);
        }
        return (ms.ToArray(), manifest);
    }

    /// <summary>Paket öffnen und jeden Bereichshash nachrechnen (Probleme statt Ausnahme, außer bei unlesbaren Paketen).</summary>
    public static OpenedPackage Open(byte[] bytes, ProjectSerializer? serializer = null)
    {
        serializer ??= new ProjectSerializer();
        string manifestJson, projectJson;
        string? environmentJson;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            manifestJson = Read(zip, "manifest.json");
            projectJson = Read(zip, "projekt.tproj.json");
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
        foreach (var expected in manifest.Entries)
        {
            var found = actual.FirstOrDefault(a => a.Area == expected.Area && a.Name == expected.Name && a.Version == expected.Version);
            if (found is null) problems.Add($"{expected.Area} '{expected.Name}' fehlt im Paket.");
            else if (found.Sha256 != expected.Sha256) problems.Add($"{expected.Area} '{expected.Name}': Hash stimmt nicht.");
        }
        foreach (var extra in actual.Where(a => manifest.Entries.All(e => e.Area != a.Area || e.Name != a.Name || e.Version != a.Version)))
            problems.Add($"{extra.Area} '{extra.Name}' ist nicht im Manifest.");
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

    private static string Read(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name) ?? throw new ReleasePackageException($"'{name}' fehlt im Paket.");
        using var r = new StreamReader(e.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>Bereichshashes aus dem kanonischen Projekt-JSON.</summary>
    public static IReadOnlyList<ManifestEntry> ComputeEntries(string projectJson, TestProject project)
    {
        var root = JsonNode.Parse(projectJson)!.AsObject();
        var d = project.Definition;
        var list = new List<ManifestEntry>();

        var flow = root.DeepClone().AsObject();
        flow.Remove("library");
        flow.Remove("assemblies");
        flow.Remove("groupLibrary");
        flow.Remove("simulation");
        flow.Remove("simulationOutputs");
        flow.Remove("curves");
        list.Add(new ManifestEntry("Ablauf", d.Name, d.Version, Sha256(flow.ToJsonString())));

        if (root["library"] is JsonArray lib)
            foreach (var b in lib)
                list.Add(new ManifestEntry("Bausteine", (string?)b!["name"] ?? (string)b["id"]!, (string?)b["version"] ?? "", Sha256(b.ToJsonString())));
        if (root["groupLibrary"] is JsonArray groups)
            foreach (var b in groups)
                list.Add(new ManifestEntry("Bausteine", (string?)b!["name"] ?? (string)b["id"]!, (string?)b["version"] ?? "", Sha256(b.ToJsonString())));

        foreach (var a in d.Assemblies.OrderBy(a => a.Name, StringComparer.Ordinal))
            list.Add(new ManifestEntry("Codeartefakte", "DLL/" + a.Name, "", Sha256(a.Data)));

        foreach (var lang in CodeFiles(d).GroupBy(f => LanguageOf(f.Path)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var combined = string.Join("\n\u0000\n", lang.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => f.Path + "\n" + f.Source));
            list.Add(new ManifestEntry("Codeartefakte", $"{lang.Key} ({lang.Count()} Quelle(n))", d.Version, Sha256(combined)));
        }

        if (root["curves"] is JsonArray curves && curves.Count > 0)
            foreach (var c in curves)
                list.Add(new ManifestEntry("Kurven", (string?)c!["name"] ?? (string)c["id"]!, (string?)c["version"] ?? "", Sha256(c.ToJsonString())));
        else
            list.Add(new ManifestEntry("Kurven", "keine", "", Sha256("")));
        list.Add(new ManifestEntry("Definitionen", "Hardwareprofil (Simulation)", d.Version,
            Sha256((root["simulation"]?.ToJsonString() ?? "[]") + (root["simulationOutputs"]?.ToJsonString() ?? ""))));
        return list;
    }

    /// <summary>Quelltexte aller Bibliotheksbausteine und CodeBlocks im Ablauf.</summary>
    public static IEnumerable<(string Path, string Source)> CodeFiles(TestDefinition d)
    {
        foreach (var b in d.Library)
            yield return ($"code/bausteine/{Safe(b.Id)}{Extension(b.Language)}", b.Source);
        foreach (var s in d.AllNodes().OfType<StepDefinition>().Where(s => s.BlockType == "code"))
        {
            var lang = s.Parameters.GetValueOrDefault("Sprache") switch
            {
                "Python" => CodeLanguage.Python,
                "C++" => CodeLanguage.Cpp,
                _ => CodeLanguage.CSharp,
            };
            yield return ($"code/ablauf/{Safe(s.Id)}{Extension(lang)}", s.Parameters.GetValueOrDefault("Quelltext") ?? "");
        }
    }

    private static string Extension(CodeLanguage l) => l switch
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

    private static string Safe(string id) => new(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
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
        var a = Outer(serializer.Serialize(current), out var dutA, out var usedA, out var curvesA);
        var b = Outer(serializer.Serialize(next), out var dutB, out var usedB, out var curvesB);
        if (dutA is null || dutA != dutB)
        {
            reason = "Prüflingsgruppe geändert";
            return false;
        }
        if (a["variables"]?.ToJsonString() != b["variables"]?.ToJsonString()) { reason = "Hauptablauf-Variablen geändert"; return false; }
        if (a["assemblies"]?.ToJsonString() != b["assemblies"]?.ToJsonString()) { reason = ".NET-DLLs geändert; neue Prüfsitzung erforderlich"; return false; }
        if (a["init"]?.ToJsonString() != b["init"]?.ToJsonString()) { reason = "Init geändert"; return false; }
        if (a["finalization"]?.ToJsonString() != b["finalization"]?.ToJsonString()) { reason = "Finalization geändert"; return false; }
        if (a["loop"]?.ToJsonString() != b["loop"]?.ToJsonString()) { reason = "Loop außerhalb des Prüflings geändert"; return false; }
        foreach (var id in curvesA.Union(curvesB))
        {
            if (Curve(a, id) != Curve(b, id))
            {
                reason = $"Kurve '{id}' wird außerhalb des Prüflings verwendet und wurde geändert";
                return false;
            }
        }
        foreach (var id in usedA.Union(usedB))
        {
            if (Block(a, id) != Block(b, id))
            {
                reason = $"Baustein '{id}' wird außerhalb des Prüflings verwendet und wurde geändert";
                return false;
            }
        }
        reason = "";
        return true;
    }

    private static string? Curve(JsonObject root, string idAtVersion)
    {
        var parts = idAtVersion.Split('@');
        var versions = (root["curves"] as JsonArray)?.Where(c => (string?)c?["id"] == parts[0]).ToList() ?? [];
        var pick = parts.Length > 1 && parts[1].Length > 0 ? versions.FirstOrDefault(c => (string?)c?["version"] == parts[1]) : versions.LastOrDefault();
        return pick?.ToJsonString();
    }

    private static string? Block(JsonObject root, string id)
        => (root["library"] as JsonArray)?.Concat((root["groupLibrary"] as JsonArray) ?? []).FirstOrDefault(b => (string?)b?["id"] == id)?.ToJsonString();

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
            if (node is not JsonObject o) return;
            if (o["dutRun"]?.GetValue<bool>() == true)
            {
                found ??= (string?)o["id"];
                foreach (var key in o.Select(kv => kv.Key).Where(k => k is not ("id" or "art")).ToList()) o.Remove(key);
                return;
            }
            if (o["parameters"] is JsonObject p && (string?)p["Baustein"] is { Length: > 0 } b) used.Add(b);
            if (o["parameters"] is JsonObject pc && (string?)pc["Kurve"] is { Length: > 0 } curve) curves.Add(curve + "@" + ((string?)pc["Version"] ?? ""));
            if (o["children"] is JsonArray children) foreach (var c in children) Walk(c);
            Walk(o["exit"]);
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
