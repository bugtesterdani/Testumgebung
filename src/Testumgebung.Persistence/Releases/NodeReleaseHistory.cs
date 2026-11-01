/**
 * @file
 * @brief Rekonstruiert Wertezeiträume einer Ablaufkarte aus geprüften Release-Paketen.
 */
using System.Text.Json;

namespace Testumgebung.Persistence.Releases;

/// <summary>Ein gespeicherter Feldwert ab einer Freigabe bis zur nächsten Änderung dieses Feldes.</summary>
public sealed record NodeValuePeriod(string Field, string? Before, string? Value, ReleaseInfo Since,
    ReleaseInfo? Until, bool FirstRecorded)
{
    public string BeforeText => FirstRecorded ? "Vorher nicht belegt" : Before ?? "Nicht gesetzt / nicht vorhanden";
    public string ValueText => Value ?? "Nicht gesetzt / Standard oder Vererbung";
    public string SinceText => $"v{Since.Version} · {Since.ReleasedAt?.ToLocalTime():dd.MM.yyyy HH:mm:ss}";
    public string UntilText => Until is null ? "Letzte erfasste Freigabe" : $"bis vor v{Until.Version} · {Until.ReleasedAt?.ToLocalTime():dd.MM.yyyy HH:mm:ss}";
    public string ChangeText => FirstRecorded ? "Erster belegter Stand" : Value is null ? "Entfernt / zurückgesetzt" : Before is null ? "Hinzugefügt / gesetzt" : "Geändert";
    public bool IsLatestValue => Until is null && Value is not null;
}

/// <summary>Historie gespeicherter Konfiguration; keine Aussage über Laufzeitwerte oder Übernahme am Prüfplatz.</summary>
public static class NodeReleaseHistory
{
    public static async Task<IReadOnlyList<NodeValuePeriod>> LoadAsync(ReleaseService service,
        string projectName, string nodeId, CancellationToken ct = default)
    {
        var releases = (await service.ListAsync(ct).ConfigureAwait(false))
            .Where(r => r.ProjectName == projectName && r.State is ReleaseState.Released or ReleaseState.Archived)
            .OrderBy(r => r.ReleasedAt).ThenBy(r => r.ReleaseId, StringComparer.Ordinal).ToList();
        if (releases.Any(r => r.ReleasedAt is null))
            throw new ReleaseRuleException("Historie unvollständig: Einer Version fehlt der Freigabezeitpunkt.");
        if (releases.GroupBy(r => r.ReleasedAt).Any(g => g.Count() > 1))
            throw new ReleaseRuleException("Die Reihenfolge gleichzeitiger Freigaben ist nicht eindeutig belegbar.");
        var result = new List<NodeValuePeriod>();
        var active = new Dictionary<string, int>(StringComparer.Ordinal);
        var previous = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        for (int i = 0; i < releases.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var release = releases[i];
            var opened = await service.OpenAsync(release, ct).ConfigureAwait(false);
            if (opened.Project.Definition.Name != release.ProjectName || opened.Project.Definition.Version != release.Version)
                throw new ReleasePackageException($"v{release.Version}: Metadaten passen nicht zum gespeicherten Paket.");
            if (!opened.IsIntact)
                throw new ReleasePackageException($"v{release.Version}: Paket beschädigt. Keine gesicherte Wertehistorie möglich.");
            var current = Capture(opened.Project, nodeId);
            AddChanges(result, active, previous, current, release, i == 0);
            previous = current;
        }
        return result;
    }

    private sealed record FieldValue(string Label, string Json)
    {
        public string Display => Json == "\"\"" ? Json : Json.StartsWith('"') ? JsonSerializer.Deserialize<string>(Json)! : Json;
    }

