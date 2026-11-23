// Exports the application's real block descriptors; no independent copy of their defaults.
using System.Text.Json;
using Testumgebung.Persistence;
using Testumgebung.Engine.Blocks;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Labels.CabJScript;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
if (args.Length != 2 || args[0] is not ("export" or "check"))
    throw new ArgumentException("Expected: export <catalog.json> or check <project.tproj.json>");
if (args[0] == "export")
{
    var blocks = BlockCatalog.Default.All.OrderBy(b => b.Type, StringComparer.Ordinal).Select(b => new
    {
        b.Type, b.DisplayName, b.Category, b.Description,
        Parameters = b.Parameters.Select(p => p.VisibleWhen is null && !p.Advanced && p.Section.Length == 0
                ? (object)new { p.Name, p.Label, Kind = p.Kind.ToString(), p.Default, p.Required, p.Choices, p.Hint }
                : new { p.Name, p.Label, Kind = p.Kind.ToString(), p.Default, p.Required, p.Choices, p.Hint, p.VisibleWhen, p.Advanced, p.Section })
    });
    var catalog = new { Format = ProjectSerializer.FormatName, FormatVersion = ProjectSerializer.FormatVersion, Blocks = blocks };
    File.WriteAllText(args[1], JsonSerializer.Serialize(catalog, jsonOptions) + "\n");
    Console.WriteLine($"Exported {BlockCatalog.Default.All.Count} blocks.");
}
else
{
    var serializer = new ProjectSerializer();
    var project = serializer.Load(args[1]);
    var errors = project.Definition.Validate().ToList();
    foreach (var step in project.Definition.AllNodes().OfType<StepDefinition>())
        errors.AddRange(BlockCatalog.Default.Rebuild(step).Select(error => step.Id + ": " + error));
    foreach (var issue in CabProjectDiagnostics.Check(project.Definition))
        if (issue.IsError) errors.Add(issue.ToString());
        else Console.WriteLine(issue.ToString());
    if (errors.Count != 0)
    {
        foreach (var error in errors) Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }
    else
    {
        serializer.Deserialize(serializer.Serialize(project));
        Console.WriteLine("Project structure and block configuration valid. Code compilation and hardware behavior are not checked.");
    }
}
