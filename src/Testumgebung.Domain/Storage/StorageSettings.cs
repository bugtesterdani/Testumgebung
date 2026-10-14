/**
 * @file
 * @brief Löst geerbte Speicherregeln für Anzeige, Datei und Datenbank unabhängig voneinander auf.
 */

using Testumgebung.Domain.Definitions;

namespace Testumgebung.Domain.Storage;

/// <summary>Ziel einer Datenübernahme (Abschnitt 9.1). Die Ziele sind unabhängig voneinander.</summary>
public enum StorageTarget
{
    /// <summary>Werkeranzeige (Protokoll, Messwerttabelle).</summary>
    Display,
    /// <summary>Werte und Bewertungen in der Ergebnisdatei.</summary>
    File,
    /// <summary>Kurven in der Ergebnisdatei.</summary>
    FileWaveforms,
    /// <summary>Werte und Bewertungen in der Datenbank (nur bei konfigurierter Verbindung).</summary>
    Database,
    /// <summary>Kurven in der Datenbank.</summary>
    DatabaseWaveforms,
}

/// <summary>
/// Filtereinstellungen eines Knotens. null = vom Elternknoten erben (Gruppendefault), sonst expliziter Override.
/// Ohne Einstellung in der ganzen Elternkette ist alles aktiv.
/// </summary>
public sealed record StorageSettings(
    bool? Display = null,
    bool? File = null,
    bool? FileWaveforms = null,
    bool? Database = null,
    bool? DatabaseWaveforms = null)
{
    public static StorageSettings Inherit { get; } = new();

    public bool IsInherit => this == Inherit;

    public bool? Get(StorageTarget target) => target switch
    {
        StorageTarget.Display => Display,
        StorageTarget.File => File,
        StorageTarget.FileWaveforms => FileWaveforms,
        StorageTarget.Database => Database,
        StorageTarget.DatabaseWaveforms => DatabaseWaveforms,
        _ => null,
    };

    public StorageSettings With(StorageTarget target, bool? value) => target switch
    {
        StorageTarget.Display => this with { Display = value },
        StorageTarget.File => this with { File = value },
        StorageTarget.FileWaveforms => this with { FileWaveforms = value },
        StorageTarget.Database => this with { Database = value },
        StorageTarget.DatabaseWaveforms => this with { DatabaseWaveforms = value },
        _ => this,
    };

    public static IReadOnlyList<StorageTarget> Targets { get; } = Enum.GetValues<StorageTarget>();

    public static string Label(StorageTarget target) => target switch
    {
        StorageTarget.Display => "Anzeige",
        StorageTarget.File => "Ergebnisdatei",
        StorageTarget.FileWaveforms => "Kurven in Datei",
        StorageTarget.Database => "Datenbank",
        StorageTarget.DatabaseWaveforms => "Kurven in Datenbank",
        _ => target.ToString(),
    };

    /// <summary>Technische Mindestmenge, die unabhängig von allen Filtern erhalten bleibt (für die UI-Kennzeichnung).</summary>
    public const string MinimalSetDescription =
        "Immer gespeichert (technische Mindestmenge): Run-ID, Prüflingskennung, Start/Ende, Gesamtbewertung, Ablaufstatus, " +
        "Testname/-version und Ablaufstruktur (IDs, Namen). Gefilterte Werte, Bewertungen und Kurven werden nicht übernommen.";
}

/// <summary>Effektive Filter: Override am Knoten, sonst nächster Vorfahre mit Override, sonst aktiv.</summary>
public sealed class StorageResolver
{
    private readonly TestDefinition _definition;
    private readonly Dictionary<(string, StorageTarget), bool> _cache = [];

    public StorageResolver(TestDefinition definition) => _definition = definition;

    /// <summary>Alles aktiv (z. B. für Ereignisse ohne Knoten).</summary>
    public bool IsEnabled(string? nodeId, StorageTarget target)
    {
        if (nodeId is null) return true;
        lock (_cache)
        {
            if (_cache.TryGetValue((nodeId, target), out var cached)) return cached;
            var result = Resolve(nodeId, target);
            _cache[(nodeId, target)] = result;
            return result;
        }
    }

    /// <summary>Effektiver Wert und ob er geerbt ist (für die Editoranzeige).</summary>
    public (bool Value, string? Source) Explain(string nodeId, StorageTarget target)
    {
        var node = _definition.Find(nodeId);
        if (node?.Storage.Get(target) is { } own) return (own, null);
        IReadOnlyList<GroupDefinition> ancestors;
        try { ancestors = _definition.AncestorsOf(nodeId); }
        catch (KeyNotFoundException) { return (true, null); }
        for (var i = ancestors.Count - 1; i >= 0; i--)
            if (ancestors[i].Storage.Get(target) is { } v) return (v, ancestors[i].Name);
        return (true, "Standard");
    }

    private bool Resolve(string nodeId, StorageTarget target) => Explain(nodeId, target).Value;
}
