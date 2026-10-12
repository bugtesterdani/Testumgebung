using Testumgebung.Identity;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class AccessModeBoundaryTests
{
    [Theory]
    [InlineData("missing", false, "Zugangsdatei fehlt")]
    [InlineData("missing", true, "--development")]
    [InlineData("broken", false, "beschädigt")]
    [InlineData("entra", true, "Microsoft Entra ID")]
    [InlineData("local", true, "Rechte aus der Liste")]
    [InlineData("local", false, "Entra-App fehlt")]
    public void Access_page_explains_actual_authentication_configuration(string mode, bool development, string expected)
    {
        var provider = new SimulatedIdentityProvider(() => "tester");
        var config = mode switch { "missing" => null, "broken" => new AccessConfig { LoadError = "fixture" }, "entra" => new AccessConfig { ClientId = "client", TenantId = "tenant" }, _ => new AccessConfig() };
        var identity = new IdentityServices(new SessionService(provider, config, allowDevelopmentMode: development), null, _ => provider);
        var page = new AccessSettingsViewModel(identity); Assert.Contains(expected, page.ModeText); Assert.False(page.Save()); Assert.False(page.SaveCommand.CanExecute(null));
        page.AddCommand.Execute(null); Assert.Single(page.Users); Assert.True(page.Users[0].Develop);
        page.RemoveCommand.Execute("wrong parameter"); Assert.Single(page.Users); page.RemoveCommand.Execute(page.Users[0]); Assert.Empty(page.Users);
        page.ClientId = " client "; page.TenantId = " tenant "; page.DevelopRole = " develop "; page.ReleaseRole = " release "; page.InactivityMinutes = -1; page.RightsSource = " ";
        Assert.Equal("client", page.ClientId); Assert.Equal("tenant", page.TenantId); Assert.Equal("develop", page.DevelopRole); Assert.Equal("release", page.ReleaseRole); Assert.Equal(0, page.InactivityMinutes); Assert.Equal("Lokal", page.RightsSource);
        page.ReloadCommand.Execute(null); Assert.Equal("", page.ClientId); Assert.Equal("", page.TenantId); Assert.Contains("keine Zugangsdatei", page.Path);
    }
}
