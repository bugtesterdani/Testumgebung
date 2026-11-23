using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Labels.CabJScript;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed partial class LabelPrintingTests
{
    [Theory]
    [InlineData("ESC.")]
    [InlineData("ESC:")]
    [InlineData("<BASE64>")]
    public async Task Syntax_bound_placeholders_leave_every_download_byte_unchanged(string framing)
    {
        var payload = new byte[] { 0, 255, 27 }.Concat("{Kennung}"u8.ToArray()).ToArray();
        var prefix = "d TMP;payload\r"u8.ToArray().Concat(CabResourceEditor.Frame(payload, framing)).Concat("\r"u8.ToArray()).ToArray();
        const string job = "J\rT:Data;1,8,0,5,3;{Kennung}\rB 1,12,0,CODE128,10,.3;ID-{Kennung}\rW 1,20,0,30,10,3,3;<HTML><b>{Kennung}</b></HTML>\rR Data;again-{Kennung}\rA 1\r";
        var bytes = prefix.Concat(Encoding.UTF8.GetBytes(job)).ToArray();
        var printed = await PrintCab(bytes, "SN42");
        Assert.Equal(prefix.Concat(Encoding.UTF8.GetBytes(job.Replace("{Kennung}", "SN42", StringComparison.Ordinal))), printed);
        Assert.Equal(payload, CabResourceEditor.Read(new CabSourceEditor(printed), Assert.Single(CabResourceEditor.Resources(new(printed)))));
    }

    [Fact]
    public async Task Replacement_does_not_move_inline_codepage_controls_or_reencode_unchanged_literal_bytes()
    {
        var before = "J\rT 1,8,0,5,3;A{Kennung}\u001bowindows-1252;"u8.ToArray();
        var bytes = before.Concat(new byte[] { 0xe4 }).Concat("\rA 1\r"u8.ToArray()).ToArray();
        var token = bytes.AsSpan().IndexOf("{Kennung}"u8);
        const string value = "Ä😀-longer";
        var expected = bytes[..token].Concat(Encoding.UTF8.GetBytes(value)).Concat(bytes[(token + "{Kennung}".Length)..]).ToArray();
        Assert.Equal(expected, await PrintCab(bytes, value));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99999")]
    [InlineData("legacy-value")]
    public async Task Legacy_text_offset_is_not_required_or_used_to_select_mutable_bytes(string offset)
    {
        var definition = WithCab(Program("Windows", "NEW"), Convert.ToBase64String("J\rT 1,8,0,5,3;{Kennung}\rA 1\r"u8));
        var step = (StepDefinition)definition.Find("label")!;
        step.Parameters[LabelPrintingBlocks.CabTextOffsetParameter] = offset;
        var clone = new ProjectSerializer().Clone(new TestProject(definition));
        var printer = new RecordingPrinter();
        await new TestRun(clone.Definition, new() { LabelPrinter = printer }).RunAsync().Within();
        Assert.Contains(";NEW\r", Encoding.UTF8.GetString(Assert.Single(printer.Requests).Data));
        Assert.Equal(offset, ((StepDefinition)clone.Definition.Find("label")!).Parameters[LabelPrintingBlocks.CabTextOffsetParameter]);
    }

    [Fact]
    public async Task Rich_text_values_are_html_encoded_without_changing_original_markup_or_native_fields()
    {
        const string job = "J\rW 1,2,0,40,10,3,3;<HTML><b>{Kennung}</b><br>[ISODATE]</HTML>\rA 1\r";
        var bytes = await PrintCab(Encoding.UTF8.GetBytes(job), "A<b>&");
        Assert.Equal(job.Replace("{Kennung}", "A&lt;b&gt;&amp;", StringComparison.Ordinal), Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("bad[DAY]")]
    [InlineData("bad\rA 99")]
    public async Task Inserted_printer_control_content_is_rejected_before_transport(string value)
    {
        var definition = WithCab(Program("Windows", value), Convert.ToBase64String("J\rT 1,8,0,5,3;{Kennung}\rA 1\r"u8));
        var printer = new RecordingPrinter();
        var run = new TestRun(definition, new() { LabelPrinter = printer });
        Assert.Equal(ResultRating.Error, (await run.RunAsync().Within()).Rating);
        Assert.Empty(printer.Requests);
    }

    [Fact]
    public async Task Stored_interface_codepage_is_used_without_changing_the_saved_document()
    {
        var bytes = Encoding.Latin1.GetBytes("J\rT 1,8,0,5,3;ä{Kennung}\rA 1\r");
        var definition = WithCab(Program("Windows", "ö"), Convert.ToBase64String(bytes));
        var step = (StepDefinition)definition.Find("label")!;
        step.Parameters[CabLabelLayout.Parameter] = new LabelLayout { CabSource = new() { SetupCodePage = "ISO-8859-1" } }.Save();
        BlockCatalog.Default.Rebuild(step);
        var original = step.Parameters.ToDictionary();
        var printer = new RecordingPrinter();
        await new TestRun(definition, new() { LabelPrinter = printer }).RunAsync().Within();
        Assert.Equal(Encoding.Latin1.GetBytes("J\rT 1,8,0,5,3;äö\rA 1\r"), Assert.Single(printer.Requests).Data);
        Assert.Equal(original.OrderBy(p => p.Key), step.Parameters.OrderBy(p => p.Key));
    }

    private static async Task<byte[]> PrintCab(byte[] bytes, string value)
    {
        var definition = WithCab(Program("Windows", value), Convert.ToBase64String(bytes));
        var printer = new RecordingPrinter();
        var run = new TestRun(definition, new() { LabelPrinter = printer });
        Assert.Equal(ResultRating.NResult, (await run.RunAsync().Within()).Rating);
        return Assert.Single(printer.Requests).Data;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undecodable_text_is_preserved_but_cannot_silently_hide_a_placeholder(bool placeholder)
    {
        var bytes = "J\rT 1,8,0,5,3;"u8.ToArray().Concat(new byte[] { 255 })
            .Concat(Encoding.UTF8.GetBytes(placeholder ? "{Kennung}\rA 1\r" : "literal\rA 1\r")).ToArray();
        var definition = WithCab(Program("Windows", "REPLACED"), Convert.ToBase64String(bytes));
        Assert.Equal(placeholder, CabProjectDiagnostics.Check(definition).Any(i => i.IsError));
        var printer = new RecordingPrinter();
        var result = await new TestRun(definition, new() { LabelPrinter = printer }).RunAsync().Within();
        Assert.Equal(placeholder ? ResultRating.Error : ResultRating.NResult, result.Rating);
        if (placeholder) Assert.Empty(printer.Requests);
        else Assert.Equal(bytes, Assert.Single(printer.Requests).Data);
    }

    [Theory]
    [InlineData("ESC.")]
    [InlineData("ESC:")]
    [InlineData("<BASE64>")]
    public async Task Readable_binary_downloads_are_not_validated_as_fields_or_interpolated(string framing)
    {
        var payload = "[THIS_IS_NOT_A_FIELD]{Kennung}"u8.ToArray();
        var bytes = "d TMP;opaque\r"u8.ToArray().Concat(CabResourceEditor.Frame(payload, framing)).Concat("\rJ\rA 1\r"u8.ToArray()).ToArray();
        var stored = new CabStoredLabel(bytes);
        Assert.False(stored.Validation.HasErrors, string.Join("; ", stored.Validation.Diagnostics.Select(d => d.Message)));
        Assert.Empty(CabContentValidation.Bind(stored.Document, stored.Validation.Commands));
        Assert.Equal(bytes, await PrintCab(bytes, "NEVER-IN-BINARY"));
    }
}
