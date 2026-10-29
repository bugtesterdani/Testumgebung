using Testumgebung.Domain.Processes;
using Testumgebung.Workers;
namespace Testumgebung.Engine.Tests;
public sealed class LiveFeedTests
{
    private static ProcessLaunch Request(string[] args,bool capture=true)=>new(ExternalProcessKind.Executable,Path.Combine(AppContext.BaseDirectory,"fixtures","process","ProcessProbe.exe"),args,AppContext.BaseDirectory,new Dictionary<string,string>(),CaptureOutput:capture);
    private static string Workers=>Path.Combine(AppContext.BaseDirectory,"workers");
    [Fact]
    public async Task Independent_cursors_read_stderr_and_wait_for_changes_without_consuming_dialog()
    {
        using var session=new ProcessSession();var process=session.Start(Request(["dialog"]),"test",Workers,null);
        var first=session.ReadOutputAsync(process.Handle,ProcessOutputStream.StandardOutput,0,default);
        var read=await first.WaitAsync(TimeSpan.FromSeconds(10));Assert.NotEmpty(read.Text);
        var again=await session.ReadOutputAsync(process.Handle,ProcessOutputStream.StandardOutput,0,default);Assert.StartsWith(read.Text,again.Text);
        await session.WaitForOutputAsync(process.Handle,"ready>",false,ProcessOutputStream.StandardOutput,TimeSpan.FromSeconds(5),default);
        await session.SendAsync(process.Handle,"hello\n",false,TimeSpan.FromSeconds(5),default);
        long errorOffset=0;
        var errorText="";
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(!errorText.Contains("value=42;hello;"))
        {
            var chunk=await session.ReadOutputAsync(process.Handle,ProcessOutputStream.StandardError,errorOffset,deadline.Token);
            errorText+=chunk.Text;errorOffset=chunk.NextOffset;
        }
        using var cancel=new CancellationTokenSource();
        var pending=session.ReadOutputAsync(process.Handle,ProcessOutputStream.StandardError,errorOffset,cancel.Token);Assert.False(pending.IsCompleted);
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        await session.SendAsync(process.Handle,"done\n",true,TimeSpan.FromSeconds(5),default);
        await session.WaitAsync(process.Handle,TimeSpan.FromSeconds(5),default);
        var closed=await session.ReadOutputAsync(process.Handle,ProcessOutputStream.StandardError,errorOffset,default);Assert.True(closed.Completed);Assert.Empty(closed.Text);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>session.ReadOutputAsync(process.Handle,ProcessOutputStream.StandardError,long.MaxValue,default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>session.ReadOutputAsync(process.Handle,(ProcessOutputStream)99,0,default));
    }
    [Fact]
    public async Task Overflow_and_disabled_capture_are_reported_instead_of_silently_losing_log_text()
    {
        using var session=new ProcessSession();var process=session.Start(Request(["flood"]),"test",Workers,null);
        await session.WaitAsync(process.Handle,TimeSpan.FromSeconds(10),default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>session.ReadOutputAsync(process.Handle,ProcessOutputStream.StandardOutput,0,default));
        var disabled=session.Start(Request(["sleep","20"],false),"test",Workers,null);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>session.ReadOutputAsync(disabled.Handle,ProcessOutputStream.StandardOutput,0,default));
    }
}
