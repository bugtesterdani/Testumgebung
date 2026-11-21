namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabPathRules
{
    internal static void Validate(CabBoundCommand command, CabPrinterGeneration generation, CabDiagnosticCollector diagnostics)
    {
        var entry = CabCatalog.Default["JS-1.5"];
        var media = entry.Parameters.Single(p => p.Name == "path");
        foreach (var parameter in command.Parameters.Where(p => p.IsPresent && p.Rule.DataType == CabDataType.Path && p.Value is not null))
        {
            var text = parameter.Value!;
            string? medium = null;
            if (parameter.Rule.Parameter.Name == "path") medium = text.Trim('/');
            else if (text.StartsWith('/')) medium = text.Split('/').ElementAtOrDefault(1);
            if (string.IsNullOrEmpty(medium)) continue;
            var value = media.Values.FirstOrDefault(v => v.Value == medium);
            if (value is null) diagnostics.Add("path", CabDiagnosticSeverity.Error, "Unbekannter Speicherpfad: " + medium, entry, parameter.Syntax!);
            else CabValidator.Unavailable(value.Availability(generation), medium, entry, parameter.Syntax!, diagnostics);
        }
    }
}
