using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabValidationTests
{
    [Fact]
    public void Rfid_accepts_both_printed_power_separators_and_four_raw_password_bytes()
    {
        var bytes = "J\rE RFID;T:Auto,P:5E:10,S:"u8.ToArray()
            .Concat(new byte[] { 0xe4, 0xf6, 0xfc, 0xff }).Concat("\rA 1\r"u8.ToArray()).ToArray();
        var result = CabValidator.Validate(CabParser.Parse(bytes));
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        var command = Assert.Single(result.Commands, c => c.Definition.Catalog.Id == "JS-4.5.4");
        Assert.Equal("10", command.Parameter("power")!.Value);
        Assert.Equal("äöüÿ", command.Parameter("accesspwd")!.Value);
        Assert.Equal("5", command.Parameter("pos")!.Value);
    }

    [Theory]
    [InlineData("usbmem", false)]
    [InlineData("missing", true)]
    public void Download_names_exclude_the_storage_medium_from_the_eight_character_limit(string medium, bool invalidPath)
    {
        var result = Validate("d BMP;/" + medium + "/logo\r\u001b.\0\u001b.\rJ\rA 1\r");
        var download = Assert.Single(result.Commands, c => c.Syntax.Command == "d");
        Assert.Equal("logo", download.Parameter("name")!.Value);
        Assert.Equal(medium, download.Parameter("path")!.Value);
        Assert.Equal(invalidPath, result.Diagnostics.Any(d => d.Code == "path"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "parameter-value" && d.CatalogId == "JS-3.3");
    }

    [Fact]
    public void Parameter_edits_use_each_original_codepage_and_fail_before_writing_unrepresentable_text()
    {
        var bytes = "J\rT 0,0,0,3,3;a\u001bowindows-1252;"u8.ToArray()
            .Concat(new byte[] { 0xe4 }).Concat("\rA 1\r"u8.ToArray()).ToArray();
        var document = CabParser.Parse(bytes);
        var parameter = Assert.Single(CabValidator.Validate(document).Commands, c => c.Syntax.Command == "T").Parameter("text")!;
        Assert.Equal("aä", parameter.Value);
        var edited = document.Write(CabParameterEditor.Replace(document, parameter, "bö"));
        Assert.Contains((byte)0xf6, edited);
        Assert.Equal("bö", Assert.Single(CabValidator.Validate(CabParser.Parse(edited)).Commands, c => c.Syntax.Command == "T").Parameter("text")!.Value);
        Assert.Throws<EncoderFallbackException>(() => CabParameterEditor.Replace(document, parameter, "b漢"));
        Assert.Throws<ArgumentException>(() => CabParameterEditor.Replace(document, parameter with { Syntax = null }, "x"));
        Assert.Throws<ArgumentException>(() => CabParameterEditor.Replace(document, parameter with { Rule = parameter.Rule with { DataType = CabDataType.Binary } }, "x"));
        Assert.Equal(bytes, document.Write());
    }

    [Theory]
    [InlineData("ä", "ö")]
    [InlineData("ä", "longer text")]
    [InlineData("ä", "")]
    public void Parameter_edits_preserve_controls_even_inside_utf8_characters(string original, string replacement)
    {
        var encoded = Encoding.UTF8.GetBytes(original);
        var bytes = "J\rT 0,0,0,3,3;"u8.ToArray().Concat(encoded.Take(1))
            .Concat(new byte[] { 27, (byte)'?' }).Concat(encoded.Skip(1)).Concat("\rA 1\r"u8.ToArray()).ToArray();
        var document = CabParser.Parse(bytes);
        var command = Assert.Single(CabValidator.Validate(document).Commands, c => c.Syntax.Command == "T");
        var edited = document.Write(CabParameterEditor.Replace(document, command.Parameter("text")!, replacement));
        var result = CabValidator.Validate(CabParser.Parse(edited));
        Assert.Equal(replacement, Assert.Single(result.Commands, c => c.Syntax.Command == "T").Parameter("text")!.Value);
        Assert.Single(result.Commands, c => c.Syntax.Kind == CabNodeKind.EscapeCommand);
        Assert.Equal(bytes, document.Write());
    }

    [Theory]
    [InlineData("m m\rJ\rS l1;0,0,40,43,60\rT:Part;1,2,0,5,3;hello\rR Part;new\rA 2\r")]
    [InlineData("J\nS e;0,0,40,40,60\nG:Line;2,3,22.5;L:20,0.4,s,a,[D:2,1],[S:0,100,90]\nA 1\n")]
    [InlineData("J\rS l1;0,0,40,43,60\rB:Code;1,2,0,CODE128+MOD10+WS2,10,0.25;1234\rA 1\r")]
    [InlineData("J\rS e;0,0,40,40,60\rE OPCUA-2;opc.tcp://user:pass@localhost:4840\rT 0,2,0,3,pt12;abc\rA 10 [PREVIEW:ALL][SAVE]\r")]
    [InlineData("J\rS e;0,0,40,40,60\rE RFID;T:Auto,R:2,P:5,E:10,W:7,L:8,S:0x1234abcd\rH 100,0:0,P2,T:I,R0,S1,B50\rO M,R,T\rA 1\r")]
    [InlineData("J\rS e;0,0,40,40,60\rW:Rich;0,0,0,30,20,3,pt12;<HTML>\n<p>text</p>\n</HTML>\rA 1\r")]
    public void Complete_commands_bind_without_errors_and_leave_every_byte_unchanged(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var document = CabParser.Parse(bytes);
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ManualReference + ": " + d.Message)));
        Assert.Equal(bytes, document.Write());
        Assert.All(result.Diagnostics, d => { Assert.StartsWith("JS-", d.CatalogId); Assert.InRange(d.FirstPage, 1, 382); });
    }

    [Fact]
    public void Controls_inside_parameters_do_not_break_binding_or_split_utf8_characters()
    {
        var prefix = Encoding.ASCII.GetBytes("m \u001ba i\rJ\rS e;0,0,2,2,3\rT:Name;0.1,0.2,0,3,0.1;");
        var bytes = prefix.Concat(new byte[] { 0xc3, 0x1b, (byte)'?', 0xa4 }).Concat("\rA 1\r"u8.ToArray()).ToArray();
        var result = CabValidator.Validate(CabParser.Parse(bytes));
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        var text = Assert.Single(result.Commands, c => c.Syntax.Command == "T");
        Assert.Equal(CabUnit.Inches, text.Syntax.Unit);
        Assert.Equal("ä", text.Parameter("text")!.Value);
        Assert.Equal(2, text.Parameter("text")!.Parts.Count);
        Assert.Equal(2, result.Commands.Count(c => c.Syntax.Kind == CabNodeKind.EscapeCommand));
    }

    [Fact]
    public void Name_duplicates_and_forward_references_are_errors_but_different_jobs_have_separate_names()
    {
        var result = Validate("J\rT:Name;0,0,0,3,3;a\rT:Name;1,1,0,3,3;b\rR Later;x\rA 1\rJ\rT:Name;0,0,0,3,3;c\rA 1\r");
        Assert.Single(result.Diagnostics, d => d.Code == "duplicate-name");
        Assert.Single(result.Diagnostics, d => d.Code == "field-reference");
        Assert.Equal(new[] { 0, 1 }, result.Commands.Where(c => c.Syntax.Command == "J").Select(c => c.JobIndex));
        var comment = Validate("; comment\u001baJ\rJ\rT:Name;0,0,0,3,3;a\rA 1\r");
        Assert.Single(comment.Commands, c => c.Syntax.Command == "J");
    }

    [Fact]
    public void Generation_and_position_diagnostics_are_warnings_with_exact_manual_references()
    {
        var document = CabParser.Parse("m m\rJ\rS e;0,0,40,40,60\rD 5,0\rO M,R\rW 70,0,0,10,10,3,3;text\rA 1\r"u8);
        var settings = new CabDocumentSettings { JobGenerations = new() { [0] = CabPrinterGeneration.X2 } };
        var result = CabValidator.Validate(document, settings);
        Assert.Contains(result.Diagnostics, d => d.Code == "generation" && d.Severity == CabDiagnosticSeverity.Warning && d.CatalogId == "JS-4.17");
        Assert.Contains(result.Diagnostics, d => d.Code == "position" && d.Severity == CabDiagnosticSeverity.Warning && d.FirstPage == 233);
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Where(d => d.Severity == CabDiagnosticSeverity.Error).Select(d => d.Message)));
        var inside = Validate("J\rS e;0,0,40,40,60\rD 5,0\rT -2,0,0,3,3;a\rA 1\r");
        Assert.DoesNotContain(inside.Diagnostics, d => d.Code == "position");
    }

    [Theory]
    [InlineData("T 0,0,45,-1,x2,y2;text", "font-rotation")]
    [InlineData("T 0,0,0,-1,x2,y2,fr1;text", "font-effect")]
    [InlineData("T 0,0,0,3,3,o;text", "font-effect")]
    [InlineData("W 0,0,0,20,20,-1,3;text", "rich-font")]
    [InlineData("I 0,0,0,a;image", "autoload-scale")]
    [InlineData("B 0,0,0,EAN13,10,0.3;123", "barcode-length")]
    [InlineData("B 0,0,0,EAN13,10,0.3;ABCDEFGHIJKL", "barcode-characters")]
    [InlineData("B 0,0,0,QRCODE+MODEL1+VERSION2,0.3;text", "qr-model")]
    public void Command_combinations_and_barcode_properties_are_checked(string command, string code)
        => Assert.Contains(Validate("J\rS e;0,0,40,40,60\r" + command + "\rA 1\r").Diagnostics, d => d.Code == code);

    [Fact]
    public void Per_generation_object_limits_are_applied_without_interpreting_unknown_ax_limits_as_zero()
    {
        var source = "J\r" + string.Concat(Enumerable.Repeat("B 0,0,0,CODE128,10,0.3;123\r", 101)) + "A 1\r";
        var document = CabParser.Parse(Encoding.UTF8.GetBytes(source));
        var x2 = CabValidator.Validate(document, new() { DefaultGeneration = CabPrinterGeneration.X2 });
        Assert.Single(x2.Diagnostics, d => d.Code == "object-limit" && d.CatalogId == "JS-1.6");
        Assert.DoesNotContain(CabValidator.Validate(document).Diagnostics, d => d.Code == "object-limit");
        Assert.DoesNotContain(CabValidator.Validate(document, new() { DefaultGeneration = CabPrinterGeneration.Ax }).Diagnostics, d => d.Code == "object-limit");
    }

    [Fact]
    public void Arbitrary_source_bytes_produce_diagnostics_without_throwing_or_rewriting()
    {
        var random = new Random(257);
        for (var sample = 0; sample < 1000; sample++)
        {
            var bytes = new byte[random.Next(128)]; random.NextBytes(bytes);
            var document = CabParser.Parse(bytes);
            _ = CabValidator.Validate(document);
            Assert.Equal(bytes, document.Write());
        }
    }

    private static CabValidationResult Validate(string source) => CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes(source)));
}
