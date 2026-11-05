using Testumgebung.Contracts;
using Testumgebung.Identity;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class LocalPasswordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tu-password-" + Guid.NewGuid().ToString("N"));
    private static readonly string Hash = LocalPasswordHash.Create("test-password");
    private string _user = "Tester", _password = "test-password";
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private AccessConfig Config() => new() { AllowLocalPasswords = true, Users = [new() { Upn = "tester@simuliert.local", Name = "Tester", Develop = true, Release = true, PasswordHash = Hash }] };
    private IIdentityProvider Provider(AccessConfig? c) => c is { UsesLocalPasswords: true } ? new LocalPasswordIdentityProvider(c, () => (_user, _password)) : new SimulatedIdentityProvider(() => _user);

    [Fact]
    public void Hashes_are_salted_versioned_and_password_comparison_is_exact()
    {
        var first = LocalPasswordHash.Create(" test "); var second = LocalPasswordHash.Create(" test ");
        Assert.NotEqual(first, second); Assert.StartsWith("pbkdf2-sha256$600000$", first);
        Assert.True(LocalPasswordHash.Verify(" test ", first)); Assert.False(LocalPasswordHash.Verify("test", first));
        Assert.False(LocalPasswordHash.Verify("wrong", Hash)); Assert.False(LocalPasswordHash.Verify("", Hash));
        Assert.False(LocalPasswordHash.Verify(new string('a', 1025), Hash));
        Assert.Throws<ArgumentException>(() => LocalPasswordHash.Create("")); Assert.Throws<ArgumentException>(() => LocalPasswordHash.Create(new string('a', 1025)));
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("sha256$1$a$b")]
    [InlineData("pbkdf2-sha256$1$a$b")] [InlineData("pbkdf2-sha256$999999999$a$b")]
    [InlineData("pbkdf2-sha256$600000$bad!$bad!")]
    [InlineData("pbkdf2-sha256$600000$YQ==$YQ==")]
    public void Invalid_or_unbounded_hash_formats_are_rejected(string? stored)
    { Assert.False(LocalPasswordHash.IsValid(stored)); Assert.False(LocalPasswordHash.Verify("password", stored)); }

    [Fact]
    public async Task Password_login_survives_restart_has_local_rights_and_rejects_wrong_or_unknown_credentials()
    {
        var config = Config(); var store = new AccessConfigStore(Path.Combine(_root, "zugang.json")); store.Save(config);
        var json = File.ReadAllText(store.Path); Assert.DoesNotContain("test-password", json); Assert.Contains("pbkdf2-sha256", json);
        var loaded = store.Load()!; var session = new SessionService(Provider(loaded), loaded);
        Assert.False(session.CanEnableDevelopmentMode); Assert.False(session.IsDevelopmentMode);
        Assert.True(await session.SignInAsync()); Assert.True(session.CanDevelop); Assert.True(session.CanRelease); Assert.False(session.User!.IsConfirmed);
        Assert.Equal(LocalPasswordIdentityProvider.Source, session.User.Source);
        await session.SignOutAsync(); _password = "wrong"; Assert.False(await session.SignInAsync()); Assert.Null(session.User);
        var message = session.LastError; _user = "unknown"; Assert.False(await session.SignInAsync()); Assert.Equal(message, session.LastError);
        _user = "TESTER@simuliert.local"; _password = "test-password"; Assert.True(await session.SignInAsync());
        var changed = Config(); changed.Users[0].PasswordHash = LocalPasswordHash.Create("new-password"); session.Reconfigure(Provider(changed), changed);
        Assert.Null(session.User); _password = "new-password"; Assert.True(await session.SignInAsync());
        var shell = new ShellViewModel(ImmediateDispatcher.Instance, identity: new IdentityServices(session, store, Provider));
        Assert.True(shell.IsLocalPasswordLogin); Assert.True(shell.ShowsLoginName); Assert.Contains("lokaler Passwortzugang", shell.UserDetail);
        shell.SwitchAccountCommand.Execute(null); Assert.False(session.IsSignedIn);
    }
    [Fact]
    public async Task Name_only_simulation_cannot_bypass_enabled_passwords_even_in_development_mode()
    {
        var config = Config(); var session = new SessionService(new SimulatedIdentityProvider(() => "Tester"), config, allowDevelopmentMode: true);
        Assert.False(session.IsDevelopmentMode); Assert.False(session.CanEnableDevelopmentMode); Assert.False(await session.SignInAsync()); Assert.Contains("Passwort", session.LastError);
        config.ClientId = "client"; config.TenantId = "tenant"; Assert.False(config.UsesLocalPasswords);
        var entraOnly = new SessionService(new LocalPasswordIdentityProvider(config, () => (_user, _password)), config);
        Assert.False(await entraOnly.SignInAsync()); Assert.Null(entraOnly.User);
    }
    [Fact]
    public async Task Voluntary_worker_login_does_not_grant_editor_or_release_rights()
    {
        var config = Config(); config.Users[0].Develop = config.Users[0].Release = false;
        var session = new SessionService(Provider(config), config); Assert.True(await session.SignInAsync());
        Assert.True(session.IsSignedIn); Assert.False(session.CanDevelop); Assert.False(session.CanRelease);
        session.Reconfigure(Provider(config), config); Assert.True(session.IsSignedIn);
        config.InactivityMinutes = 1;
        await session.SignOutAsync(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SignInAsync(cancellationToken: cancellation.Token));
    }
    [Fact]
    public async Task First_setup_requires_passwords_preserves_developer_access_and_does_not_store_plaintext()
    {
        var store = new AccessConfigStore(Path.Combine(_root, "zugang.json"));
        var session = new SessionService(Provider(null), null, allowDevelopmentMode: true); Assert.True(await session.SignInAsync());
        var services = new IdentityServices(session, store, Provider); var page = new AccessSettingsViewModel(services);
        page.AllowLocalPasswords = true; Assert.False(page.Save()); Assert.Contains("Passwort", page.Status);
        var row = Assert.Single(page.Users); Assert.Equal("nicht gesetzt", row.PasswordStatus); row.SetPassword("test-password"); Assert.Equal("gesetzt", row.PasswordStatus);
        Assert.True(page.Save(), page.Status); Assert.Null(session.User); Assert.True(store.Load()!.UsesLocalPasswords);
        Assert.False(store.Load()!.AllowLocalDevelopment); Assert.True(await session.SignInAsync());
        page.Load(); Assert.Equal(Hash.Length, Assert.Single(page.Users).PasswordHash!.Length);
        page.Users[0].Develop = false; Assert.False(page.Save()); Assert.Contains("Entwickeln", page.Status);
        page.Users[0].Develop = true; page.Users[0].Upn = "other"; Assert.False(page.Save()); Assert.Contains("Zugang", page.Status);
        page.Users[0].Upn = "Tester"; Assert.True(page.Save(), page.Status);
        page.AllowLocalPasswords = false; Assert.True(page.Save()); Assert.True(store.Load()!.SupportsLocalDevelopment); Assert.Null(session.User); // explicitly disabling restores the opt-in development path
    }
}
