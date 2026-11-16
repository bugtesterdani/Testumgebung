using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Identity;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Pilot;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.Settings;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class ManageRightsTests
{
    private const string Manager = "manager@example.test";
    private const string Password = "access-test-manager";

    [Theory]
    [InlineData("Testumgebung.Verwalten", true, false)]
    [InlineData("Testumgebung.Entwickeln", true, true)]
    [InlineData("Testumgebung.Freigeben", false, false)]
    public async Task Confirmed_Entra_roles_grant_only_the_intended_permissions(string role, bool manage, bool develop)
    {
        var config = new AccessConfig { ClientId = "client", TenantId = "tenant", RightsSource = "AppRollen" };
        var provider = new EntraProvider(new UserIdentity(Manager, "Manager", "Entra-Test", true, "tenant", Roles: [role]));
        var session = new SessionService(provider, config);

        Assert.True(await session.SignInAsync());
        Assert.Equal(manage, session.CanManage);
        Assert.Equal(develop, session.CanDevelop);
        Assert.Equal(role == config.ReleaseRole, session.CanRelease);
        Assert.Equal(UserRights.None, config.RightsFor(new UserIdentity(Manager, "Manager", "Simuliert", false, Roles: [role])));
    }

    [Fact]
    public async Task Legacy_local_access_file_keeps_management_access_without_a_manage_field()
    {
        var config = JsonSerializer.Deserialize<AccessConfig>(
            """{"AllowLocalDevelopment":true,"Users":[{"Upn":"developer@simuliert.local","Develop":true}]}""")!;
        var session = new SessionService(new SimulatedIdentityProvider(() => "developer"), config);
        session.EnableDevelopmentMode();

        Assert.True(await session.SignInAsync());
        Assert.False(Assert.Single(config.Users).Manage);
        Assert.True(session.CanManage);
        Assert.True(session.CanDevelop);
    }

    [Fact]
    public async Task Local_manager_can_save_settings_and_station_config_but_cannot_edit_access_or_restore_it()
    {
        var directory = Directory.CreateTempSubdirectory("tu-manage-");
        string PathFor(string name) => Path.Combine(directory.FullName, name);
        try
        {
            var access = new AccessConfigStore(PathFor("zugang.json"));
            var stationStore = new StationSetupStore(PathFor("pruefplaetze.json"));
            var settings = new SettingsStore(PathFor("einstellungen.json"), PlainSecretProtector.Instance);
            var config = new AccessConfig
            {
                AllowLocalPasswords = true,
                Users = [new() { Upn = Manager, Manage = true, PasswordHash = LocalPasswordHash.Create(Password) }]
            };
            access.Save(config);
            config = access.Load()!;
            var provider = new LocalPasswordIdentityProvider(config, () => (Manager, Password));
            var session = new SessionService(provider, config);
            var identity = new IdentityServices(session, access, _ => provider);
            var ui = new QueueDispatcher();
            await using var services = new ResultServices(new AppSettings { ResultFolder = PathFor("results") },
                new NoDatabaseProviders(), PathFor("outbox"), PathFor("database"), recoveryFolder: PathFor("recovery"));
            var shell = new ShellViewModel(ui, identity: identity, services: services, settingsStore: settings, stationStore: stationStore);

            await shell.LoginAsync(false);
            ui.Pump();
            Assert.True(shell.CanManage);
            Assert.False(shell.IsDeveloper);
            Assert.False(shell.CanRelease);
            Assert.Equal(ShellPage.Stations, shell.Page);
            Assert.Equal("VERWALTUNGSANSICHT", shell.ViewTitle);
            Assert.Contains("Verwalten", shell.UserDetail);
            foreach (var page in new[] { ShellPage.Settings, ShellPage.Notifications, ShellPage.System })
                Assert.True(shell.NavigateCommand.CanExecute(page));
            foreach (var page in new[] { ShellPage.Editor, ShellPage.Debug, ShellPage.Library, ShellPage.Access, ShellPage.Releases, ShellPage.Python, ShellPage.Selection })
                Assert.False(shell.NavigateCommand.CanExecute(page));

            var settingsPage = shell.SettingsPage!;
            settingsPage.PythonExecutable = "unapproved-python";
            settingsPage.Save();
            Assert.Contains("Entwickeln", settingsPage.SaveStatus);
            settingsPage.Revert();
            settingsPage.CppCompiler = "unapproved-cpp";
            settingsPage.Save();
            Assert.Contains("Entwickeln", settingsPage.SaveStatus);
            settingsPage.Revert();
            settingsPage.CppCacheDirectory = "unapproved-cache";
            settingsPage.Save();
            Assert.Contains("Entwickeln", settingsPage.SaveStatus);
            Assert.False(File.Exists(settings.Path));
            settingsPage.Revert();
            settingsPage.Save();
            Assert.True(File.Exists(settings.Path), settingsPage.SaveStatus);
            shell.StationsPage.Save();
            Assert.True(File.Exists(stationStore.Path), shell.StationsPage.Error);
            Assert.False(shell.AccessPage.Save());
            Assert.False(shell.AccessPage.SaveCommand.CanExecute(null));

            var managerZip = PathFor("manager.zip");
            shell.SystemPage.Backup(managerZip);
            Assert.DoesNotContain(ConfigBackup.Inspect(managerZip).Manifest.Files, file => file.Name == "zugang.json");

            var privilegedZip = PathFor("privileged.zip");
            var originalAccess = File.ReadAllText(access.Path);
            var otherAccess = PathFor("other-access.json");
            new AccessConfigStore(otherAccess).Save(new AccessConfig { Users = [new() { Upn = Manager, Develop = true }] });
            var saved = settings.Load();
            saved.PythonExecutable = "existing-python";
            saved.CppCompiler = "existing-cpp";
            saved.CppCacheDirectory = "existing-cache";
            settings.Save(saved);

            var otherSettings = PathFor("other-settings.json");
            new SettingsStore(otherSettings, PlainSecretProtector.Instance).Save(new AppSettings
            {
                ResultFolder = PathFor("restored-results"), PythonExecutable = "unapproved-python",
                CppCompiler = "unapproved-cpp", CppCacheDirectory = "unapproved-cache"
            });
            ConfigBackup.Create(privilegedZip, [
                new("zugang.json", otherAccess, "Zugang"),
                new("pruefplaetze.json", stationStore.Path, "Prüfplätze"),
                new("einstellungen.json", otherSettings, "Einstellungen")
            ], DateTimeOffset.UtcNow);
            shell.SystemPage.InspectBackup(privilegedZip);
            Assert.False(shell.SystemPage.BackupFiles.Single(row => row.Name == "zugang.json").CanRestore);
            Assert.True(shell.SystemPage.BackupFiles.Single(row => row.Name == "pruefplaetze.json").CanRestore);
            shell.SystemPage.Restore();
            Assert.Contains("pruefplaetze.json", shell.SystemPage.BackupStatus);
            Assert.Equal(originalAccess, File.ReadAllText(access.Path));
            var restored = settings.Load();
            Assert.Equal(PathFor("restored-results"), restored.ResultFolder);
            Assert.Equal(("existing-python", "existing-cpp", "existing-cache"),
                (restored.PythonExecutable, restored.CppCompiler, restored.CppCacheDirectory));

            // The loaded runtime can lag behind the settings on disk until restart.
            settingsPage.ResultFolder = PathFor("manager-results");
            settingsPage.Save();
            var resaved = settings.Load();
            Assert.Equal(PathFor("manager-results"), resaved.ResultFolder);
            Assert.Equal(("existing-python", "existing-cpp", "existing-cache"),
                (resaved.PythonExecutable, resaved.CppCompiler, resaved.CppCacheDirectory));

            await shell.LogoutAsync();
            ui.Pump();
            Assert.False(shell.SystemPage.RestoreCommand.CanExecute(null));
            Assert.False(shell.CanManage);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class EntraProvider(UserIdentity user) : IIdentityProvider
    {
        public string Name => "Entra-Test";
        public Task<UserIdentity> SignInAsync(bool selectAccount, CancellationToken cancellationToken) => Task.FromResult(user);
        public Task SignOutAsync(UserIdentity identity, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoDatabaseProviders : IDatabaseProviderRegistry
    {
        public System.Data.Common.DbProviderFactory? Factory(DatabaseProvider provider) => null;
    }
}
