using System.Text;
using System.Text.Json;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabContentCorpusTests
{
    public static IEnumerable<object[]> Corpus()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory.Parent is not null && !File.Exists(Path.Combine(directory.FullName, "Testumgebung.sln"))) directory = directory.Parent;
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tests/Fixtures/CabJScript/content-fields.json")));
        foreach (var row in corpus.RootElement.EnumerateArray()) yield return [row.GetProperty("id").GetString()!, row.GetProperty("source").GetString()!];
    }

    private static readonly HashSet<string> Names = new(["Data", "Num", "List", "HexData"], StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Every_catalog_content_field_is_recognized_bound_and_validated(string id, string source)
    {
        var syntax = CabContentParser.Parse(source, Names);
        Assert.Empty(syntax.Issues);
        var field = Assert.Single(syntax.Fields);
        Assert.Equal(id, field.Definition.Id);
        Assert.Empty(CabContentGrammar.Errors(field, Names));
        var expected = field.Definition.Parameters.Select(p => p.Name).Order().ToArray();
        Assert.Equal(expected, CabContentArguments.Bind(field).Select(p => p.Parameter.Name).Distinct().Order());
        foreach (var parameter in field.Definition.Parameters)
            Assert.Equal(parameter, CabDefinitions.ParameterRule(id, parameter.Name).Parameter);
    }

    [Fact]
    public void Corpus_matches_all_107_entries_without_missing_or_extra_rows()
        => Assert.Equal(CabContentParser.Definitions.Select(e => e.Id).Order(), Corpus().Select(r => (string)r[0]).Order());

    [Theory]
    [InlineData("[SQL:SELECT a,b FROM source]", "SQL")]
    [InlineData("[OPCUA-2:ns=2;s=Node,Arg;Value]", "OPCUA-2")]
    [InlineData("[WEPC]", "WEPC")]
    [InlineData("[WACP]", "WACP")]
    [InlineData("[WKLP]", "WKLP")]
    [InlineData("[?:Pick]", "?")]
    public void Hardware_fields_remain_explicit_placeholders_with_intact_tail(string source, string name)
    {
        var result = CabContentPreview.Evaluate(source, new());
        Assert.Equal(name, Assert.Single(result.Syntax.Fields).Name);
        Assert.True(result.Value.Placeholder);
        Assert.Contains(result.Issues, i => i.Code == "preview-hardware");
    }

    [Fact]
    public void Previous_names_are_scoped_per_job_and_replacement_fields_are_evaluated()
    {
        var source = "J\rT:Data;0,0,0,3,3;abc[I]\rT 0,0,0,3,3;[Data]\rR Data;xyz\rT 0,0,0,3,3;[Data]\rJ\rT 0,0,0,3,3;[Data]\rA 1\r";
        var document = CabParser.Parse(Encoding.UTF8.GetBytes(source));
        var rows = CabDocumentPreview.Evaluate(document, new());
        Assert.Equal(new[] { "abc", "abc", "xyz", "xyz", "[Data]" }, rows.Select(r => r.Preview.Text));
        Assert.True(rows[0].Preview.Hidden);
        Assert.Equal(source, Encoding.UTF8.GetString(document.Write()));
    }

    [Theory]
    [InlineData("[H24:unknown]")]
    [InlineData("[RTAG:-1,4]")]
    [InlineData("[WEPC:3,2,1]")]
    [InlineData("[WACP:5]")]
    [InlineData("[?:Pick,seed,1,L201]")]
    [InlineData("[?:Pick,seed,1,M9]")]
    [InlineData("[S:UNKNOWN]")]
    [InlineData("[J:z20]")]
    [InlineData("[U:SUB]")]
    [InlineData("[BITFIELD:3,2:8,1]")]
    public void Parameter_diagnostics_include_exact_catalog_page_and_source_bytes(string field)
    {
        var document = CabParser.Parse(Encoding.UTF8.GetBytes("J\rT 0,0,0,3,3;ä" + field + "\rA 1\r"));
        var result = CabValidator.Validate(document);
        var issue = Assert.Single(result.Diagnostics.Where(d => d.Code == "content-parameter").Take(1));
        Assert.Equal(field, Encoding.UTF8.GetString(document.Bytes(issue.Range)));
        Assert.StartsWith("JS-5.", issue.CatalogId); Assert.InRange(issue.FirstPage, 242, 358);
    }

    [Fact]
    public void Shift_jis_trail_byte_is_not_a_content_bracket()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var source = "J\rT 0,0,0,3,3;ゼDATE] [DAY]\rA 1\r";
        Assert.Contains((byte)0x5b, encoding.GetBytes("ゼ"));
        var document = CabParser.Parse(encoding.GetBytes(source), new("932"));
        var content = Assert.Single(CabContentValidation.Bind(document, CabValidator.Validate(document).Commands));
        Assert.Equal("DAY", Assert.Single(content.Syntax.Fields).Name);
        Assert.Equal(source, encoding.GetString(document.Write()));
    }

    [Fact]
    public void Graphic_names_do_not_become_text_field_references()
    {
        var document = CabParser.Parse("J\rG:Shape;0,0,0;R:10,10,.2,.2\rT 0,0,0,3,3;[Shape]\rA 1\r"u8);
        var text = CabContentValidation.Bind(document, CabValidator.Validate(document).Commands).Last();
        Assert.Empty(text.Syntax.Fields);
        Assert.Contains(text.Syntax.Issues, i => i.Code == "content-literal");
    }

    [Fact]
    public void Hashing_uses_the_interface_codepage_at_the_function_after_an_escape_switch()
    {
        var bytes = Encoding.UTF8.GetBytes("J\rT 0,0,0,3,3;ä\u001boISO-8859-1;")
            .Concat(Encoding.Latin1.GetBytes("[SHA256:ä]\rA 1\r")).ToArray();
        var row = Assert.Single(CabDocumentPreview.Evaluate(CabParser.Parse(bytes), new()));
        Assert.Equal("ä5E1EFFE9B7BAB73DCE628CCD9F0CBBB16C1E6EFC6C4F311E59992A467BC119FD", row.Preview.Text);
        Assert.Equal(bytes, CabParser.Parse(bytes).Write());
    }
}
