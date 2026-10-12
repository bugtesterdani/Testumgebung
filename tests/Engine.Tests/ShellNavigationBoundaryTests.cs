using System.Data.Common;
using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Identity;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.Settings;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class ShellNavigationBoundaryTests
{
    private sealed class NoProviders : IDatabaseProviderRegistry { public DbProviderFactory? Factory(DatabaseProvider provider) => null; }
    public static IEnumerable<object[]> Cases()
    {
        foreach (var develop in new[] { false, true }) foreach (var release in new[] { false, true })
            foreach (var settings in new[] { false, true }) yield return [develop, release, settings];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Navigation_obeys_roles_and_available_settings(bool develop, bool release, bool settings)
    {
        var folder = Path.Combine(Path.GetTempPath(), "shell-nav-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        try
        {
            await using var services = new ResultServices(new AppSettings { ResultFolder = Path.Combine(folder, "results"), Database = new DatabaseSettings { Provider = DatabaseChoice.None } }, new NoProviders(), Path.Combine(folder, "outbox"), Path.Combine(folder, "database"), recoveryFolder: Path.Combine(folder, "recovery"), releaseFolder: Path.Combine(folder, "releases"));
            var config = new AccessConfig { Users = [new RightsEntry { Upn = "tester@simuliert.local", Develop = develop, Release = release }] };
            var identity = IdentityServices.Simulated(() => "Tester", config);
            var ui = new QueueDispatcher();
            var shell = new ShellViewModel(ui, project: SampleProjects.AcPruefung(), clock: new VirtualClock(), identity: identity, services: settings ? services : null, settingsStore: new SettingsStore(Path.Combine(folder, "settings.json"), PlainSecretProtector.Instance));
            Assert.False(shell.HasReleases); Assert.False(shell.ShowReleasesNav); Assert.Equal(settings, shell.HasSettings);
            Assert.Equal("Anmelden", shell.UserText); Assert.Equal("", shell.UserDetail);
            await shell.LoginAsync(false); ui.Pump();
            Assert.Equal(develop || release, shell.IsSignedIn); Assert.Equal(develop, shell.IsDeveloper); Assert.Equal(release, shell.CanRelease);
            foreach (var page in Enum.GetValues<ShellPage>())
            {
                shell.Page = ShellPage.Operator;
                var allowed = page switch { ShellPage.Operator or ShellPage.Protocol => true, ShellPage.Releases => false, ShellPage.Settings => develop && settings, ShellPage.Reports => develop || release, _ => develop };
                Assert.Equal(allowed, shell.NavigateCommand.CanExecute(page));
                Assert.Equal(allowed, shell.NavigateCommand.CanExecute(page.ToString()));
                shell.NavigateCommand.Execute(page); Assert.Equal(allowed ? page : ShellPage.Operator, shell.Page);
                shell.Page = ShellPage.Operator; shell.NavigateCommand.Execute(page.ToString()); Assert.Equal(allowed ? page : ShellPage.Operator, shell.Page);
                Assert.Equal(shell.IsSignedIn && shell.Page is not (ShellPage.Operator or ShellPage.Protocol) ? "ENTWICKLERANSICHT" : "WERKERANSICHT", shell.ViewTitle);
            }
            Assert.True(shell.NavigateCommand.CanExecute(new object())); shell.NavigateCommand.Execute("not-a-page");
            shell.SelectStationCommand.Execute(null); shell.SelectedStation = null!; shell.SelectedStation = shell.SelectedStation;
            shell.NoteActivity(); shell.CheckInactivity();
            Assert.NotEmpty(shell.ActiveSoftwareText); Assert.Null(shell.IncompleteRunsHint);
            await shell.LogoutAsync(); ui.Pump(); Assert.False(shell.IsSignedIn); Assert.Equal(ShellPage.Operator, shell.Page);
            Assert.Equal("Anmelden", shell.UserText); Assert.Equal("", shell.UserDetail);
        }
        finally { Directory.Delete(folder, true); }
    }
}
