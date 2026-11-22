using System.Reflection;
using System.Text;
using System.Text.Json;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
internal sealed class CatalogCaseAttribute(string id, string file) : Attribute
{
    public string Id { get; } = id;
    public string File { get; } = file;
    public string Mode { get; set; } = "command";
    public string? DefinitionId { get; set; }
    public string? Parameter { get; set; }
    public string? Value { get; set; }
    public string? Input { get; set; }
    public CabPrinterGeneration Generation { get; set; } = CabPrinterGeneration.X4;
}

public sealed class CabJScriptCatalogCoverageTests
{
    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory.Parent is not null && !File.Exists(Path.Combine(directory.FullName, "Testumgebung.sln"))) directory = directory.Parent;
        return directory.FullName;
    }

    internal static CatalogCaseAttribute[] Inventory() => typeof(CabCatalogCases).GetCustomAttributes<CatalogCaseAttribute>().ToArray();
    internal static string Fixture(string file) => Path.Combine(RepositoryRoot(), "tests", "Fixtures", "cab", file);

    public static IEnumerable<object[]> Entries() => Inventory().Where(c => c.Parameter is null)
        .Select(c => new object[] { c.Id, c.File, c.Mode, c.DefinitionId! });
    public static IEnumerable<object[]> Values() => Inventory().Where(c => c.Parameter is not null)
        .Select(c => new object[] { c.Id, c.Parameter!, c.Value!, c.Input!, c.Generation });

    [Fact]
    public void Repository_catalog_and_definitions_match_in_both_directions()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "docs/cab/jscript-befehlskatalog.json")));
        var entries = source.RootElement.GetProperty("eintraege").EnumerateArray().ToArray();
        Assert.Equal(entries.Select(e => e.GetProperty("id").GetString()).Order(), CabCatalog.Default.Entries.Select(e => e.Id).Order());
        var definitions = CabDefinitions.Default.Commands.Concat(CabContentParser.Definitions.Select(e =>
            new CabCommandDefinition(e, e.Parameters.Select(p => CabDefinitions.ParameterRule(e.Id, p.Name)).ToArray()))).ToArray();
        Assert.Equal(237, definitions.Length);
        Assert.Equal(CabCatalog.Default.Entries.Select(e => e.Id).Order(), definitions.Select(d => d.Catalog.Id).Order());
        foreach (var entry in entries)
        {
            var id = entry.GetProperty("id").GetString()!;
            Assert.True(entry.GetProperty("geprueft").GetBoolean(), id);
            var definition = Assert.Single(definitions, d => d.Catalog.Id == id);
            Assert.True(JsonElement.DeepEquals(entry, definition.Catalog.Metadata), id);
            Assert.Equal(entry.GetProperty("parameter").EnumerateArray().Select(p => p.GetProperty("name").GetString()), definition.Parameters.Select(p => p.Parameter.Name));
            foreach (var parameter in definition.Parameters)
                Assert.Equal(parameter.Parameter.Metadata.GetProperty("werte").EnumerateArray().Select(v => v.GetProperty("wert").GetString()), parameter.Parameter.Values.Select(v => v.Value));
        }
    }

    [Fact]
    public void Explicit_attribute_inventory_covers_every_entry_and_value_once()
    {
        var cases = Inventory();
        Assert.Equal(237, cases.Count(c => c.Parameter is null));
        Assert.Equal(1801, cases.Count(c => c.Parameter is not null));
        Assert.Equal(CabCatalog.Default.Entries.Select(e => e.Id).Order(), cases.Where(c => c.Parameter is null).Select(c => c.Id).Order());
        var expected = CabCatalog.Default.Entries.SelectMany(e => e.Parameters.SelectMany(p => p.Values.Select(v => e.Id + "/" + p.Name + "/" + v.Value))).Order();
        Assert.Equal(expected, cases.Where(c => c.Parameter is not null).Select(c => c.Id + "/" + c.Parameter + "/" + c.Value).Order());
        Assert.All(cases, c => Assert.True(File.Exists(Fixture(c.File)), c.File));
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void Every_entry_has_a_parsed_validated_lossless_protocol_fixture(string id, string file, string mode, string definitionId)
    {
        var bytes = File.ReadAllBytes(Fixture(file));
        var document = CabParser.Parse(bytes);
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, id + ": " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == CabDiagnosticSeverity.Error).Select(d => d.Message)));
        Assert.Equal(bytes, document.Write());
        Assert.Equal(bytes, CabParser.Parse(document.Write()).Write());
        if (mode == "content")
            Assert.Equal(id, Assert.Single(CabContentValidation.Bind(document, result.Commands).Last().Syntax.Fields).Definition.Id);
        else if (mode == "context") Context(id, document, result);
        else Assert.Contains(result.Commands, c => c.Definition.Catalog.Id == definitionId || c.BaseCatalogId == definitionId);
    }

    private static void Context(string id, CabDocument document, CabValidationResult result)
    {
        switch (id)
        {
            case "JS-1.3": Assert.Equal("00", Assert.Single(result.Commands, c => c.Syntax.Command == "T").Parameter("x")!.Value); break;
            case "JS-1.4":
                Assert.Contains(result.Commands, c => c.Syntax.Kind == CabNodeKind.EscapeCommand);
                Assert.Contains(result.Commands, c => c.Syntax.Kind == CabNodeKind.ImmediateCommand);
                Assert.Contains(result.Commands, c => c.Syntax.Kind == CabNodeKind.LabelCommand);
                Assert.NotEmpty(CabContentValidation.Bind(document, result.Commands).SelectMany(c => c.Syntax.Fields)); break;
            case "JS-1.5": Assert.Contains(result.Commands.SelectMany(c => c.Parameters), p => p.Rule.Parameter.Name == "path" && p.Value == "usbmem"); break;
            case "JS-1.6": Assert.Single(result.Commands, c => c.Syntax.Command == "T"); break;
            default: throw new InvalidOperationException("Unbekannter Kontextfall: " + id);
        }
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void Every_table_value_has_protocol_bytes_typed_validation_and_lossless_writing(string id, string parameter, string declared, string input, CabPrinterGeneration generation)
    {
        var rule = CabDefinitions.ParameterRule(id, parameter);
        Assert.True(rule.RecognizesDeclaredValue(declared));
        // This carrier tests an isolated parameter value, not the cross-parameter semantics
        // of an invented complete command. Complete-command contexts have separate fixtures.
        var carrier = File.ReadAllText(Fixture("parameter-probe.lbl")).Replace("__VALUE__", input, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(carrier);
        var document = CabParser.Parse(bytes);
        var value = Assert.Single(CabValidator.Validate(document).Commands, c => c.Syntax.Command == "T").Parameter("text")!;
        Assert.Equal(input, value.Value);
        Assert.Null(rule.Validate(value.Value, generation));
        Assert.Equal(bytes, document.Write());
        Assert.Equal(bytes, document.Write(CabParameterEditor.Replace(document, value, input)));
    }

    public static IEnumerable<object[]> Barcodes() => CabCatalog.Default.Entries.Where(e => e.Kind == "barcode").Select(e => new object[] { e.Id });

    [Theory]
    [MemberData(nameof(Barcodes))]
    public void Every_barcode_has_an_invalid_protocol_fixture_with_a_real_validation_error(string id)
    {
        var bytes = File.ReadAllBytes(Fixture(id + "-invalid.lbl"));
        var document = CabParser.Parse(bytes);
        Assert.Contains(CabValidator.Validate(document).Diagnostics, d => d.Severity == CabDiagnosticSeverity.Error && d.Code == "parameter-value");
        Assert.Equal(bytes, document.Write());
    }
}
