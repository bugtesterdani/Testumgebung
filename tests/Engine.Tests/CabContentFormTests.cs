using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabContentFormTests
{
    [Fact]
    public void Form_accepts_known_job_references_and_keeps_renamed_field_arguments_editable()
    {
        var form = new CabContentForm("JS-5.39", CabPrinterGeneration.X4, new HashSet<string>(["Current"], StringComparer.Ordinal));
        var operand = form.Properties.First(p => p.Value == "1");
        var changed = form.Change(operand, "Current");
        Assert.Equal("[+:Current,2,3]", changed.Text);
        var reference = new CabContentForm("JS-5.88", CabPrinterGeneration.X4);
        var renamed = reference.Change(Assert.Single(reference.Properties), "Current");
        Assert.Equal("[Current]", renamed.Text);
        Assert.Equal("Current", Assert.Single(renamed.Properties).Value);
    }

    [Theory]
    [MemberData(nameof(CabContentCorpusTests.Corpus), MemberType = typeof(CabContentCorpusTests))]
    public void Every_content_insertion_has_real_parameter_bindings_and_an_offline_preview(string id, string expected)
    {
        var entry = CabCatalog.Default[id];
        var generation = Enum.GetValues<CabPrinterGeneration>().First(g => entry.Availability(g) != CabAvailability.Unsupported
            && entry.Parameters.All(p => p.Availability(g, entry.Availability(g)) != CabAvailability.Unsupported));
        var form = new CabContentForm(id, generation);
        Assert.Equal(expected, form.Text);
        Assert.Equal(entry.Parameters.Select(p => p.Name).Order(), form.Properties.Select(p => p.Definition.Name).Distinct().Order());
        foreach (var property in form.Properties)
        {
            Assert.True(property.IsPresent);
            Assert.Equal(form.Editor.Document.Write(), form.Change(property, property.Value!).Editor.Document.Write());
        }
        Assert.NotNull(form.Preview(new()).Text);
    }
}
