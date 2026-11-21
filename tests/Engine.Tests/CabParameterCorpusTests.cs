using System.Text.Json;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabParameterCorpusTests
{
    [Fact]
    public void Every_non_content_parameter_has_a_valid_example_in_the_versioned_corpus()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory.Parent is not null && !File.Exists(Path.Combine(directory.FullName, "Testumgebung.sln"))) directory = directory.Parent;
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tests/Fixtures/CabJScript/parameter-values.json")));
        var expected = CabDefinitions.Default.Commands.SelectMany(c => c.Parameters.Select(p => c.Catalog.Id + "/" + p.Parameter.Name)).Order().ToArray();
        var actual = new List<string>();
        var errors = new List<string>();
        foreach (var row in corpus.RootElement.EnumerateArray())
        {
            var id = row.GetProperty("id").GetString()!;
            var name = row.GetProperty("parameter").GetString()!;
            var value = row.GetProperty("valid").GetString()!;
            actual.Add(id + "/" + name);
            var error = CabDefinitions.ParameterRule(id, name).Validate(value);
            if (error is not null) errors.Add(id + "/" + name + " = " + value + ": " + error);
            if (row.TryGetProperty("invalid", out var invalid)
                && CabDefinitions.ParameterRule(id, name).Validate(invalid.GetString()) is null)
                errors.Add(id + "/" + name + ": unzulässiger Beispielwert wurde akzeptiert: " + invalid.GetString());
        }
        Assert.Equal(expected, actual.Order());
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
