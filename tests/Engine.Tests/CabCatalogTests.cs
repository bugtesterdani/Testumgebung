using System.Text.Json;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabCatalogTests
{
    [Fact]
    public void Every_non_content_parameter_has_an_explicit_typed_rule()
    {
        var failures = new List<string>();
        foreach (var entry in CabCatalog.Default.Entries.Where(e => e.Kind != "inhaltsfeld"))
        foreach (var parameter in entry.Parameters)
        {
            try { _ = CabDefinitions.ParameterRule(entry.Id, parameter.Name); }
            catch (InvalidOperationException error) { failures.Add(error.Message); }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        var definitions = CabDefinitions.Default;
        Assert.Equal(CabCatalog.Default.Entries.Count(e => e.Kind != "inhaltsfeld"), definitions.Commands.Count);
        foreach (var definition in definitions.Commands)
            Assert.Equal(definition.Catalog.Parameters.Select(p => p.Name), definition.Parameters.Select(p => p.Parameter.Name));
    }
    [Fact]
    public void Embedded_definitions_retain_every_reviewed_entry_parameter_value_and_metadata_field()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory.Parent is not null && !File.Exists(Path.Combine(directory.FullName, "Testumgebung.sln"))) directory = directory.Parent;
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "docs/cab/jscript-befehlskatalog.json")));
        var entries = source.RootElement.GetProperty("eintraege").EnumerateArray().ToArray();
        var catalog = CabCatalog.Default;
        Assert.Equal(entries.Length, catalog.Entries.Count);
        foreach (var entry in entries)
        {
            var actual = catalog[entry.GetProperty("id").GetString()!];
            Assert.True(JsonElement.DeepEquals(entry, actual.Metadata));
            var parameters = entry.GetProperty("parameter").EnumerateArray().ToArray();
            Assert.Equal(parameters.Length, actual.Parameters.Count);
            for (var p = 0; p < parameters.Length; p++)
            {
                Assert.True(JsonElement.DeepEquals(parameters[p], actual.Parameters[p].Metadata));
                var values = parameters[p].GetProperty("werte").EnumerateArray().ToArray();
                Assert.Equal(values.Length, actual.Parameters[p].Values.Count);
                for (var v = 0; v < values.Length; v++)
                    Assert.True(JsonElement.DeepEquals(values[v], actual.Parameters[p].Values[v].Metadata));
            }
        }
        Assert.True(catalog.TryGet("JS-4.16", out var text));
        Assert.Same(catalog["JS-4.16"], text);
        Assert.False(catalog.TryGet("not-present", out _));
    }

    [Fact]
    public void Availability_preserves_explicit_unknown_cells_and_inherits_only_unspecified_metadata()
    {
        var catalog = CabCatalog.Default;
        Assert.Equal(CabAvailability.Unsupported, catalog["JS-4.17"].Availability(CabPrinterGeneration.Ax));
        Assert.Equal(CabAvailability.Supported, catalog["JS-4.17"].Availability(CabPrinterGeneration.X4));
        Assert.Equal(CabAvailability.Unknown, catalog["JS-1.3"].Availability(CabPrinterGeneration.X4));
        var name = catalog["JS-4.16"].Parameters.Single(p => p.Name == "name");
        Assert.Equal(CabAvailability.Supported, name.Availability(CabPrinterGeneration.X4, CabAvailability.Supported));
        var font = catalog["JS-4.16"].Parameters.Single(p => p.Name == "font");
        Assert.Equal(CabAvailability.Unknown, font.Values.Single(v => v.Value == "596").Availability(CabPrinterGeneration.Ax, CabAvailability.Supported));
        Assert.Equal(CabAvailability.Unsupported, catalog["JS-4.5.4"].Parameters.Single(p => p.Name == "wpower")
            .Availability(CabPrinterGeneration.X2, CabAvailability.Supported));
        Assert.True(catalog["JS-2.5"].Parameters.Single().IsAnswer);
        Assert.False(name.IsAnswer);
    }
}
