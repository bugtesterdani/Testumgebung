namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabSemanticBinder
{
    internal static IReadOnlyList<CabBoundCommand> Bind(CabDocument document, CabDiagnosticCollector diagnostics)
    {
        var slices = CabLogicalReader.Read(document);
        var commands = new List<CabBoundCommand>();
        var nextJob = 0;
        var currentJob = -1;
        for (var index = 0; index < slices.Count; index++)
        {
            var slice = slices[index];
            if (slice.Node.Command == "J") currentJob = nextJob++;
            var next = slices.Skip(index + 1).FirstOrDefault(s => s.Node.Kind != CabNodeKind.EscapeCommand);
            BindNode(slice, next?.Node, currentJob, diagnostics, commands);
        }
        return commands.AsReadOnly();
    }

    private static void BindNode(CabCommandSlice slice, CabSyntaxNode? next, int job,
        CabDiagnosticCollector diagnostics, List<CabBoundCommand> commands)
    {
        var node = slice.Node;
        var resolved = CabCommandResolver.Resolve(slice);
        if (resolved is null)
        {
            diagnostics.Add("unknown-command", CabDiagnosticSeverity.Warning, "Unbekannter Befehl bleibt unverändert erhalten.", CabCatalog.Default["JS-1.3"], node);
            return;
        }
        var builder = new CabBindingBuilder(CabDefinitions.Default[resolved.Value.Id], slice, job, resolved.Value.BaseId);
        CabCommandGrammar.Bind(builder, next);
        commands.Add(builder.Build(node.Command != "<BASE64>"));
    }
}
