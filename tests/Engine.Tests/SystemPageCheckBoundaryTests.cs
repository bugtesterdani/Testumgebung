using Testumgebung.Presentation.Pilot;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class SystemPageCheckBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Check_page_sorts_severity_and_prevents_concurrent_checks(bool failure)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var calls = 0;
        var page = new SystemPageViewModel(() => new SystemCheckContext
        {
            RunProcess = (_, _) => { Interlocked.Increment(ref calls); entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return "fixture"; },
            VisaAvailable = () => false,
            DaqmxAvailable = () => false,
            AccessStore = failure ? new Testumgebung.Identity.AccessConfigStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")) : null
        }, [], null, () => false, new QueueDispatcher());
        Assert.False(page.CheckPassed); page.ProbeDevices = true;
        var check = page.RunCheckAsync();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10))); Assert.True(page.IsBusy); Assert.False(page.RunCheckCommand.CanExecute(null));
            var before = calls; await page.RunCheckAsync(); Assert.Equal(before, calls);
        }
        finally { release.Set(); }
        await check.Within();
        Assert.False(page.IsBusy); Assert.True(page.RunCheckCommand.CanExecute(null)); Assert.NotEmpty(page.Checks);
        Assert.Equal(page.Checks.OrderByDescending(c => c.Level).ToArray(), page.Checks.ToArray());
        Assert.Equal(page.Checks.All(c => c.Level != CheckLevel.Error), page.CheckPassed);
        Assert.Contains(page.CheckPassed ? "Bereit" : "beheben", page.CheckSummary);
    }

    [Fact]
    public async Task Failed_context_creation_releases_busy_state()
    {
        var page = new SystemPageViewModel(() => throw new InvalidOperationException("fixture"), [], null, () => false, new QueueDispatcher());
        Assert.Equal("fixture", (await Assert.ThrowsAsync<InvalidOperationException>(page.RunCheckAsync)).Message);
        Assert.False(page.IsBusy); Assert.True(page.RunCheckCommand.CanExecute(null)); Assert.Empty(page.Checks);
    }
}
