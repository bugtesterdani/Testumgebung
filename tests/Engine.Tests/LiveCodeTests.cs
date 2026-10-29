using Testumgebung.Contracts;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;
namespace Testumgebung.Engine.Tests;
#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public sealed class LiveCodeTests
{
    [Theory]
    [InlineData("Python", "live_output('tick\\n', 25, 'Schreiben', 'Flash', True)\nreturn Pass()")]
    [InlineData("C#", "LiveOutput(\"tick\\n\", 25, \"Schreiben\", \"Flash\", true); return Pass();")]
    public async Task Code_workers_can_update_text_percent_status_and_channel(string language,string source)
    {
        var block=BlockCatalog.Default.Create(BlockCatalog.Code,"code",parameters:new Dictionary<string,string>{["Sprache"]=language,["Quelltext"]=source,["Timeout"]="30"});
        var run=new TestRun(Test(Group("loop").Add(block)),new TestRunOptions{CodeRuntime=CodeBlockTests.SharedRuntime.Value});
        Assert.Equal(ResultRating.Pass,(await run.RunAsync().WaitAsync(TimeSpan.FromMinutes(2))).Rating);
        var update=Assert.Single(run.Events.OfType<LiveOutputReported>()).Update;
        Assert.Contains("tick",update.Text);Assert.Equal(25,update.Percent);Assert.Equal("Schreiben",update.Status);Assert.Equal("Flash",update.Channel);Assert.True(update.Reset);
    }
    [Fact]
    public async Task Python_default_arguments_and_host_validation_are_visible()
    {
        var source="live_output()\nlive_output('tick')\nlive_output(percent=101)\nreturn Pass()";
        var block=BlockCatalog.Default.Create(BlockCatalog.Code,"code",parameters:new Dictionary<string,string>{["Sprache"]="Python",["Quelltext"]=source});
        var run=new TestRun(Test(Group("loop").Add(block)),new TestRunOptions{CodeRuntime=CodeBlockTests.SharedRuntime.Value});
        Assert.Equal(ResultRating.Error,(await run.RunAsync().Within()).Rating);
        Assert.Equal(2,run.Events.OfType<LiveOutputReported>().Count());
        Assert.Contains("Fortschritt",run.Steps("code").Single().Message);
    }
    [Fact]
    public async Task Station_dispatches_live_events_and_clears_cards_before_next_run()
    {
        var block=BlockCatalog.Default.Create(BlockCatalog.Code,"code",parameters:new Dictionary<string,string>{["Sprache"]="Python",["Quelltext"]="live_output('running', 50, 'Working')\nreturn Pass()"});
        var project=new Testumgebung.Persistence.TestProject(Test(Group("loop").Add(block)));
        var station=new Testumgebung.Presentation.ViewModels.StationViewModel("Test",()=>project,new Testumgebung.Hardware.ResourceManager(),codeRuntime:CodeBlockTests.SharedRuntime.Value);
        await station.StartSimulationAsync().Within();
        Assert.Null(station.Error);Assert.Equal(50,Assert.Single(station.LiveOutputs.Items).Value);
        block.Parameters["Quelltext"]="return Pass()";
        await station.StartSimulationAsync().Within();Assert.Empty(station.LiveOutputs.Items);
    }

}