    private static void AddChanges(List<NodeValuePeriod> result, Dictionary<string, int> active,
        Dictionary<string, FieldValue> previous, Dictionary<string, FieldValue> current, ReleaseInfo release, bool first)
    {
        foreach (var key in previous.Keys.Union(current.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            previous.TryGetValue(key, out var before);
            current.TryGetValue(key, out var after);
            if (before?.Json == after?.Json) continue;
            if (active.TryGetValue(key, out int index)) result[index] = result[index] with { Until = release };
            active[key] = result.Count;
            result.Add(new(after?.Label ?? before!.Label, before?.Display, after?.Display, release, null, first));
        }
    }

    private static Dictionary<string, FieldValue> Capture(TestProject project, string nodeId)
    {
        using var document = JsonDocument.Parse(new ProjectSerializer().Serialize(project));
        var node = Find(document.RootElement, nodeId);
        if (node is null) return new(StringComparer.Ordinal);
        var definition = project.Definition.Find(nodeId)!;
        var result = CaptureOwnFields(definition, node.Value);
        CaptureReferences(definition, document.RootElement, result);
        return result;
    }

    // Kinder haben eigene Kartenhistorien. Die ID ist die Identität; Aktivierung wird mit Standardwerten erfasst.
    private static readonly HashSet<string> ExcludedFields = new(StringComparer.Ordinal)
        { "id", "art", "children", "exit", "disabled", "parallel" };

    private static Dictionary<string, FieldValue> CaptureOwnFields(Domain.Definitions.NodeDefinition definition, JsonElement node)
    {
        var result = new Dictionary<string, FieldValue>(StringComparer.Ordinal)
        {
            ["@kind"] = new("Elementart", JsonSerializer.Serialize(definition is Domain.Definitions.GroupDefinition ? "Gruppe" : "Schritt")),
            ["disabled"] = new("Deaktiviert", definition.Disabled ? "true" : "false")
        };
        if (definition is Domain.Definitions.GroupDefinition group)
            result.Add("parallel", new("Parallele Ausführung", group.Parallel ? "true" : "false"));
        foreach (var property in node.EnumerateObject())
            if (!ExcludedFields.Contains(property.Name))
                Flatten(property.Value, property.Name, Label(property.Name), result);
        return result;
    }

    /// <summary>Ein unveränderter Aufruf kann geänderten Bibliothekscode verwenden; diesen ebenfalls belegen.</summary>
    private static void CaptureReferences(Domain.Definitions.NodeDefinition definition, JsonElement project, Dictionary<string, FieldValue> result)
    {
        if (definition is not Domain.Definitions.StepDefinition step) return;
        if (step.BlockType != Engine.Blocks.BlockCatalog.LibraryBlock && step.BlockType != Engine.Blocks.BlockCatalog.GroupBlock) return;
        if (!step.Parameters.TryGetValue("Baustein", out var reference)) return;
        AddReference(project, "library", reference, result);
        AddReference(project, "groupLibrary", reference, result);
    }

    private static void AddReference(JsonElement project, string collection, string id, Dictionary<string, FieldValue> result)
    {
        if (!project.TryGetProperty(collection, out var items)) return;
        foreach (var item in items.EnumerateArray())
            if (item.GetProperty("id").GetString() == id)
                Flatten(item, collection, "Verwendeter Bibliotheksbaustein", result);
    }

    private static JsonElement? Find(JsonElement element, string id)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("id", out var key) && key.GetString() == id &&
                (element.TryGetProperty("children", out _) || element.TryGetProperty("block", out _))) return element;
            foreach (var property in element.EnumerateObject())
                if (Find(property.Value, id) is { } found) return found;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                if (Find(item, id) is { } found) return found;
        return null;
    }

    /// <summary>Objektfelder einzeln, benannte Variablen nach Namen statt nach ihrer Listenposition vergleichen.</summary>
    private static void Flatten(JsonElement element, string key, string label, Dictionary<string, FieldValue> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                Flatten(property.Value, key + "/" + Uri.EscapeDataString(property.Name), label + " / " + Label(property.Name), result);
        }
        else if (element.ValueKind == JsonValueKind.Array && TryNames(element, out var names))
        {
            result.Add(key + "/@order", new FieldValue(label + " / Reihenfolge", JsonSerializer.Serialize(names)));
            int i = 0;
            foreach (var item in element.EnumerateArray())
            {
                var name = names[i++];
                Flatten(item, key + "/[" + Uri.EscapeDataString(name) + "]", label + " / " + name, result);
            }
        }
        else result.Add(key, new FieldValue(label, element.GetRawText()));
    }

    private static bool TryNames(JsonElement array, out List<string> names)
    {
        names = [];
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return false;
            names.Add(name.GetString()!);
        }
        return names.Count > 0 && names.Distinct(StringComparer.Ordinal).Count() == names.Count;
    }

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["name"] = "Bezeichnung", ["description"] = "Kommentar / Verlauf", ["parameters"] = "Parameter",
        ["block"] = "Bausteintyp", ["variables"] = "Variablen", ["type"] = "Typ", ["initial"] = "Anfangswert",
        ["condition"] = "Eintrittsbedingung", ["repeat"] = "Wiederholung", ["mode"] = "Modus", ["count"] = "Anzahl",
        ["durationMs"] = "Dauer (ms)", ["expression"] = "Ausdruck", ["parallel"] = "Parallele Ausführung",
        ["disabled"] = "Deaktiviert", ["resources"] = "Reservierungen", ["resourceTimeoutMs"] = "Wartezeit (ms)",
        ["storage"] = "Anzeige / Speicherung", ["source"] = "Quelltext", ["ports"] = "Schnittstelle",
        ["version"] = "Version", ["breakpointRule"] = "Ablauf-Haltebedingung", ["codeBreakpointRules"] = "Code-Haltebedingungen",
        ["language"] = "Sprache", ["direction"] = "Richtung", ["unit"] = "Einheit", ["testValue"] = "Testwert",
        ["resultBeforeRepeat"] = "Ergebnis bei Wiederholung", ["stopWithinIteration"] = "Innerhalb der Wiederholung abbrechen",
        ["interfaceBoundary"] = "Schnittstellengrenze", ["dutRun"] = "Prüflingslauf", ["dutIdVariable"] = "Seriennummernvariable",
        ["display"] = "Anzeigen", ["file"] = "Datei", ["fileWaveforms"] = "Kurvendatei", ["database"] = "Datenbank",
        ["databaseWaveforms"] = "Kurvendatenbank", ["firstHit"] = "Ab Treffer", ["every"] = "Alle Treffer"
    };

    private static string Label(string property) => Labels.TryGetValue(property, out var text) ? text : property;
}
