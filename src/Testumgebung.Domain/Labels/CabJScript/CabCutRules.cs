namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabCutRules
{
    internal static void Validate(IReadOnlyList<CabBoundCommand> commands, CabDiagnosticCollector diagnostics)
    {
        var size = commands.LastOrDefault(c => c.Syntax.Command == "S");
        if (size is null || CabDocumentRules.Number(size, "ho") is not { } height) return;
        var millimetres = CabDocumentRules.Millimetres(height, size.Syntax.Unit);
        if (millimetres <= 0) return;
        foreach (var cut in commands.Where(c => c.Syntax.Command == "C"))
        {
            if (CabDocumentRules.Number(cut, "disp1") is not { } displacement) continue;
            if (CabDocumentRules.Millimetres(displacement, cut.Syntax.Unit) >= millimetres)
                diagnostics.Add("cut-displacement", CabDiagnosticSeverity.Error,
                    "Erster Schnittversatz muss kleiner als die Etikettenhöhe sein.", cut.Definition.Catalog, cut.Parameter("disp1")!.Syntax!);
        }
    }
}
