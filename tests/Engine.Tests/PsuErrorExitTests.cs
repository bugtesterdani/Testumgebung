using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class PsuErrorExitTests
{
    [Theory]
    [InlineData("Laden", "Timeout", true, ResultRating.Error)]
    [InlineData("Halten", "Kommunikation", true, ResultRating.Error)]
    [InlineData("Entladen", "Ungültig", true, ResultRating.Error)]
    [InlineData("Entladen", "Unvollständig", true, ResultRating.Error)]
    [InlineData("Halten", "Folgefehler", true, ResultRating.Error)]
    [InlineData("Entladen", "NOK", true, ResultRating.Fail)]
    [InlineData("Halten", "NOK", false, ResultRating.Fail)]
    public async Task Exit_shuts_down_active_outputs_before_the_dut_completes(string phase, string fault, bool immediate, ResultRating rating)
    {
        var hardware = new Fixture(fault);
        var bindings = new HardwareRegistry().Bind("Meter", hardware, "Meter");
        foreach (var output in new[] { "Quelle", "Laden", "Entladen" }) bindings.Bind(output, hardware, output);

        var parameters = (BlockCatalog.ExitDutOnErrorParameter, immediate ? "true" : "false");
        var faulty = fault switch
        {
            "NOK" => Block(BlockCatalog.SetRating, "fault", ("Bewertung", "FAIL"), parameters),
            "Unvollständig" => Block(TraceBlocks.Stop, "fault", parameters),
            "Folgefehler" => Block(BlockCatalog.Output, "fault", ("Ressource", "Quelle"), ("Wert", "Undefiniert"), parameters),
            _ => Block(BlockCatalog.Measure, "fault", ("Ressource", "Meter"), parameters)
        };
        var nested = Group(phase).Add(Output("enable-phase", phase == "Entladen" ? "Entladen" : "Laden", "1"));
        if (fault == "Unvollständig")
        {
            nested.Add(Block(TraceBlocks.Start, "capture", (TraceBlocks.EvaluationParameter, TraceBlocks.FormulaBand),
                (TraceBlocks.LowerFormulaParameter, "0"), (TraceBlocks.UpperFormulaParameter, "10")),
                Block(TraceBlocks.Point, "first", ("Zeit", "0"), ("Werte", "5")));
        }
        nested.Add(faulty, Output("regular-nested", "Quelle", "2"));
        nested.ExitGroup = Group("nested-exit").Add(Output("disable-charge", "Laden", "0"), Output("disable-discharge", "Entladen", "0"));
        var dut = Group("dut").Var("Kennung", VarType.String, "PSU-1");
        dut.IsDutRun = true;
        dut.Add(Output("enable-source", "Quelle", "1"), nested, Output("regular-dut", "Quelle", "3"));
        dut.ExitGroup = Group("dut-exit").Add(Output("disable-source", "Quelle", "0"));
        var project = new ProjectSerializer().Clone(new TestProject(Test(Group("loop").Add(dut))));
        var run = new TestRun(project.Definition, new TestRunOptions { Hardware = bindings });
        bool? outputsSafeAtCompletion = null;
        run.EventRaised += e =>
        {
            if (e is DutRunCompleted) outputsSafeAtCompletion = hardware.Outputs.Count == 3 && hardware.Outputs.Values.All(value => value == 0);
        };

        Assert.Equal(rating, (await run.RunAsync().Within()).Rating);
        Assert.Equal(immediate ? 0 : 1, run.Steps("regular-nested").Count());
        Assert.Equal(immediate ? 0 : 1, run.Steps("regular-dut").Count());
        Assert.True(outputsSafeAtCompletion);
        Assert.Equal(new[] { "Laden", "Entladen", "Quelle" }, hardware.DisabledOutputs);
        Assert.All(hardware.Outputs.Values, value => Assert.Equal(0, value));
    }

    private static StepDefinition Block(string type, string id, params (string Key, string Value)[] parameters)
        => BlockCatalog.Default.Create(type, id, parameters: parameters.ToDictionary(parameter => parameter.Key, parameter => parameter.Value));

    private static StepDefinition Output(string id, string resource, string value)
        => Block(BlockCatalog.Output, id, ("Ressource", resource), ("Wert", value));

    private sealed class Fixture(string fault) : IHardwareAdapter
    {
        public string Name => "PSU-Simulation";
        public Dictionary<string, double> Outputs { get; } = [];
        public List<string> DisabledOutputs { get; } = [];
        public ChannelCapabilities GetCapabilities(string channel) => new("V", 0, 24);

        public Task<double> ReadAsync(string channel, CancellationToken token) => fault switch
        {
            "Timeout" => Task.FromException<double>(new TimeoutException("Messgerät antwortet nicht.")),
            "Kommunikation" => Task.FromException<double>(new IOException("Messgerät getrennt.")),
            _ => Task.FromResult(double.NaN)
        };

        public Task WriteAsync(string channel, double value, CancellationToken token)
        {
            Outputs[channel] = value;
            if (value == 0) DisabledOutputs.Add(channel);
            return Task.CompletedTask;
        }
    }
}
