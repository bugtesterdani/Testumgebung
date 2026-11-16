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

public sealed class LabelPrintingTests
{
    private const string Template = "^XA\n^CI28\n^FD{Kennung} {{Los}}^FS\n^XZ";
    private const string Expected = "^XA\n^CI28\n^FDPrüfling-42 {Los}^FS\n^XZ";

    private static TestDefinition Program(string mode, string id = "Prüfling-42", string? host = null, int port = 9100)
    {
        var step = BlockCatalog.Default.Create(LabelPrintingBlocks.Print, "label", null, new Dictionary<string, string>
        {
            ["Druckart"] = mode, ["ZplVorlage"] = Template, ["Host"] = host ?? "localhost",
            ["Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Warteschlange"] = "Etikettendrucker",
        });
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

    [Fact]
    public async Task TCP_block_transmits_UTF8_ZPL_unchanged()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var reading = ReceiveLabel();
            var run = new TestRun(Program("TCP", host: "127.0.0.1", port: port), new() { LabelPrinter = new LabelPrinter() });
            await run.RunAsync().Within();
            Assert.Equal(Expected, Encoding.UTF8.GetString(await reading.Within()));
            Assert.Equal((long)Encoding.UTF8.GetByteCount(Expected), Assert.Single(run.Steps("label")).Value);
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
