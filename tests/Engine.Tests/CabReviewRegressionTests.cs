using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabReviewRegressionTests
{
    public static IEnumerable<object[]> BuiltinAmounts()
    {
        foreach (var generation in Enum.GetValues<CabPrinterGeneration>())
        foreach (var option in new[] { "NO", "NOPRINT", "R", "REPEAT" })
            yield return [generation, option];
    }

    [Theory]
    [MemberData(nameof(BuiltinAmounts))]
    public void Builtin_amount_options_do_not_inherit_variable_generation_restrictions(CabPrinterGeneration generation, string option)
    {
        var source = "J\rA [" + option + "] \t\r";
        var document = CabParser.Parse(Encoding.UTF8.GetBytes(source));
        var result = CabValidator.Validate(document, new() { DefaultGeneration = generation });
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "generation");
        Assert.Null(Assert.Single(result.Commands, c => c.Syntax.Command == "A").Parameter("VAR"));
        Assert.Equal(source, Encoding.UTF8.GetString(document.Write()));
    }

    [Theory]
    [InlineData(CabPrinterGeneration.Ax)]
    [InlineData(CabPrinterGeneration.X2)]
    [InlineData(CabPrinterGeneration.X3)]
    public void Actual_amount_field_variables_still_warn_outside_x4(CabPrinterGeneration generation)
    {
        var result = CabValidator.Validate(CabParser.Parse("J\rT:Counter;0,0,0,3,3;1\rA [Counter]\r"u8), new() { DefaultGeneration = generation });
        Assert.Contains(result.Diagnostics, d => d.Code == "generation" && d.Message.Contains("[<VAR>]", StringComparison.Ordinal));
    }

    [Fact]
    public void Bare_preview_is_a_builtin_even_when_it_requires_a_newer_generation()
    {
        var result = CabValidator.Validate(CabParser.Parse("J\rA 1 [PREVIEW]\r"u8), new() { DefaultGeneration = CabPrinterGeneration.X2 });
        Assert.Contains(result.Diagnostics, d => d.Code == "generation");
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("[<VAR>]", StringComparison.Ordinal));
        Assert.Null(Assert.Single(result.Commands, c => c.Syntax.Command == "A").Parameter("VAR"));
    }

    [Theory]
    [InlineData("[Pattern]")]
    [InlineData("[DATE]")]
    [InlineData("{Pattern}")]
    public void Dynamic_fim_content_defers_both_the_choice_and_character_checks(string content)
    {
        var source = "J\rT:Pattern;0,0,0,3,3;A\rB 0,0,0,FIM,10,0.3,2;" + content + "\rA 1\r";
        var result = CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes(source)));
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains(result.Diagnostics, d => d.Code == "barcode-dynamic");
    }

    [Theory]
    [InlineData("A", false)]
    [InlineData("D", false)]
    [InlineData("E", true)]
    [InlineData("[Unknown]", true)]
    public void Literal_fim_content_still_uses_its_closed_choice(string content, bool error)
        => Assert.Equal(error, CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\rB 0,0,0,FIM,10,0.3,2;" + content + "\rA 1\r"))).HasErrors);

    [Fact]
    public void Print_option_padding_is_not_part_of_the_bound_flag_or_its_edit()
    {
        const string source = "J\rO M,\t R \t\rA 1\r";
        var document = CabParser.Parse(Encoding.UTF8.GetBytes(source));
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        var options = Assert.Single(result.Commands, c => c.Syntax.Command == "O");
        Assert.Equal("M", options.Parameter("M")!.Value);
        var rotate = options.Parameter("R")!;
        Assert.Equal("R", document.Decode(rotate.Syntax!));
        var edited = document.Write(CabParameterEditor.Replace(document, rotate, "U"));
        Assert.Equal(source.Replace(" R ", " U ", StringComparison.Ordinal), Encoding.UTF8.GetString(edited));
        Assert.False(CabValidator.Validate(CabParser.Parse(edited)).HasErrors);
        Assert.Equal(source, Encoding.UTF8.GetString(document.Write()));
    }

    [Fact]
    public void Head_option_and_pair_padding_is_accepted_without_rewriting()
    {
        const string source = "J\rH 100, 0 : 1 , P2 , T : I , R0 , S1 , B50\rA 1\r";
        var document = CabParser.Parse(Encoding.UTF8.GetBytes(source));
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(source, Encoding.UTF8.GetString(document.Write()));
    }

    [Theory]
    [InlineData("QRCODE+MODEL1+VERSION2", "[DATE]", "qr-model")]
    [InlineData("DATAMATRIX+ROWS8+COLS10", "[DATE]", "matrix-size")]
    [InlineData("QRCODE+MODEL1+VERSION2", "{Sample}", "qr-model")]
    public void Dynamic_content_does_not_skip_content_independent_barcode_rules(string type, string content, string code)
    {
        var result = CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\rB 0,0,0," + type + ",0.3;" + content + "\rA 1\r")));
        Assert.Contains(result.Diagnostics, d => d.Code == code && d.Severity == CabDiagnosticSeverity.Error);
        Assert.Contains(result.Diagnostics, d => d.Code == "barcode-dynamic");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "barcode-characters" or "barcode-length" or "matrix-capacity");
    }

    [Theory]
    [InlineData("QRCODE+MODEL2+VERSION2")]
    [InlineData("DATAMATRIX+ROWS8+COLS18")]
    public void Valid_static_barcode_options_remain_valid_with_dynamic_content(string type)
    {
        var result = CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\rB 0,0,0," + type + ",0.3;[DATE]\rA 1\r")));
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
    }

    [Theory]
    [InlineData("SC1", "size", "SC1")]
    [InlineData("SC1,n", "size", "SC1")]
    [InlineData("SC1,16,0.35", "size", "SC1")]
    [InlineData("16,0.35", "height", "16")]
    public void Ean_standard_sizes_and_individual_dimensions_are_alternative_forms(string dimensions, string parameter, string expected)
    {
        var result = CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\rB 0,0,0,EAN13," + dimensions + ";123456789012\rA 1\r")));
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        var barcode = Assert.Single(result.Commands, c => c.Syntax.Command == "B");
        Assert.Equal(expected, barcode.Parameter(parameter)!.Value);
    }

    [Theory]
    [InlineData("[Digits]")]
    [InlineData("[Digits,1,12]")]
    public void Barcode_content_for_previously_defined_fields_is_deferred(string reference)
    {
        var source = "J\rT:Digits;0,0,0,3,3;123456789012\rB 0,0,0,EAN13,10,0.3;" + reference + "\rA 1\r";
        var document = CabParser.Parse(Encoding.UTF8.GetBytes(source));
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains(result.Diagnostics, d => d.Code == "barcode-dynamic");
        Assert.Equal(source, Encoding.UTF8.GetString(document.Write()));
    }

    [Theory]
    [InlineData("J\rB 0,0,0,EAN13,10,0.3;[Digits]\rT:Digits;0,0,0,3,3;123456789012\rA 1\r")]
    [InlineData("J\rT:Digits;0,0,0,3,3;123456789012\rA 1\rJ\rB 0,0,0,EAN13,10,0.3;[Digits]\rA 1\r")]
    public void Undeclared_names_and_names_from_other_jobs_do_not_hide_literal_barcode_errors(string source)
    {
        var result = CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes(source)));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "barcode-dynamic");
        Assert.Contains(result.Diagnostics, d => d.Code == "barcode-characters");
    }

    [Theory]
    [InlineData("T 0,0,0,3;text")]
    [InlineData("W 0,0,0,20,20,3;text")]
    public void Selected_vector_or_rich_text_syntax_requires_its_size(string command)
    {
        var result = CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\r" + command + "\rA 1\r")));
        var field = Assert.Single(result.Commands, c => c.Syntax.Command is "T" or "W");
        var size = field.Parameter("size");
        Assert.NotNull(size);
        Assert.True(size.Rule.Required);
        Assert.False(size.IsPresent);
        Assert.Contains(result.Diagnostics, d => d.Code == "parameter-value" && d.Message.Contains("size", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("T 0,0,0,3,pt12;text")]
    [InlineData("T 0,0,0,-1,x2,y2;text")]
    public void Valid_vector_and_bitmap_size_alternatives_remain_valid(string command)
        => Assert.False(CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\r" + command + "\rA 1\r"))).HasErrors);

    [Theory]
    [InlineData("e BMP;logo\r", "JS-3.4", "name", "logo")]
    [InlineData("F 100,custom\r", "JS-4.6", "name", "custom")]
    [InlineData("F 100;custom\r", "JS-4.6", "name", "custom")]
    public void Documented_erase_and_font_alias_forms_bind_both_parameters(string source, string id, string name, string value)
    {
        var document = CabParser.Parse(Encoding.UTF8.GetBytes(source));
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        var command = Assert.Single(result.Commands, c => c.Definition.Catalog.Id == id);
        Assert.Equal(value, command.Parameter(name)!.Value);
        Assert.Equal(source, Encoding.UTF8.GetString(document.Write()));
    }

    [Theory]
    [InlineData("t\r", "param")]
    [InlineData("t \r", "param")]
    [InlineData("P\r", "disp")]
    [InlineData("P \r", "disp")]
    public void Missing_optional_arguments_remain_absent(string source, string parameter)
    {
        var result = CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes(source)));
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        Assert.Null(Assert.Single(result.Commands).Parameter(parameter));
    }

    [Theory]
    [InlineData("e BMP\r")]
    [InlineData("F 100\r")]
    [InlineData("F bad,custom\r")]
    [InlineData("t invalid\r")]
    [InlineData("P invalid\r")]
    public void Missing_required_or_invalid_explicit_arguments_still_fail(string source)
        => Assert.True(CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes(source))).HasErrors);

    [Theory]
    [InlineData("C 1,39.999", false)]
    [InlineData("C 1,40", true)]
    [InlineData("m i\rC 1,1", false)]
    [InlineData("m i\rC 1,2", true)]
    public void Cut_displacement_is_strictly_smaller_than_label_height_in_the_active_units(string command, bool error)
    {
        var document = CabParser.Parse(Encoding.UTF8.GetBytes("m m\rJ\rS e;0,0,40,40,60\r" + command + "\rA 1\r"));
        Assert.Equal(error, CabValidator.Validate(document).Diagnostics.Any(d => d.Code == "cut-displacement"));
    }
}
