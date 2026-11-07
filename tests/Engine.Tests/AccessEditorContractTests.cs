using Testumgebung.Identity;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class AccessEditorContractTests
{
    public static IEnumerable<object[]> Configurations()
    {
        foreach (var source in new[] { "Lokal", "AppRollen", "Beide" })
            foreach (var client in new[] { "", "client" })
                foreach (var tenant in new[] { "", "tenant" })
                    foreach (var user in new[] { "", "user@example.test" })
                        foreach (var roles in new[] { false, true }) yield return [source, client, tenant, user, roles];
    }

    [Theory, MemberData(nameof(Configurations))]
    public void Access_editor_validates_before_saving_and_reloads_valid_configuration(string source, string client, string tenant, string user, bool roles)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new AccessConfigStore(Path.Combine(directory, "access.json"));
            var provider = new SimulatedIdentityProvider(() => "Tester");
            var identity = new IdentityServices(new SessionService(provider, null), store, _ => provider);
            var vm = new AccessSettingsViewModel(identity);
            Assert.Equal(store.Path, vm.Path);
            Assert.Contains("Produktivbetrieb", vm.ModeText);
            vm.ClientId = " " + client + " "; vm.TenantId = " " + tenant + " ";
            var callback = client.Length > 0 ? "http://localhost:5001/Anmeldung" : null;
            vm.EntraRedirectUri = callback ?? "";
            vm.RightsSource = source; vm.InactivityMinutes = -1;
            Assert.Equal(0, vm.InactivityMinutes);
            vm.InactivityMinutes = 7;
            vm.DevelopRole = roles ? " Develop " : ""; vm.ReleaseRole = roles ? " Release " : "";
            vm.AddCommand.Execute(null);
            Assert.Single(vm.Users);
            var row = vm.Users[0]; row.Upn = user; row.Name = roles ? "Tester" : "";
            row.Develop = roles; row.Release = !roles;
            var valid = user.Length > 0 && (source == "Lokal" || client.Length > 0) && (client.Length == 0) == (tenant.Length == 0);
            Assert.Equal(valid, vm.Save());
            Assert.NotNull(vm.Status);
            Assert.Equal(valid, store.Exists);
            if (valid)
            {
                var loaded = Assert.IsType<AccessConfig>(store.Load());
                Assert.Equal(source, loaded.RightsSource); Assert.Equal(7, loaded.InactivityMinutes);
                Assert.Equal(client.Length == 0 ? null : client, loaded.ClientId);
                Assert.Equal(tenant.Length == 0 ? null : tenant, loaded.TenantId);
                Assert.Equal(callback, loaded.EntraRedirectUri);
                vm.EntraRedirectUri = "https://login.example.test/Anmeldung";
                Assert.False(vm.Save());
                Assert.Equal(callback, store.Load()!.EntraRedirectUri);
                Assert.Equal(roles ? "Develop" : "Testumgebung.Entwickeln", loaded.DevelopRole);
                Assert.Equal(roles ? "Release" : "Testumgebung.Freigeben", loaded.ReleaseRole);
                Assert.Equal(user, Assert.Single(loaded.Users).Upn);
                Assert.Equal(roles, loaded.Users[0].Develop); Assert.Equal(!roles, loaded.Users[0].Release);
                vm.ClientId = "discard"; vm.ReloadCommand.Execute(null);
                Assert.Equal(client, vm.ClientId); Assert.Equal(tenant, vm.TenantId);
                Assert.Equal(callback ?? "", vm.EntraRedirectUri);
                Assert.Single(vm.Users);
            }
            vm.RemoveCommand.Execute(new object()); Assert.Single(vm.Users);
            vm.RemoveCommand.Execute(vm.Users[0]); Assert.Empty(vm.Users);
            vm.ClientId = null!; vm.TenantId = null!; vm.RightsSource = " ";
            Assert.Equal("Lokal", vm.RightsSource); Assert.True(vm.Save());
            Assert.Empty(store.Load()!.Users);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Broker_settings_require_a_pinned_local_https_callback_and_survive_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), "tu-broker-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new AccessConfigStore(path);
        var provider = new SimulatedIdentityProvider(() => "Tester");
        var services = new IdentityServices(new SessionService(provider, null), store, _ => provider);
        var vm = new AccessSettingsViewModel(services)
        {
            ClientId = "11111111-2222-3333-4444-555555555555",
            TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            EntraRedirectUri = "https://localhost:5001/Anmeldung"
        };
        vm.Users.Add(new RightsEditRow { Upn = "worker@example.test", Develop = true });
        try
        {
            Assert.False(vm.Save());
            vm.EntraBrokerTlsSha256 = "invalid";
            Assert.False(vm.Save());
            vm.EntraBrokerTlsSha256 = new string('A', 64);
            vm.EntraRedirectUri = "https://localhost:5002/Anmeldung";
            Assert.False(vm.Save());
            vm.EntraRedirectUri = "https://localhost:5001/Anmeldung";
            Assert.True(vm.Save(), vm.Status);
            Assert.Contains("lokalen Entra-Dienst", vm.ModeText);
            vm.EntraBrokerTlsSha256 = "";
            vm.Load();
            Assert.Equal(new string('A', 64), vm.EntraBrokerTlsSha256);
            Assert.Equal("https://localhost:5001/Anmeldung", vm.EntraRedirectUri);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Simulated_access_has_no_store_and_cannot_save()
    {
        var vm = new AccessSettingsViewModel(IdentityServices.Simulated(() => "Tester"));
        Assert.False(vm.Save()); Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Contains("Entwicklungsmodus", vm.ModeText);
        Assert.Contains("keine Zugangsdatei", vm.Path);
    }

    [Fact]
    public void Failed_file_replacement_reports_error_without_claiming_success()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, "access.json");
            var store = new AccessConfigStore(target);
            var provider = new SimulatedIdentityProvider(() => "Tester");
            var vm = new AccessSettingsViewModel(new IdentityServices(new SessionService(provider, null), store, _ => provider));
            Directory.CreateDirectory(target);
            Assert.False(vm.Save()); Assert.NotNull(vm.Status);
            Assert.DoesNotContain("Gespeichert", vm.Status);
        }
        finally { Directory.Delete(directory, true); }
    }
}
