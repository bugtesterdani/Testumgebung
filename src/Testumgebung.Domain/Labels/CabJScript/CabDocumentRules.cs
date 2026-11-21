using System.Globalization;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabDocumentRules
{
    internal static void Validate(IReadOnlyList<CabBoundCommand> commands, CabDocumentSettings settings, CabDiagnosticCollector diagnostics)
    {
        var downloads = commands.Where(c => c.JobIndex < 0 && c.Syntax.Command == "d").ToArray();
        var hasJobs = commands.Any(c => c.JobIndex >= 0);
        foreach (var job in commands.GroupBy(c => c.JobIndex))
        {
            Names(job, diagnostics);
            if (job.Key >= 0) ObjectLimits(downloads.Concat(job), settings.GenerationForJob(job.Key), diagnostics);
            else if (!hasJobs) ObjectLimits(job, settings.DefaultGeneration, diagnostics);
            CabPositionRules.Validate(job.ToArray(), diagnostics);
        }
        foreach (var command in commands)
        {
            CabFontRules.Validate(command, diagnostics);
            CabPathRules.Validate(command, settings.GenerationForJob(command.JobIndex), diagnostics);
            CabBarcodeRules.Validate(command, diagnostics);
            Combinations(command, diagnostics);
        }
    }

    private static void Names(IEnumerable<CabBoundCommand> commands, CabDiagnosticCollector diagnostics)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            var declaration = Declaration(command);
            if (declaration?.Value is { Length: > 0 } name && !names.Add(name))
                diagnostics.Add("duplicate-name", CabDiagnosticSeverity.Error, "Feldname ist bereits definiert: " + name,
                    declaration.Rule.Entry, declaration.Syntax!);
            var reference = command.Syntax.Command switch { "R" => command.Parameter("name"), "A" => command.Parameter("VAR"), _ => null };
            if (reference?.Value is { Length: > 0 } target && !names.Contains(target))
                diagnostics.Add("field-reference", CabDiagnosticSeverity.Error, "Feld wurde zuvor nicht definiert: " + target,
                    reference.Rule.Entry, reference.Syntax!);
        }
    }

    private static CabBoundParameter? Declaration(CabBoundCommand command)
        => command.Syntax.Command switch
        { "T" or "B" or "G" or "W" => command.Parameter("name"), "I" => command.Parameter("fieldname"), _ => null };

    private static void ObjectLimits(IEnumerable<CabBoundCommand> commands, CabPrinterGeneration generation, CabDiagnosticCollector diagnostics)
    {
        var entry = CabCatalog.Default["JS-1.6"];
        var limits = entry.Metadata.GetProperty("objektgrenzen");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            var type = command.Syntax.Command;
            if (type == "d" && command.Parameter("type")?.Value is "IMG" or "PCX" or "BMP") type = "d IMG/PCX/BMP";
            if (!limits.TryGetProperty(type, out var byGeneration) || !byGeneration.TryGetProperty(generation.ToString(), out var maximum)) continue;
            var count = counts.GetValueOrDefault(type) + 1;
            counts[type] = count;
            if (count > maximum.GetInt32()) diagnostics.Add("object-limit", CabDiagnosticSeverity.Error,
                "Objektgrenze für " + type + " überschritten (" + maximum.GetInt32() + ").", entry, command.Syntax);
        }
    }

    private static void Combinations(CabBoundCommand command, CabDiagnosticCollector diagnostics)
    {
        if (command.Syntax.Command == "I" && command.Parameter("a") is not null
            && (command.Parameter("mx") is null || command.Parameter("my") is null))
            diagnostics.Add("autoload-scale", CabDiagnosticSeverity.Error, "Autoload benötigt mx und my.", command.Definition.Catalog, command.Syntax);
        if (command.Syntax.Command == "C" && command.Parameter("disp2") is not null
            && command.Parameter("param")?.Value is "s" or "p" or "sp")
            diagnostics.Add("cut-offset", CabDiagnosticSeverity.Error, "Zweiter Schnittversatz ist mit diesem Schnittmodus nicht zulässig.", command.Definition.Catalog, command.Syntax);
        ApplicatorTime(command, diagnostics);
    }

    private static void ApplicatorTime(CabBoundCommand command, CabDiagnosticCollector diagnostics)
    {
        var timers = command.Parameter("Ax=y");
        if (timers?.Value is { } timer)
        {
            var equals = timer.IndexOf('=');
            if (equals >= 0 && (!decimal.TryParse(timer.AsSpan(equals + 1), CultureInfo.InvariantCulture, out var time) || time > 1000))
                diagnostics.Add("applicator-time", CabDiagnosticSeverity.Error, "Applikatorzeit darf 1000 ms nicht überschreiten.", timers.Rule.Entry, timers.Syntax!);
        }
    }

    internal static decimal? Number(CabBoundCommand command, string name)
        => decimal.TryParse(command.Parameter(name)?.Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
            | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var value) ? value : null;
    internal static decimal Millimetres(decimal value, CabUnit unit) => unit == CabUnit.Inches ? value * 25.4m : value;
}
