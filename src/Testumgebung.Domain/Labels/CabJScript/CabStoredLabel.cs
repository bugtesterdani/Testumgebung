using System.Text.Json;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>Reads the existing embedded-file contract without depending on designer types.</summary>
public sealed class CabStoredLabel
{
    public const string BlockType = "etikett.drucken", FormatParameter = "Vorlagenformat", Format = "cab LBL";
    public const string DataParameter = "CabLblDaten", LayoutParameter = "CabLblLayout";
    public CabDocument Document { get; }
    public CabValidationResult Validation { get; }
    public string SetupCodePage { get; }
    public CabDocumentSettings? Settings { get; }
    public const string PlaceholderEncodingError = "cab-Platzhalter in nicht dekodierbarem Text: Bitte die Codepage der Vorlage korrigieren.";
    public bool HasUndecodablePlaceholders => Validation.Commands.SelectMany(c => c.Parameters)
        .Where(CabContentValidation.IsContent).Any(p => p.Value is null
            && p.Parts.Any(part => Document.Bytes(part.Range).Contains((byte)'{')));

    public CabStoredLabel(byte[] data, string setupCodePage = "UTF-8", CabDocumentSettings? settings = null)
    {
        SetupCodePage = setupCodePage; Settings = settings;
        Document = CabParser.Parse(data, new(setupCodePage));
        Validation = CabValidator.Validate(Document, settings);
    }

    public static bool IsCab(StepDefinition step) => step.BlockType == BlockType
        && step.Parameters.GetValueOrDefault(FormatParameter) == Format;

    public static CabStoredLabel Read(IReadOnlyDictionary<string, string> parameters)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(parameters.GetValueOrDefault(DataParameter) ?? ""); }
        catch (FormatException error) { throw new InvalidDataException("Die eingebettete cab-LBL-Druckdatei enthält ungültige Base64-Daten.", error); }
        if (bytes.Length == 0) throw new InvalidDataException("Die eingebettete cab-LBL-Druckdatei ist leer.");
        try
        {
            var (page, settings) = Options(parameters.GetValueOrDefault(LayoutParameter));
            return new(bytes, page, settings);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        { throw new InvalidDataException("Die gespeicherte cab-Konfiguration ist ungültig. " + error.Message, error); }
    }

    private static (string Page, CabDocumentSettings? Settings) Options(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ("UTF-8", null);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null) return ("UTF-8", null);
        var page = "UTF-8";
        if (root.TryGetProperty("CabSource", out var source) && source.ValueKind != JsonValueKind.Null
            && source.TryGetProperty("SetupCodePage", out var encoding)) page = encoding.GetString() ?? page;
        var settings = root.TryGetProperty("CabSettings", out var options) && options.ValueKind != JsonValueKind.Null
            ? options.Deserialize<CabDocumentSettings>() : null;
        return (page, settings);
    }
}

public sealed record CabProjectIssue(string NodeId, string Message, CabDiagnosticSeverity Severity)
{
    public bool IsError => Severity == CabDiagnosticSeverity.Error;
    public override string ToString() => $"Schritt {NodeId}: {SeverityText} cab JScript: {Message}";
    private string SeverityText => Severity switch { CabDiagnosticSeverity.Error => "Fehler", CabDiagnosticSeverity.Warning => "Warnung", _ => "Hinweis" };
}

/// <summary>Reports source diagnostics without executing commands or changing project parameters.</summary>
public static class CabProjectDiagnostics
{
    public static IReadOnlyList<CabProjectIssue> Check(TestDefinition definition)
        => definition.EnabledNodes().OfType<StepDefinition>().SelectMany(ForStep).ToArray();

    public static IReadOnlyList<CabProjectIssue> ForStep(StepDefinition step)
    {
        if (!CabStoredLabel.IsCab(step)) return [];
        try
        {
            var stored = CabStoredLabel.Read(step.Parameters);
            var issues = stored.Validation.Diagnostics.Select(d => new CabProjectIssue(step.Id,
                $"Zeile {d.Line}, Spalte {d.Column}: {d.Message} ({d.ManualReference})", d.Severity)).ToList();
            if (stored.HasUndecodablePlaceholders)
                issues.Add(new(step.Id, CabStoredLabel.PlaceholderEncodingError, CabDiagnosticSeverity.Error));
            return issues.OrderByDescending(i => i.Severity).ToArray();
        }
        catch (InvalidDataException error) { return [new(step.Id, error.Message, CabDiagnosticSeverity.Error)]; }
    }
}
