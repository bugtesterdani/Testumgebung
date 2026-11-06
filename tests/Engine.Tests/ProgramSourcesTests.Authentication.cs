using Testumgebung.Persistence;
using Testumgebung.Persistence.Programs;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed partial class ProgramSourcesTests
{
    private sealed class AuthProbe(string root) : IProgramSourceProvider
    {
        public string Id => "Git";
        public int Calls { get; private set; }
        public int Failures { get; set; }
        public bool Wait { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SourceSnapshot> ExportAsync(ProgramSource source, string? revision, CancellationToken ct = default)
        {
            Calls++;
            if (Failures-- > 0) throw new SourceAuthenticationException(source, "Anmeldung fehlgeschlagen.");
            if (Wait) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "program.json"); new ProjectSerializer().Save(SampleProjects.AcPruefung(), file);
            return new(new(source, "fixture-revision", "fixture", "fixture"), file, directory);
        }
    }
    [Fact]
    public async Task Local_authentication_ui_preserves_rights_and_retries_only_once_after_an_explicit_prompt()
    {
        var (service, catalog, _, original) = await Context();
        var source = original with { Provider = "Git" };
        var state = await catalog.ReadAsync(); await catalog.SaveAsync(state with { Sources = [source] });
        await using var hub = new ReleaseHub(service);
        var identity = IdentityServices.Simulated(() => "tester"); await identity.Session.SignInAsync();
        var vm = new ProgramSourcesViewModel(hub, new ShellViewModel().Editor, identity.Session, Testumgebung.Presentation.Mvvm.ImmediateDispatcher.Instance, [], P("clients.json"));
        var store = new SourceCredentialStore(); vm.ConfigureAuthentication(store);
        await vm.RefreshAsync(); Assert.Equal(source, vm.Selected); Assert.Equal(3, vm.LoginChoices.Count);
        vm.LoginMode = SourceLoginMode.GitAccessToken; vm.LoginUsername = "user";
        Assert.True(vm.LoginNeedsSecret); Assert.False(vm.SaveAuthentication(""));
        Assert.True(vm.SaveAuthentication("fixture-token")); Assert.Equal("fixture-token", store.Get(source)!.Secret);
        Assert.True(vm.SaveAuthentication(""));
        vm.LoginMode = SourceLoginMode.GitDeployToken; Assert.False(vm.SaveAuthentication("")); Assert.True(vm.SaveAuthentication("deploy-token"));
        vm.RememberLogin = true; Assert.False(vm.SaveAuthentication("deploy-token")); vm.RememberLogin = false;
        vm.Selected = source with { Provider = "SVN" }; Assert.Contains(vm.LoginChoices, c => c.Mode == SourceLoginMode.SvnWindows);
        vm.LoginMode = SourceLoginMode.SvnWindows; Assert.False(vm.LoginNeedsSecret); Assert.True(vm.SaveAuthentication(""));
        vm.Selected = source;
        vm.RemoveAuthentication(); Assert.Null(store.Get(source));
        var probe = new AuthProbe(_root); service.SourceProviders = new([probe]);
        await vm.TestConnectionAsync(); Assert.Contains("Verbindung und Projektdatei geprüft", vm.Status); Assert.Empty(await service.ListAsync());
        probe.Failures = 1; vm.RequestAuthentication = () => Task.FromResult(false);
        await vm.TestConnectionAsync(); Assert.Contains("abgebrochen", vm.Status);
        var prompts = 0;
        vm.RequestAuthentication = () => { prompts++; Assert.True(vm.SaveAuthentication("")); return Task.FromResult(true); };
        probe.Failures = 1; await vm.TestConnectionAsync(); Assert.Equal(1, prompts); Assert.Contains("geprüft", vm.Status);
        probe.Failures = 2; await vm.TestConnectionAsync(); Assert.Equal(2, prompts); Assert.Contains("Anmeldung", vm.Status);
        probe.Failures = 0; probe.Wait = true;
        var pending = vm.TestConnectionAsync(); await probe.Entered.Task.Within();
        Assert.False(vm.SaveAuthentication("")); vm.RemoveAuthentication(); vm.CancelSourceOperation();
        await pending.Within(); Assert.Contains("abgebrochen", vm.Status);
        await identity.Session.SignOutAsync(); Assert.False(vm.SaveAuthentication("")); Assert.Contains("Berechtigung", vm.Status);
        vm.RemoveAuthentication(); await vm.TestConnectionAsync(); Assert.Contains("Berechtigung", vm.Status);
        await identity.Session.SignInAsync(); vm.Selected = null; Assert.False(vm.SaveAuthentication("")); await vm.TestConnectionAsync(); Assert.Contains("Quelle", vm.Status);
    }
}