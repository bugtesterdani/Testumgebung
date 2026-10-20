using Testumgebung.Identity;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class AccessLockoutTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tu-lockout-" + Guid.NewGuid().ToString("N"));
    private string _name = "M. Weber";
    private AccessConfigStore Store => new(Path.Combine(_directory, "zugang.json"));
    private IIdentityProvider Provider(AccessConfig? _) => new SimulatedIdentityProvider(() => _name);
    private async Task<(SessionService Session, AccessSettingsViewModel Page)> Initial()
    {
        var session = new SessionService(Provider(null), null, allowDevelopmentMode: true);
        Assert.True(await session.SignInAsync());
        return (session, new AccessSettingsViewModel(new IdentityServices(session, Store, Provider)));
    }
    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tu-lockout-"));
        Assert.StartsWith(expected, Path.GetFullPath(_directory));
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
    [Fact]
    public async Task Access_page_created_before_login_seeds_the_current_developer_on_sign_in()
    {
        var session = new SessionService(Provider(null), null, allowDevelopmentMode: true);
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, identity: new IdentityServices(session, Store, Provider));
        Assert.Empty(shell.AccessPage.Users);
        await shell.LoginAsync(false); ui.Pump();
        Assert.Equal("m.weber@simuliert.local", Assert.Single(shell.AccessPage.Users).Upn);
        shell.AccessPage.Users.Add(new RightsEditRow { Upn = "Tester", Develop = true });
        Assert.True(shell.AccessPage.Save()); ui.Pump(); Assert.True(session.CanDevelop);
    }
    [Fact]
    public async Task First_user_setup_preserves_current_developer_and_survives_normal_restart()
    {
        var (session, page) = await Initial();
        Assert.Equal("m.weber@simuliert.local", Assert.Single(page.Users).Upn);
        page.Users.Add(new RightsEditRow { Upn = "Tester", Develop = true });
        Assert.True(page.Save(), page.Status);
        Assert.True(session.CanDevelop); Assert.True(session.IsSignedIn);
        var config = Store.Load()!;
        Assert.True(config.SupportsLocalDevelopment);
        Assert.Contains(config.Users, u => u.Upn == "tester@simuliert.local");
        _name = "Tester";
        var restarted = new SessionService(Provider(config), config);
        Assert.False(await restarted.SignInAsync());
        Assert.True(restarted.CanEnableDevelopmentMode);
        restarted.EnableDevelopmentMode();
        Assert.True(await restarted.SignInAsync()); Assert.True(restarted.CanDevelop); Assert.False(restarted.CanRelease);
        Assert.False(restarted.User!.IsConfirmed);
        _name = "Unknown";
        Assert.False(await restarted.SignInAsync(selectAccount: true));
        Assert.False(restarted.IsSignedIn); Assert.False(restarted.CanDevelop);
    }
    [Fact]
    public async Task Losing_own_settings_access_does_not_write_or_sign_out()
    {
        var (session, page) = await Initial();
        page.Users.Clear(); page.Users.Add(new RightsEditRow { Upn = "Tester", Develop = true });
        Assert.False(page.Save()); Assert.False(Store.Exists); Assert.True(session.IsSignedIn);
        Assert.Contains("Zugang zu den Einstellungen verlieren", page.Status);
        page.Users.Add(new RightsEditRow { Upn = session.User!.Upn, Develop = true });
        Assert.True(page.Save()); var before = File.ReadAllText(Store.Path);
        page.Users.Single(u => u.Upn == session.User.Upn).Develop = false;
        Assert.False(page.Save()); Assert.Equal(before, File.ReadAllText(Store.Path)); Assert.True(session.CanDevelop);
    }
    [Fact]
    public async Task Another_developer_can_remove_the_initial_simulation_name()
    {
        var (session, page) = await Initial();
        page.Users.Add(new RightsEditRow { Upn = "Tester", Develop = true }); Assert.True(page.Save());
        _name = "Tester"; Assert.True(await session.SignInAsync(selectAccount: true));
        page.Load(); page.Users.Remove(page.Users.Single(u => u.Upn == "m.weber@simuliert.local"));
        Assert.True(page.Save()); Assert.True(session.CanDevelop);
        Assert.DoesNotContain(Store.Load()!.Users, u => u.Upn == "m.weber@simuliert.local");
    }
    [Fact]
    public async Task Normalized_duplicate_names_are_rejected_before_writing()
    {
        var (session, page) = await Initial();
        page.Users.Add(new RightsEditRow { Upn = "Tester", Develop = true });
        page.Users.Add(new RightsEditRow { Upn = "TESTER@simuliert.local", Release = true });
        Assert.False(page.Save()); Assert.False(Store.Exists); Assert.True(session.CanDevelop);
        Assert.Contains("doppelt", page.Status);
    }
    [Theory]
    [InlineData("@@")]
    [InlineData("tester@")]
    [InlineData("Name <tester@example.test>")]
    [InlineData("!!!")]
    public async Task Invalid_normalized_or_display_names_are_not_saved(string name)
    {
        var (session, page) = await Initial();
        page.Users.Add(new RightsEditRow { Upn = name, Develop = true });
        Assert.False(page.Save()); Assert.False(Store.Exists); Assert.True(session.CanDevelop);
        Assert.Contains("Ungültiger Anmeldename", page.Status);
    }
    [Theory]
    [InlineData("legacy")]
    [InlineData("broken")]
    [InlineData("entra")]
    [InlineData("partial")]
    [InlineData("no-developer")]
    [InlineData("roles")]
    public void Invalid_or_production_configuration_does_not_offer_local_development(string mode)
    {
        var config = new AccessConfig { AllowLocalDevelopment = true, Users = [new RightsEntry { Upn = "tester@simuliert.local", Develop = true }] };
        switch (mode)
        {
            case "legacy": config.AllowLocalDevelopment = false; break;
            case "broken": config.LoadError = "fixture"; break;
            case "entra": config.ClientId = "client"; config.TenantId = "tenant"; break;
            case "partial": config.ClientId = "client"; break;
            case "no-developer": config.Users.Clear(); break;
            case "roles": config.RightsSource = "AppRollen"; break;
        }
        var session = new SessionService(Provider(config), config);
        Assert.False(session.CanEnableDevelopmentMode);
        Assert.Throws<InvalidOperationException>(session.EnableDevelopmentMode);
    }
    [Fact]
    public async Task Legacy_local_configuration_can_be_migrated_only_in_explicit_development()
    {
        var legacy = new AccessConfig { Users = [new RightsEntry { Upn = "tester@simuliert.local", Develop = true }] };
        Store.Save(legacy); _name = "Tester";
        var session = new SessionService(Provider(legacy), Store.Load(), allowDevelopmentMode: true);
        Assert.True(await session.SignInAsync());
        var page = new AccessSettingsViewModel(new IdentityServices(session, Store, Provider));
        Assert.True(page.Save()); Assert.True(Store.Load()!.SupportsLocalDevelopment);
    }
}
