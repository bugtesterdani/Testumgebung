using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabSemanticCasesTests
{
    [Fact]
    public void Composite_data_does_not_count_toward_the_linear_expanded_capacity()
    {
        CabValidationResult Result(string value, string type = "RSSEXPANDED") => CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\rB 0,0,0," + type + ",10,0.3;" + value + "\rA 1\r")));
        Assert.DoesNotContain(Result(new string('1', 74) + "|additional data").Diagnostics, d => d.Code == "barcode-length");
        Assert.Contains(Result(new string('1', 75)).Diagnostics, d => d.Code == "barcode-length");
        Assert.Contains(Result(new string('A', 42)).Diagnostics, d => d.Code == "barcode-length");
        Assert.Contains(Result(new string('A', 2726), "CODABLOCKF").Diagnostics, d => d.Code == "barcode-length");
        Assert.DoesNotContain(Result("01234567890128", "RSS14").Diagnostics, d => d.Code == "barcode-checksum");
        Assert.DoesNotContain(Result("[unknown literal]", "CODE128").Diagnostics, d => d.Code == "barcode-dynamic");
    }

    [Theory]
    [InlineData("T 0,0,0,3,3;text", 500, 2000)]
    [InlineData("B 0,0,0,CODE128,10,0.3;123", 100, 500)]
    [InlineData("G 0,0,0;L:10,0.3,s,a", 500, 2000)]
    [InlineData("I 0,0,0;logo", 200, 500)]
    [InlineData("F 100;custom", 128, 256)]
    public void Object_limits_apply_to_each_documented_object_family(string line, int x2Limit, int x4Limit)
    {
        var bytes = Encoding.UTF8.GetBytes("J\r" + string.Concat(Enumerable.Repeat(line + "\r", x2Limit + 1)) + "A 1\r");
        var document = CabParser.Parse(bytes);
        Assert.Single(CabValidator.Validate(document, new() { DefaultGeneration = CabPrinterGeneration.X2 }).Diagnostics, d => d.Code == "object-limit");
        Assert.DoesNotContain(CabValidator.Validate(document).Diagnostics, d => d.Code == "object-limit");
        var x4Document = CabParser.Parse(Encoding.UTF8.GetBytes("J\r" + string.Concat(Enumerable.Repeat(line + "\r", x4Limit + 1)) + "A 1\r"));
        Assert.Single(CabValidator.Validate(x4Document).Diagnostics, d => d.Code == "object-limit");
    }

    [Fact]
    public void Downloads_before_the_first_job_use_each_jobs_selected_generation_for_object_limits()
    {
        var source = string.Concat(Enumerable.Range(0, 257).Select(i => "d BMP;img" + i + "\r\u001b.\0\u001b.\r")) + "J\rA 1\rJ\rA 1\r";
        var document = CabParser.Parse(Encoding.UTF8.GetBytes(source));
        var result = CabValidator.Validate(document, new() { JobGenerations = new() { [1] = CabPrinterGeneration.X2 } });
        Assert.Single(result.Diagnostics, d => d.Code == "object-limit");
        Assert.DoesNotContain(CabValidator.Validate(document).Diagnostics, d => d.Code == "object-limit");
    }

    [Theory]
    [InlineData("EAN13", 12, 13)]
    [InlineData("EAN8", 7, 8)]
    [InlineData("UPCA", 11, 12)]
    [InlineData("UPCE", 7, 8)]
    [InlineData("ADDON2", 2, 3)]
    [InlineData("ADDON5", 5, 6)]
    public void Fixed_barcode_lengths_validate_input_before_printer_generated_check_digits(string type, int valid, int invalid)
    {
        CabValidationResult Result(int count) => CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\rB 0,0,0," + type + ",10,0.3;" + new string('1', count) + "\rA 1\r")));
        Assert.DoesNotContain(Result(valid).Diagnostics, d => d.Code == "barcode-length");
        Assert.Contains(Result(invalid).Diagnostics, d => d.Code == "barcode-length");
    }

    [Theory]
    [InlineData("DATAMATRIX", 3116, "1")]
    [InlineData("DATAMATRIX", 2335, "A")]
    [InlineData("QRCODE", 2000, "A")]
    [InlineData("PDF417", 2000, "A")]
    [InlineData("MICROPDF417", 366, "1")]
    [InlineData("MICROPDF417", 250, "A")]
    public void Global_barcode_capacity_boundaries_are_enforced(string type, int maximum, string character)
    {
        CabValidationResult Result(int count) => CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\rB 0,0,0," + type + ",0.3;" + new string(character[0], count) + "\rA 1\r")));
        Assert.DoesNotContain(Result(maximum).Diagnostics, d => d.Code == "barcode-capacity");
        Assert.Contains(Result(maximum + 1).Diagnostics, d => d.Code == "barcode-capacity");
    }

    [Theory]
    [InlineData("CODABAR", "A12B", "a12b")]
    [InlineData("FIM", "A", "E")]
    [InlineData("HIBC", "ABC-123", "lower")]
    [InlineData("PLESSEY", "ABCD09", "G")]
    [InlineData("POSTNET", "123456789", "ABC")]
    [InlineData("CODE39FULL", "lower", "ä")]
    [InlineData("MICROPDF417", "ASCII", "ä")]
    [InlineData("MICROQR", "ASCII", "ä")]
    public void Symbology_character_sets_distinguish_valid_data_from_invalid_data(string type, string valid, string invalid)
    {
        CabValidationResult Result(string value) => CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\rB 0,0,0," + type + ",10,0.3;" + value + "\rA 1\r")));
        Assert.DoesNotContain(Result(valid).Diagnostics, d => d.Code is "barcode-characters" or "barcode-ascii");
        Assert.Contains(Result(invalid).Diagnostics, d => d.Code is "barcode-characters" or "barcode-ascii");
    }

    [Theory]
    [InlineData("s 261121123000", "JS-3.13")]
    [InlineData("q b;sample", "JS-3.11")]
    [InlineData("E DBF;/usbmem/sample", "JS-4.5.1")]
    [InlineData("E LOG;sample,C:UTF-8", "JS-4.5.2")]
    [InlineData("E SQL;127.0.0.1:3306", "JS-4.5.5")]
    [InlineData("E SQLITE;/usbmem/sample.db", "JS-4.5.6")]
    [InlineData("E TMP;sample", "JS-4.5.7")]
    [InlineData("Mc usbmem", "JS-4.11.1")]
    [InlineData("Md LBL;/usbmem/sample", "JS-4.11.2")]
    [InlineData("M f;sample", "JS-4.11.3")]
    [InlineData("M l LBL;sample", "JS-4.11.4")]
    [InlineData("M r", "JS-4.11.5")]
    [InlineData("M s LBL;sample", "JS-4.11.6")]
    [InlineData("M u LBL;sample", "JS-4.11.7")]
    [InlineData("<ABC>\rprint 1\r</ABC>", "JS-6.2")]
    [InlineData("<ENCRYPTED JOB>\runknown\r</ENCRYPTED JOB>", "JS-6.4")]
    [InlineData("<ENCRYPTED LABEL: 123456789012>\ropaque", "JS-6.3")]
    [InlineData("d BMP;logo[SAVE] [B:-20]\r\u001b.\0\u001b.", "JS-3.3")]
    [InlineData("J\rI:Image;0,0,0,1,1,GOODBAD1:2:3;logo\rA 1", "JS-4.9")]
    [InlineData("J\rB 0,0,0,CODE128+GOODBAD1:2:3+VERIFY+EXTERN2:3:4,10,0.3;123\rA 1", "JS-4.2.11")]
    public void Synthetic_command_fixtures_bind_the_documented_family_without_errors(string source, string id)
    {
        var document = CabParser.Parse(Encoding.UTF8.GetBytes(source + "\r"));
        var result = CabValidator.Validate(document);
        Assert.Contains(result.Commands, c => c.Definition.Catalog.Id == id);
        Assert.False(result.HasErrors, string.Join("; ", result.Diagnostics.Select(d => d.ManualReference + ": " + d.Message)));
        Assert.Equal(Encoding.UTF8.GetBytes(source + "\r"), document.Write());
    }

    [Theory]
    [InlineData("B 0,0,0,CODE39,10,0.3;lower", "barcode-characters")]
    [InlineData("B 0,0,0,CODE128,10,0.3;ä", "barcode-ascii")]
    [InlineData("B 0,0,0,RSS14,10,0.3;01234567890129", "barcode-checksum")]
    [InlineData("B 0,0,0,RSSLIMITED,10,0.3;21234567890128", "barcode-limited")]
    [InlineData("B 0,0,0,UPCE0,10,0.3;1234567", "barcode-length")]
    [InlineData("O A0=1001", "applicator-time")]
    [InlineData("O A0=9999999999999999999999999999999999", "applicator-time")]
    [InlineData("E RFID;T:Auto,V,P,N,bad", "rfid-option")]
    [InlineData("J\rT 0,0,0,3,x2,y2;text", "font-size")]
    [InlineData("J\rT 0,0,45.5,3,3;text", "font-rotation")]
    [InlineData("J\rT 0,0,0,-4,x2,y2,o;text", "ocr-effect")]
    public void Invalid_combinations_produce_the_specific_diagnostic(string source, string code)
        => Assert.Contains(CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes(source + "\r"))).Diagnostics, d => d.Code == code);

    [Theory]
    [InlineData("{Sample}")]
    [InlineData("[DATE]")]
    public void Dynamic_barcode_data_is_deferred_to_content_evaluation(string value)
    {
        var result = CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\rB 0,0,0,EAN13,10,0.3;" + value + "\rA 1\r")));
        Assert.Contains(result.Diagnostics, d => d.Code == "barcode-dynamic");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "barcode-characters");
    }
}
