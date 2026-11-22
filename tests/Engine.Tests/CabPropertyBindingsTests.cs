using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabPropertyBindingsTests
{
    [Fact]
    public void Repairing_an_empty_first_substring_argument_preserves_its_field_name_and_following_argument()
    {
        const string source = "J\rT:Data;0,0,0,5,3;abc\rT 0,0,0,5,3;[Data,,1]\rA 1\r";
        var editor = new CabSourceEditor(Encoding.UTF8.GetBytes(source));
        var content = CabContentValidation.Bind(editor.Document, editor.Validation.Commands).Last();
        var property = Assert.Single(CabPropertyBindings.ForContent(editor, content, content.Syntax.Fields.Single()), p => p.Definition.Name == "m");
        Assert.Equal("", property.Value);
        var next = CabPropertyBindings.Replace(editor, property, "4");
        Assert.Equal(Encoding.UTF8.GetBytes(source.Replace("[Data,,1]", "[Data,4,1]", StringComparison.Ordinal)), next.Document.Write());
    }

    [Fact]
    public void Missing_answers_and_opaque_bytes_never_display_synthetic_input_examples()
    {
        var schema = CabPropertySchema.All(CabPrinterGeneration.X4);
        var answer = schema.First(d => d.Control == CabPropertyControl.Answer);
        Assert.Equal("Keine Druckerantwort gelesen", new CabBoundProperty(answer, null).EditorValue);
        var resource = schema.First(d => d.Control == CabPropertyControl.Resource);
        Assert.Contains("Dateidaten", new CabBoundProperty(resource, null).EditorValue);
        var syntax = new CabSyntaxNode(CabNodeKind.Parameter, new(0, 0), "data", "UTF-8", CabUnit.Millimetres, []);
        Assert.StartsWith("Binärdaten", new CabBoundProperty(resource, new(resource.Rule, syntax, null)).EditorValue);
        Assert.Equal("observed", new CabBoundProperty(answer, new(answer.Rule, syntax, "observed")).EditorValue);
        Assert.Contains("nicht", new CabBoundProperty(answer, new(answer.Rule, syntax, null)).EditorValue, StringComparison.OrdinalIgnoreCase);
        var name = CabPropertySchema.ForEntry("JS-4.16", CabPrinterGeneration.X4).Single(d => d.Name == "name");
        Assert.Equal(CabPropertyTemplates.Example("JS-4.16", "name"), new CabBoundProperty(name, null).EditorValue);
    }

    [Theory]
    [InlineData("[SER:1,,2]", "inc", "[SER:1,4,2]")]
    [InlineData("[SER:1,2,]", "freq", "[SER:1,2,4]")]
    [InlineData("[WEPC:,,1]", "start", "[WEPC:4,,1]")]
    [InlineData("[WEPC:,,1]", "len", "[WEPC:,4,1]")]
    public void Documented_empty_optional_content_arguments_remain_editable(string field, string name, string expected)
    {
        var text = "J\rT 0,0,0,5,3;" + field + "\rA 1\r";
        var bytes = Encoding.UTF8.GetBytes(text);
        var editor = new CabSourceEditor(bytes);
        var content = Assert.Single(CabContentValidation.Bind(editor.Document, editor.Validation.Commands));
        var property = Assert.Single(CabPropertyBindings.ForContent(editor, content, content.Syntax.Fields[0]), p => p.Definition.Name == name);
        Assert.Equal("", property.Value);
        Assert.Equal(bytes, CabPropertyBindings.Replace(editor, property, "").Document.Write());
        Assert.Equal(Encoding.UTF8.GetBytes(text.Replace(field, expected, StringComparison.Ordinal)), CabPropertyBindings.Replace(editor, property, "4").Document.Write());
    }

    [Theory]
    [MemberData(nameof(CabJScriptCatalogCoverageTests.Entries), MemberType = typeof(CabJScriptCatalogCoverageTests))]
    public void Every_protocol_template_exposes_actual_command_properties_without_changing_source(string id, string file, string mode, string definition)
    {
        var template = CabPropertyTemplates.Get(id);
        var bytes = File.ReadAllBytes(CabJScriptCatalogCoverageTests.Fixture(file));
        Assert.Equal(bytes, template.Bytes); Assert.Equal(mode, template.Mode); Assert.Equal(definition, template.DefinitionId);
        foreach (var generation in Enum.GetValues<CabPrinterGeneration>())
        {
            var editor = new CabSourceEditor(bytes, settings: new() { DefaultGeneration = generation });
            foreach (var command in editor.Validation.Commands)
                foreach (var property in CabPropertyBindings.ForCommand(editor, command))
                {
                    Assert.NotEmpty(property.Definition.Help);
                    if (!property.IsPresent || !property.IsTextEditable || property.Value is null || CabPropertyBindings.Validate(property, property.Value) is not null) continue;
                    Assert.Equal(bytes, CabPropertyBindings.Replace(editor, property, property.Value).Document.Write());
                }
        }
    }

    [Theory]
    [InlineData(CabPrinterGeneration.Ax)]
    [InlineData(CabPrinterGeneration.X2)]
    [InlineData(CabPrinterGeneration.X3)]
    [InlineData(CabPrinterGeneration.X4)]
    public void Every_catalog_parameter_has_a_typed_form_and_complete_choices(CabPrinterGeneration generation)
    {
        var forms = CabPropertySchema.All(generation); Assert.Equal(1416, forms.Count);
        foreach (var entry in CabCatalog.Default.Entries)
        {
            var rows = CabPropertySchema.ForEntry(entry.Id, generation);
            Assert.Equal(entry.Parameters.Select(p => p.Name), rows.Select(r => r.Name));
            foreach (var row in rows)
            {
                Assert.Contains(entry.Id, row.Help); Assert.Contains("Handbuch S.", row.Help);
                Assert.Equal(row.Rule.Parameter.Values.Select(v => v.Value), row.Choices.Select(v => v.Value));
                if (row.Rule.Parameter.IsAnswer) { Assert.Equal(CabPropertyControl.Answer, row.Control); Assert.False(row.IsEnabled); }
                if (row.Availability == CabAvailability.Unsupported) Assert.False(row.IsEnabled);
                _ = row.Control; _ = row.Unit; _ = row.Range;
                Assert.NotEmpty(row.ControlText); Assert.NotEmpty(row.AvailabilityText); Assert.DoesNotContain("CabNumericRange", row.RangeText);
            }
        }
    }

    [Theory]
    [MemberData(nameof(CabContentCorpusTests.Corpus), MemberType = typeof(CabContentCorpusTests))]
    public void Every_content_parameter_has_an_exact_editable_source_binding(string id, string field)
    {
        var bytes = Encoding.UTF8.GetBytes("J\rT:Data;0,0,0,5,3;abc\rT:Num;0,0,0,5,3;12\rT:List;0,0,0,5,3;a|b\rT:HexData;0,0,0,5,3;00010203\rT 0,0,0,5,3;" + field + "\rA 1\r");
        var entry = CabCatalog.Default[id];
        var generation = new[] { CabPrinterGeneration.X4, CabPrinterGeneration.X3, CabPrinterGeneration.X2, CabPrinterGeneration.Ax }
            .First(g => entry.Availability(g) != CabAvailability.Unsupported && entry.Parameters.All(p => p.Availability(g, entry.Availability(g)) != CabAvailability.Unsupported));
        var editor = new CabSourceEditor(bytes, settings: new() { DefaultGeneration = generation });
        var content = CabContentValidation.Bind(editor.Document, editor.Validation.Commands).Last();
        var syntax = Assert.Single(content.Syntax.Fields); Assert.Equal(id, syntax.Definition.Id);
        var properties = CabPropertyBindings.ForContent(editor, content, syntax);
        Assert.Equal(syntax.Definition.Parameters.Select(p => p.Name).Order(), properties.Select(p => p.Definition.Name).Distinct().Order());
        foreach (var property in properties)
        {
            Assert.True(property.IsPresent, id + "/" + property.Definition.Name);
            Assert.Equal(property.Value, CabContentSource.Read(editor.Document, property.Source!).Text);
            Assert.Equal(bytes, CabPropertyBindings.Replace(editor, property, property.Value!).Document.Write());
        }
    }

    [Fact]
    public void Content_parameter_edits_preserve_neighbor_bytes_and_intervening_controls()
    {
        var bytes = "J\rT 0,0,0,5,3;[SER:12\x1b?34][DAY]\rA 1\r"u8.ToArray();
        var editor = new CabSourceEditor(bytes); var content = CabContentValidation.Bind(editor.Document, editor.Validation.Commands).Last();
        var property = Assert.Single(CabPropertyBindings.ForContent(editor, content, content.Syntax.Fields[0]), p => p.Definition.Name == "x");
        var next = CabPropertyBindings.Replace(editor, property, "5678");
        Assert.Equal("J\rT 0,0,0,5,3;[SER:56\x1b?78][DAY]\rA 1\r"u8.ToArray(), next.Document.Write());
    }
}
