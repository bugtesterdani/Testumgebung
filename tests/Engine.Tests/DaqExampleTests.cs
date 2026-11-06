using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Instruments;
using Testumgebung.Persistence;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class DaqExampleTests
{
    [Fact]
    public async Task Shipped_example_runs_existing_io_blocks_and_records_curve_with_daq_replica()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName,"Testumgebung.sln"))) root = root.Parent;
        var project = new ProjectSerializer().Load(Path.Combine(root.FullName,"docs","beispiele","daq-demo.tproj.json"));
        Assert.Empty(project.Definition.Validate());
        foreach (var step in project.Definition.AllNodes().OfType<StepDefinition>()) Assert.Empty(BlockCatalog.Default.Rebuild(step));
        using var device = InstrumentCatalog.Create(DaqModels.Simulation,"T7","DAQ-DEMO",new Dictionary<string,string> { ["Rueckkopplung"]="AIN0=DAC0", ["Sichere Ausgänge"]="DAC0=0;DIO4=0" });
        var hardware = new HardwareRegistry().Bind("DAQ_Ausgang",device,"DAC0").Bind("DAQ_Eingang",device,"AIN0").Bind("DAQ_Digital",device,"DIO4");
        var run = new TestRun(project.Definition,new TestRunOptions { Clock=new VirtualClock(),Hardware=hardware,Operator=new Answers() });
        var recorder = new Testumgebung.Persistence.Results.ResultRecorder(run);
        Testumgebung.Persistence.Results.ResultRun? result = null; recorder.RunRecorded += value => result=value;
        var status = await run.RunAsync().Within();
        Assert.Equal(ResultRating.Pass,status.Rating); Assert.NotNull(result);
        Assert.Equal("DAQ-TEST",result.DutId);
        var waveform = result.Waveforms.Single(w => w.Data.Channel == "Spannung").Data;
        var metadata = EnvelopeTraceMetadata.Decode(waveform.Quality)!;
        Assert.Equal(6,metadata.OriginalTimes.Length); Assert.True(metadata.Complete);
        Assert.Equal(0,await device.ReadAsync("AIN0",default)); Assert.Equal(0,await device.ReadAsync("DIO4",default));
    }
    private sealed class Answers : IOperatorInterface
    {
        public Task<object?> AskAsync(string runId,OperatorPrompt prompt,CancellationToken token) => Task.FromResult<object?>("DAQ-TEST");
    }
}
