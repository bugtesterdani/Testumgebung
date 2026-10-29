using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Processes;
using Testumgebung.Engine.Blocks;
using Testumgebung.Workers;
using static Testumgebung.Engine.Tests.Kit;
namespace Testumgebung.Engine.Tests;

public sealed class LiveLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(),"live-log-tests-"+Guid.NewGuid().ToString("N"));
    public LiveLogTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { foreach(var file in Directory.GetFiles(_folder)) File.Delete(file); Directory.Delete(_folder); }
    private string FilePath(string name) => Path.Combine(_folder,name);
    private static StepDefinition Monitor(Dictionary<string,string> p) => BlockCatalog.Default.Create(LiveLogBlocks.Monitor,"monitor",parameters:p);
    private static Dictionary<string,string> FileOptions(string path) => new() { ["Quelle"]="Datei",["Datei"]=path,["Zeitlimit"]="10000",["EndeRegex"]="^DONE$",["ZahlenRegex"]=@"(?<current>\d+) / (?<total>\d+)" };
    [Fact]
    public async Task Shared_file_shows_live_progress_before_writer_closes_and_tees_exact_text()
    {
        var path=FilePath("writer.log"); var copy=FilePath("copy.log");
        await using var writer=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.ReadWrite);
        var options=FileOptions(path);options["Mitschrift"]=copy;
        var run=new TestRun(Test(Group("loop").Add(Monitor(options)))); var task=run.RunAsync();
        try
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes("1 / 4\n")); await writer.FlushAsync();
            await run.WaitFor<LiveOutputReported>(e=>e.Update.Percent==25); Assert.False(task.IsCompleted);
            await writer.WriteAsync(Encoding.UTF8.GetBytes("4 / 4\nDONE")); await writer.FlushAsync();
            Assert.Equal(ResultRating.NResult,(await task.Within()).Rating);
            Assert.Equal("1 / 4\n4 / 4\nDONE",File.ReadAllText(copy));
            Assert.Equal(100,run.Events.OfType<LiveOutputReported>().Last().Update.Percent);
        }
        finally {run.RequestAbort();await task.Within();}
    }
    [Fact]
    public async Task Reader_waits_for_creation_decodes_split_utf8_and_releases_handle_on_abort()
    {
        var path=FilePath("late.log");var options=FileOptions(path);options["EndeRegex"]="^never$";
        var run=new TestRun(Test(Group("loop").Add(Monitor(options))));var task=run.RunAsync();
        try
        {
            await run.WaitFor<LiveOutputReported>(_=>true);
            await using(var writer=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.ReadWrite))
            {
                await writer.WriteAsync(new byte[]{0xC3});await writer.FlushAsync();
                await writer.WriteAsync(new byte[]{0xBC,10});await writer.FlushAsync();
                await run.WaitFor<LiveOutputReported>(e=>e.Update.Text.Contains('ü'));
            }
            run.RequestAbort(); Assert.True((await task.Within()).OperatorAbort);
            using var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        }
        finally {run.RequestAbort();await task.Within();}
    }
    [Fact]
    public async Task Exclusive_lock_and_timeout_are_explicit_errors()
    {
        var path=FilePath("exclusive.log");
        using(var locked=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None))
        {
            var run=new TestRun(Test(Group("loop").Add(Monitor(FileOptions(path)))));
            Assert.Equal(ResultRating.Error,(await run.RunAsync().Within()).Rating);
            Assert.Contains("exklusiv",run.Steps("monitor").Single().Message);
        }
        var options=FileOptions(FilePath("missing.log")); options["Zeitlimit"]="50";
        var timeout=new TestRun(Test(Group("loop").Add(Monitor(options))));
        Assert.Equal(ResultRating.Error,(await timeout.RunAsync().Within()).Rating);
        Assert.Contains("Gesamtzeitlimit",timeout.Steps("monitor").Single().Message);
    }
    [Fact]
    public async Task Graphical_monitor_preserves_process_dialog_and_streams_before_exit()
    {
        var probe=Path.Combine(AppContext.BaseDirectory,"fixtures","process","ProcessProbe.exe");
        var workers=Path.Combine(AppContext.BaseDirectory,"workers");
        IProcessSession? processes=null;string? handle=null;
        var start=new StepDefinition("start","start",new DelegateAction(ctx=>
        {
            processes=ctx.Processes; handle=processes.Start(new(ExternalProcessKind.Executable,probe,["dialog"],_folder,new Dictionary<string,string>()),ctx.ProcessOwner,workers,null).Handle;
            ctx.Set("H",handle);return Task.FromResult(StepOutcome.Unrated());
        }));
        var run=new TestRun(Test(Group("loop").Var("H",VarType.String).Add(start,Monitor(new(){["Prozess"]="{H}",["Zeitlimit"]="10000"}))));
        var task=run.RunAsync();
        try
        {
            await run.WaitFor<LiveOutputReported>(_=>string.Concat(run.Events.OfType<LiveOutputReported>().Select(e=>e.Update.Text)).Contains("ready>"));Assert.False(task.IsCompleted);
            Assert.Equal("ready>",(await processes!.WaitForOutputAsync(handle!,"ready>",false,ProcessOutputStream.StandardOutput,TimeSpan.FromSeconds(5),default)).Value);
            await processes.SendAsync(handle!,"hello\ndone\n",true,TimeSpan.FromSeconds(5),default);
            Assert.Equal(ResultRating.NResult,(await task.Within()).Rating);
            Assert.Contains("done:done:",string.Concat(run.Events.OfType<LiveOutputReported>().Select(e=>e.Update.Text)));
        }
        finally{run.RequestAbort();await task.Within();}
    }
    [Theory]
    [InlineData("Zeitlimit","0")] [InlineData("Zeitlimit","NaN")] [InlineData("ZahlenRegex","(bad)")]
    public void Bad_settings_are_rejected_before_execution(string key,string value)
    {
        var options=FileOptions("C:/test.log");options[key]=value;
        Assert.NotEmpty(BlockCatalog.Default.Rebuild(Monitor(options)));
    }
    [Fact]
    public async Task Tail_from_end_ignores_old_text_and_reads_truncation_without_reusing_old_decoder_state()
    {
        var path=FilePath("tail.log");File.WriteAllText(path,"OLD\n");
        var options=FileOptions(path);options["AbEnde"]="true";
        var run=new TestRun(Test(Group("loop").Add(Monitor(options))));var task=run.RunAsync();
        try
        {
            await run.WaitFor<LiveOutputReported>(e=>!e.Update.Reset && e.Update.Text=="");
            await using(var writer=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite))
            {
                writer.Seek(0,SeekOrigin.End);
                await writer.WriteAsync(Encoding.UTF8.GetBytes("NEW\n"));await writer.FlushAsync();
                var seen=await run.WaitFor<LiveOutputReported>(e=>e.Update.Text.Contains("NEW"));
                writer.SetLength(0);writer.Position=0;await writer.FlushAsync();
                await run.WaitFor<LiveOutputReported>(e=>e.Sequence>seen.Sequence && e.Update.Text=="");
                await writer.WriteAsync(Encoding.UTF8.GetBytes("1 / 2\nDONE\n"));await writer.FlushAsync();
                Assert.Equal(ResultRating.NResult,(await task.Within()).Rating);
            }
            var text=string.Concat(run.Events.OfType<LiveOutputReported>().Select(e=>e.Update.Text));
            Assert.DoesNotContain("OLD",text);Assert.Contains("1 / 2",text);
        }
        finally{run.RequestAbort();await task.Within();}
    }
    [Theory]
    [InlineData("Datei", "relative.log")]
    [InlineData("Kodierung", "not-an-encoding")]
    [InlineData("Mitschrift", "relative.log")]
    [InlineData("Mitschrift", "same")]
    public async Task Invalid_paths_and_encodings_fail_without_overwriting_source(string key,string value)
    {
        var path=FilePath("source.log");File.WriteAllText(path,"keep");var options=FileOptions(path);
        options[key]=value=="same"?path:value;
        var run=new TestRun(Test(Group("loop").Add(Monitor(options))));
        Assert.Equal(ResultRating.Error,(await run.RunAsync().Within()).Rating);Assert.Equal("keep",File.ReadAllText(path));
    }

}
