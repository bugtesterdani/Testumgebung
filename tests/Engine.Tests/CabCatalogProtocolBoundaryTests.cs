using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabCatalogProtocolBoundaryTests
{
    [Theory]
    [InlineData("line-endings")]
    [InlineData("units-jobs")]
    [InlineData("esc-middle")]
    [InlineData("downloads-three")]
    [InlineData("codepages")]
    [InlineData("encrypted")]
    [InlineData("comments")]
    public void Protocol_boundaries_are_validated_without_losing_bytes(string name)
    {
        var bytes = File.ReadAllBytes(CabJScriptCatalogCoverageTests.Fixture("edge-" + name + ".lbl"));
        var document = CabParser.Parse(bytes);
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join("; ", result.Diagnostics.Where(d => d.Severity == CabDiagnosticSeverity.Error).Select(d => d.Message)));
        Assert.Equal(bytes, document.Write());
        Assert.Equal(bytes, CabParser.Parse(document.Write()).Write());
        CheckMeaning(name, document, result);
    }

    private static void CheckMeaning(string name, CabDocument document, CabValidationResult result)
    {
        switch (name)
        {
            case "line-endings": Assert.Equal(2, result.Commands.Count(c => c.Syntax.Command == "J")); break;
            case "units-jobs": Assert.Equal(new[] { CabUnit.Inches, CabUnit.Millimetres }, result.Commands.Where(c => c.Syntax.Command == "T").Select(c => c.Syntax.Unit)); break;
            case "esc-middle": Assert.Equal("ä", Assert.Single(result.Commands, c => c.Syntax.Command == "T").Parameter("text")!.Value); break;
            case "downloads-three": Assert.Equal(3, result.Commands.Count(c => c.Syntax.Command == "d")); break;
            case "codepages": Assert.Equal(new[] { "Latinä", "漢字" }, result.Commands.Where(c => c.Syntax.Command == "T").Select(c => c.Parameter("text")!.Value)); break;
            case "encrypted": Assert.Contains(result.Diagnostics, d => d.Severity == CabDiagnosticSeverity.Information); break;
            case "comments":
                Assert.Single(result.Commands, c => c.Syntax.Command == "J");
                Assert.Contains(CabContentValidation.Bind(document, result.Commands).SelectMany(c => c.Syntax.Issues), i => i.Code == "content-literal"); break;
        }
    }

    [Theory]
    [InlineData("mixed-jobs.lbl")]
    [InlineData("opaque-blocks.lbl")]
    public void Existing_lossless_fixtures_are_retained_byte_for_byte(string file)
    {
        var retained = File.ReadAllBytes(CabJScriptCatalogCoverageTests.Fixture("retained-" + file));
        var original = File.ReadAllBytes(Path.Combine(CabJScriptCatalogCoverageTests.RepositoryRoot(), "tests/Fixtures/CabJScript", file));
        Assert.Equal(original, retained);
        Assert.Equal(retained, CabParser.Parse(retained).Write());
    }
}
