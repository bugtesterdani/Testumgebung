using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabMatrixRulesTests
{
    public static IEnumerable<object[]> CapacityRows()
    {
        foreach (var row in CabCatalog.Default["JS-4.2.12"].Metadata.GetProperty("kapazitaeten").EnumerateArray())
        {
            var dimensions = row.GetProperty("groesse").GetString()!.Split('x').Select(s => s.Trim()).ToArray();
            var type = "DATAMATRIX+ROWS" + dimensions[0] + "+COLS" + dimensions[1];
            yield return [type, "1", row.GetProperty("numerisch").GetInt32(), false];
            yield return [type, "A", row.GetProperty("alphanumerisch").GetInt32(), false];
        }
        foreach (var row in CabCatalog.Default["JS-4.2.42"].Metadata.GetProperty("kapazitaeten").EnumerateArray())
        {
            var version = row.GetProperty("version").GetInt32();
            var level = row.GetProperty("level").GetString();
            var type = "RMQR+VERSION" + version + "+EL" + level;
            var uncertain = version == 13 && level == "M";
            yield return [type, "1", row.GetProperty("numerisch").GetInt32(), uncertain];
            yield return [type, "A", row.GetProperty("alphanumerisch").GetInt32(), uncertain];
        }
    }

    [Theory]
    [MemberData(nameof(CapacityRows))]
    public void Every_reviewed_datamatrix_and_rmqr_size_has_a_capacity_boundary_test(string type, string character, int maximum, bool uncertain)
    {
        Assert.False(Validate(type, new string(character[0], maximum)).HasErrors);
        var diagnostics = Validate(type, new string(character[0], maximum + 1)).Diagnostics;
        Assert.Contains(diagnostics, d => d.Code == "matrix-capacity" && d.Severity == (uncertain ? CabDiagnosticSeverity.Warning : CabDiagnosticSeverity.Error));
    }

    private static CabValidationResult Validate(string type, string text)
        => CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\rB 0,0,0," + type + ",0.3;" + text + "\rA 1\r")));

    [Theory]
    [InlineData("RMQR", "m", 7)]
    [InlineData("RMQR", "h", 3)]
    [InlineData("GS1RMQR", "m", 7)]
    [InlineData("GS1RMQR", "h", 3)]
    public void Lowercase_error_levels_keep_capacity_boundaries_and_original_bytes(string type, string level, int maximum)
    {
        var prefix = type + "+VERSION0+EL" + level;
        var bytes = Encoding.UTF8.GetBytes("J\rB 0,0,0," + prefix + ",.3;" + new string('A', maximum) + "\rA 1\r");
        var document = CabParser.Parse(bytes);
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(level, Assert.Single(result.Commands, c => c.Syntax.Command == "B").Parameter("EL")!.Value);
        Assert.Equal(bytes, document.Write());
        Assert.Contains(Validate(prefix, new string('A', maximum + 1)).Diagnostics,
            d => d.Code == "matrix-capacity" && d.Severity == CabDiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("RMQR")]
    [InlineData("GS1RMQR")]
    public void Lowercase_m_preserves_the_version_13_capacity_ambiguity(string type)
    {
        var result = Validate(type + "+VERSION13+ELm", new string('1', 34));
        Assert.False(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == "matrix-capacity" && d.Severity == CabDiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData("RMQR+ELM+VERSION0", "M")]
    [InlineData("RMQR+ELH+VERSION0", "H")]
    [InlineData("MICROQR+ELL+VERSION2", "L")]
    [InlineData("MICROQR+ELM+VERSION3", "M")]
    [InlineData("MICROQR+ELQ+VERSION4", "Q")]
    public void Letter_error_levels_bind_as_values_in_actual_commands(string type, string level)
    {
        var result = Validate(type, "A");
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        var barcode = Assert.Single(result.Commands, c => c.Syntax.Command == "B");
        Assert.Equal(level, barcode.Parameter("EL")!.Value);
    }

    [Theory]
    [InlineData("DATAMATRIX+ROWS10+COLS10", "AAA", false)]
    [InlineData("DATAMATRIX+ROWS10+COLS10", "AAAA", true)]
    [InlineData("DATAMATRIX+ROWS10+COLS10", "123456", false)]
    [InlineData("DATAMATRIX+ROWS10+COLS10", "1234567", true)]
    [InlineData("DATAMATRIX+ROWS10+COLS10", "1234A", false)]
    [InlineData("RMQR+ELM+VERSION0", "AAAAAAA", false)]
    [InlineData("RMQR+ELM+VERSION0", "AAAAAAAA", true)]
    [InlineData("RMQR+ELH+VERSION0", "AAA", false)]
    [InlineData("RMQR+ELH+VERSION0", "AAAA", true)]
    [InlineData("MICROQR+VERSION2", "AAAAAA", false)]
    [InlineData("MICROQR+VERSION2", "AAAAAAA", true)]
    [InlineData("MICROQR+VERSION2", "lower", true)]
    [InlineData("MICROQR+VERSION1", "AAAAAAAAAA", false)]
    public void Fixed_matrix_capacity_uses_the_documented_table_and_automatic_micro_growth(string type, string value, bool error)
        => Assert.Equal(error, Validate(type, value).Diagnostics.Any(d => d.Code == "matrix-capacity" && d.Severity == CabDiagnosticSeverity.Error));

    [Fact]
    public void Unsupported_row_column_pairs_and_disputed_capacity_rows_remain_distinct()
    {
        Assert.Contains(Validate("DATAMATRIX+ROWS8+COLS10", "A").Diagnostics, d => d.Code == "matrix-size");
        Assert.DoesNotContain(Validate("DATAMATRIX+ROWS8+COLS18", "A").Diagnostics, d => d.Code == "matrix-size");
        var disputed = Validate("RMQR+VERSION13+ELM", new string('1', 34));
        Assert.Contains(disputed.Diagnostics, d => d.Code == "matrix-capacity" && d.Severity == CabDiagnosticSeverity.Warning);
        Assert.False(disputed.HasErrors);
        Assert.DoesNotContain(Validate("RMQR+VERSION9+ELM", new string('1', 100) + "a").Diagnostics, d => d.Code == "matrix-capacity");
        Assert.DoesNotContain(Validate("RMQR+VERSION0+ELM", "日本語").Diagnostics, d => d.Code == "matrix-capacity");
        Assert.DoesNotContain(Validate("CODE128", "[UNKNOWN]").Diagnostics, d => d.Code == "barcode-dynamic");
    }
}
