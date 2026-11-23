using System.Net;
using System.Net.Sockets;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed partial class LabelPrintingTests
{
    private const string Template = "^XA\n^CI28\n^FD{Kennung} {{Los}}^FS\n^XZ";
    private const string Expected = "^XA\n^CI28\n^FDPrüfling-42 {Los}^FS\n^XZ";
    private static readonly byte[] CabBytes =
    [
        .. "m m\r\nJ\r\n"u8,
        0x1B, 0x00, 0x7B, 0xFF, 0x80, 0x7D,
        .. "\r\nA 1\r\n"u8,
    ];

    private static TestDefinition WithCab(TestDefinition definition, string? encoded = null)
    {
        var step = (StepDefinition)definition.Find("label")!;
        step.Parameters[LabelPrintingBlocks.FormatParameter] = LabelPrintingBlocks.Cab;
        step.Parameters[LabelPrintingBlocks.CabDataParameter] = encoded ?? Convert.ToBase64String(CabBytes);
        step.Parameters["ZplVorlage"] = "";
        BlockCatalog.Default.Rebuild(step);
        return definition;
    }

    private static TestDefinition Program(string mode, string id = "Prüfling-42", string? host = null, int port = 9100)
    {
        var step = BlockCatalog.Default.Create(LabelPrintingBlocks.Print, "label", null, new Dictionary<string, string>
        {
            ["Druckart"] = mode, ["ZplVorlage"] = Template, ["Host"] = host ?? "localhost",
            ["Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Warteschlange"] = "Etikettendrucker",
        });
        step.Parameters.Remove(LabelPrintingBlocks.FormatParameter);
        BlockCatalog.Default.Rebuild(step);
        var definition = Test(Group("loop").Add(step));
        definition.Root.Variables.Add(new("Kennung", VarType.String, id));
        return definition;
    }

    [Theory]
    [InlineData("Simulation")]
    [InlineData("TCP")]
    [InlineData("Windows")]
    public async Task Runs_without_printer_preview_ZPL_without_sending(string mode)
    {
        var run = new TestRun(Program(mode));
        var status = await run.RunAsync().Within();
        Assert.Equal(ResultRating.NResult, status.Rating);
        var result = Assert.Single(run.Steps("label"));
        Assert.Equal(Expected, result.Value);
        Assert.Contains("simuliert", result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TCP_block_transmits_ZPL_and_binary_LBL_unchanged(bool cab)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var reading = ReceiveLabel();
            var definition = Program("TCP", host: "127.0.0.1", port: port);
            var expected = cab ? CabBytes : Encoding.UTF8.GetBytes(Expected);
            var run = new TestRun(cab ? WithCab(definition) : definition, new() { LabelPrinter = new LabelPrinter() });
            await run.RunAsync().Within();
            Assert.Equal(expected, await reading.Within());
            Assert.Equal((long)expected.Length, Assert.Single(run.Steps("label")).Value);
        }
        finally { listener.Stop(); }

        async Task<byte[]> ReceiveLabel()
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var buffer = new MemoryStream();
            await client.GetStream().CopyToAsync(buffer);
            return buffer.ToArray();
        }
    }

    [Fact]
    public async Task Windows_mode_uses_named_raw_queue_and_preview_does_not_invoke_printer()
    {
        var printer = new RecordingPrinter();
        var run = new TestRun(Program("Windows"), new() { LabelPrinter = printer });
        await run.RunAsync().Within();
        var sent = Assert.Single(printer.Requests);
        Assert.Equal(LabelPrinterKind.Windows, sent.Kind);
        Assert.Equal("Etikettendrucker", sent.Destination);
        Assert.Equal(Expected, Encoding.UTF8.GetString(sent.Zpl));

        var preview = new TestRun(Program("Simulation"), new() { LabelPrinter = printer });
        await preview.RunAsync().Within();
        Assert.Single(printer.Requests);
        Assert.Equal(Expected, Assert.Single(preview.Steps("label")).Value);
    }

    [Fact]
    public async Task Embedded_LBL_remains_binary_after_roundtrip_and_simulates_without_sending()
    {
        var definition = WithCab(Program("Windows"));
        var project = new ProjectSerializer().Clone(new TestProject(definition));
        var printer = new RecordingPrinter();
        var run = new TestRun(project.Definition, new() { LabelPrinter = printer });
        await run.RunAsync().Within();
        var sent = Assert.Single(printer.Requests);
        Assert.Equal(LabelPrinterKind.Windows, sent.Kind);
        Assert.Equal(CabBytes, sent.Data);

        var preview = new TestRun(WithCab(Program("Simulation")), new() { LabelPrinter = printer });
        await preview.RunAsync().Within();
        Assert.Equal((long)CabBytes.Length, Assert.Single(preview.Steps("label")).Value);
        Assert.Single(printer.Requests);
    }

    [Theory]
    [InlineData("", "erforderlich")]
    [InlineData("!!!", "Base64")]
    public async Task Missing_or_invalid_LBL_is_rejected_before_printing(string encoded, string expected)
    {
        var printer = new RecordingPrinter();
        var run = new TestRun(WithCab(Program("Windows"), encoded), new() { LabelPrinter = printer });
        Assert.Equal(ResultRating.Error, (await run.RunAsync().Within()).Rating);
        Assert.Contains(expected, Assert.Single(run.Steps("label")).Message);
        Assert.Empty(printer.Requests);
    }

    [Fact]
    public async Task Unsafe_variable_and_invalid_port_become_ERRORS_before_printing()
    {
        var printer = new RecordingPrinter();
        var run = new TestRun(Program("Windows", id: "SN^XZ"), new() { LabelPrinter = printer });
        var status = await run.RunAsync().Within();
        Assert.Equal(ResultRating.Error, status.Rating);
        Assert.Contains("Steuerzeichen", Assert.Single(run.Steps("label")).Message);
        Assert.Empty(printer.Requests);
        var invalid = new TestRun(Program("TCP", port: 0), new() { LabelPrinter = printer });
        status = await invalid.RunAsync().Within();
        Assert.Equal(ResultRating.Error, status.Rating);
        Assert.Contains("TCP-Port", Assert.Single(invalid.Steps("label")).Message);
        Assert.Empty(printer.Requests);
    }

    [Fact]
    public async Task Editor_simulation_clones_label_step_and_uses_preview_even_for_TCP()
    {
        var project = new TestProject(Program("TCP"));
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project);
        var station = shell.SelectedStation;
        await station.StartSimulationAsync().Within();
        ui.Pump();
        var run = Assert.IsType<TestRun>(station.Run);
        Assert.Equal(Expected, Assert.Single(run.Steps("label")).Value);
    }

    [Fact]
    public async Task Windows_transport_reports_an_unavailable_queue_without_creating_a_job()
    {
        if (!OperatingSystem.IsWindows()) return;
        var request = new LabelPrintRequest(LabelPrinterKind.Windows, "Testumgebung-" + Guid.NewGuid().ToString("N"),
            0, Encoding.UTF8.GetBytes(Expected), TimeSpan.Zero);
        var error = await Assert.ThrowsAsync<IOException>(() => new LabelPrinter().SendAsync(request, CancellationToken.None)).Within();
        Assert.Contains("Druckwarteschlange öffnen", error.Message);
    }

    [Theory]
    [InlineData("REAL-42", false)]
    [InlineData("X\rA 9", true)]
    [InlineData("ä", true)]
    public async Task Cab_designer_renders_actual_values_without_modifying_binary_images(string id, bool fails)
    {
        var layout = new LabelLayout
        {
            Elements =
            [
                new() { Kind = LabelElementKind.Image, ImageWidth = 8, ImageHeight = 1, Bitmap = [0x1B] },
                new() { Kind = LabelElementKind.Code128, Value = "ID-{Kennung}", ExampleValues = "Kennung=EXAMPLE-ONLY" },
            ],
        };
        var bytes = CabLabelLayout.Render(layout, out var offset);
        var definition = WithCab(Program("Windows", id), Convert.ToBase64String(bytes));
        var step = (StepDefinition)definition.Find("label")!;
        step.Parameters[LabelPrintingBlocks.CabTextOffsetParameter] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        BlockCatalog.Default.Rebuild(step);
        var printer = new RecordingPrinter();
        var run = new TestRun(definition, new() { LabelPrinter = printer });
        var result = await run.RunAsync().Within();
        if (fails)
        {
            Assert.Equal(ResultRating.Error, result.Rating);
            Assert.Empty(printer.Requests);
            return;
        }
        var printed = Assert.Single(printer.Requests).Data;
        Assert.Equal(bytes[..offset], printed[..offset]);
        var text = Encoding.UTF8.GetString(printed, offset, printed.Length - offset);
        Assert.Contains("ID-REAL-42", text);
        Assert.DoesNotContain("EXAMPLE-ONLY", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Designer_prints_datamatrix_and_native_gs1_variants(bool cab)
    {
        var layout = new LabelLayout
        {
            Elements =
            [
                new() { Kind = LabelElementKind.DataMatrix, Value = "_1:{Kennung}", Size = 4 },
                new() { Kind = LabelElementKind.Gs1DataMatrix, Value = "(01)09506000134376(10)LOT(21){Kennung}", Size = 4 },
                new() { Kind = LabelElementKind.Gs1Qr, Value = "(01)09506000134376(10)LOT(21){Kennung}", Size = 4 },
            ],
        };
        var definition = Program("Windows", "SN_123");
        var step = (StepDefinition)definition.Find("label")!;
        if (cab)
        {
            var data = CabLabelLayout.Render(layout, out var offset);
            WithCab(definition, Convert.ToBase64String(data));
            step.Parameters[LabelPrintingBlocks.CabTextOffsetParameter] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            step.Parameters[LabelLayout.Parameter] = layout.Save();
            step.Parameters["ZplVorlage"] = layout.ToZpl();
        }
        BlockCatalog.Default.Rebuild(step);
        var printer = new RecordingPrinter();
        var run = new TestRun(definition, new() { LabelPrinter = printer });
        Assert.Equal(ResultRating.NResult, (await run.RunAsync().Within()).Rating);
        var raw = Encoding.UTF8.GetString(Assert.Single(printer.Requests).Data);
        if (cab)
        {
            Assert.Contains(",DATAMATRIX,0.5;_1:SN_123", raw);
            Assert.Contains(",GS1-DATAMATRIX,0.5;(01)09506000134376(10)LOT(21)SN_123", raw);
            Assert.Contains(",GS1QRCODE,0.5;(01)09506000134376(10)LOT(21)SN_123", raw);
        }
        else
        {
            Assert.Contains("^BXN,4,200,0,0,1,_,1^FD__1:SN__123^FS", raw);
            Assert.Contains("^FD_1010950600013437610LOT_121SN__123^FS", raw);
            Assert.Contains("^FD>;>8010950600013437610LOT>821SN_123^FS", raw);
            Assert.DoesNotContain("@TU-", raw);
        }
    }

    [Theory]
    [InlineData("(01)12")]
    [InlineData("(17)abc123")]
    [InlineData("(99)not-in-catalog")]
    [InlineData("unlabeled")]
    public void Invalid_gs1_values_are_not_rendered(string value)
        => Assert.Throws<FormatException>(() => Gs1LabelData.Cab(value));

    private sealed class RecordingPrinter : ILabelPrinter
    {
        public List<LabelPrintRequest> Requests { get; } = [];
        public Task SendAsync(LabelPrintRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
