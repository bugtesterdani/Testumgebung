/**
 * @file
 * @brief Projekt prüfen (echter Serializer, Ablaufregeln, Bausteinkonfiguration) und Codeartefakte bauen.
 */

using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;

namespace Testumgebung.Cli;

public static class ProjectChecks
{
    /// <summary>Lädt und prüft ein Projekt. Project null: nicht ladbar; Valid false: Struktur- oder Bausteinfehler.</summary>
    public static (TestProject? Project, bool Valid) Check(string path, Report report)
    {
        var suite = SuiteName(path);
        TestProject project;
        var serializer = new ProjectSerializer();
        try
        {
            project = serializer.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or InvalidOperationException or ArgumentException or FormatException
                                       or KeyNotFoundException or NotSupportedException)
        {
            report.Fail(suite, "Projekt laden", ex.Message);
            return (null, false);
        }
        report.Pass(suite, "Projekt laden", $"{project.Definition.Name} v{project.Definition.Version}");

        var structure = project.Definition.Validate().ToList();
        if (structure.Count > 0) report.Fail(suite, "Ablaufstruktur", string.Join(Environment.NewLine, structure));
        else report.Pass(suite, "Ablaufstruktur");

        var blockErrors = new List<string>();
        var hints = new List<string>();
        foreach (var step in project.Definition.AllRoots.SelectMany(r => r.Descendants()).OfType<StepDefinition>())
        {
            var errors = BlockCatalog.Default.Rebuild(step);
            if (errors.Count == 0) continue;
            blockErrors.AddRange(errors.Select(e => $"Schritt {step.Id} ({step.BlockType}): {e}"));
            if (step.BlockType is { } type && !hints.Any(h => h.StartsWith(type + " ", StringComparison.Ordinal)))
                hints.Add(CatalogText.Describe(type));
        }
        if (blockErrors.Count > 0)
        {
            report.Fail(suite, "Bausteinkonfiguration", string.Join(Environment.NewLine, blockErrors),
                hints.Count > 0 ? "Bausteinkatalog:" + Environment.NewLine + string.Join(Environment.NewLine, hints) : null);
        }
        else
        {
            report.Pass(suite, "Bausteinkonfiguration");
        }

        var roundtripValid = true;
        try
        {
            var json = serializer.Serialize(project);
            serializer.Serialize(serializer.Deserialize(json));
            report.Pass(suite, "Speichern und erneut laden");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
        {
            roundtripValid = false;
            report.Fail(suite, "Speichern und erneut laden", ex.Message);
        }
        return (project, structure.Count == 0 && blockErrors.Count == 0 && roundtripValid);
    }

    /// <summary>Wie „Build prüfen“ bzw. die Entwicklungsprüfung: Bibliotheksbausteine und CodeBlocks im Ablauf übersetzen.</summary>
    public static async Task BuildAsync(string path, TestProject project, ICodeRuntime runtime, Report report, CancellationToken cancellationToken)
    {
        var suite = SuiteName(path);
        var d = project.Definition;
        var requests = new List<(string Name, CodeRequest Request)>();
        foreach (var b in d.Library)
            requests.Add(($"Build Baustein {b.Id} ({b.Language})", new CodeRequest(b.Language, b.Source, b.Ports) { Assemblies = d.Assemblies }));
        foreach (var s in d.AllRoots.SelectMany(r => r.Descendants()).OfType<StepDefinition>().Where(s => s.BlockType == BlockCatalog.Code))
        {
            var ports = CodeBlockConverter.VisibleVariables(d, s.Id).Select(v => new CodePort(v.Name, v.Type, PortDirection.InOut)).ToList();
            var language = s.Parameters.GetValueOrDefault("Sprache") ?? "C#";
            requests.Add(($"Build CodeBlock {s.Id} ({language})",
                new CodeRequest(BlockCatalog.ParseLanguage(language), s.Parameters.GetValueOrDefault("Quelltext") ?? "", ports) { Assemblies = d.Assemblies }));
        }
        if (requests.Count == 0)
        {
            report.Pass(suite, "Codeartefakte", "keine im Projekt");
            return;
        }
        foreach (var (name, request) in requests)
        {
            try
            {
                var result = await runtime.CompileAsync(request, cancellationToken).ConfigureAwait(false);
                var lines = result.Diagnostics.Select(x => $"{(x.IsError ? "Fehler" : "Warnung")} {x}").ToList();
                if (result.Success) report.Pass(suite, name, $"{result.Warnings} Warnung(en)", lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null);
                else report.Fail(suite, name, string.Join(Environment.NewLine, lines.Where(l => l.StartsWith("Fehler", StringComparison.Ordinal)).Take(20)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.Fail(suite, name, "Übersetzung nicht möglich: " + ex.Message);
            }
        }
    }

    /// <summary>Names remain readable while the relative path separates identically named projects in different folders.</summary>
    public static string SuiteName(string path)
        => $"{Path.GetFileName(path)} [{Path.GetRelativePath(Environment.CurrentDirectory, Path.GetFullPath(path)).Replace('\\', '/')}]";
}
