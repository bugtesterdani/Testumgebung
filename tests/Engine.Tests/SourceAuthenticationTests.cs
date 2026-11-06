using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Testumgebung.Persistence.Programs;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.Engine.Tests;

public sealed class SourceAuthenticationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tu-source-auth-" + Guid.NewGuid().ToString("N"));
    private static ProgramSource Source(string provider = "Git", string repository = "https://example.invalid/repo") => new("source", "Program", provider, repository, "main", "project.json");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Protector : ISecretProtector
    {
        public bool Unreadable { get; set; }
        public string Protect(string plain) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        public string? Unprotect(string stored) => Unreadable ? null : Encoding.UTF8.GetString(Convert.FromBase64String(stored));
    }
    [Fact]
    public async Task Real_client_process_receives_secrets_privately_and_diagnostics_are_redacted()
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "fixtures", "process", "ProcessProbe.exe");
        var source = Source();
        var git = new GitProgramSource(probe, credentials: _ => new(SourceLoginMode.GitAccessToken, "user", "redact-fixture"));
        var failure = await Assert.ThrowsAsync<IOException>(() => git.ExportAsync(source, null));
        Assert.Contains("[ausgeblendet]", failure.Message); Assert.DoesNotContain("redact-fixture", failure.Message);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("user:redact-fixture")), failure.Message);
        git = new(probe, credentials: _ => new(SourceLoginMode.GitAccessToken, "user", "auth-failure"));
        var auth = await Assert.ThrowsAsync<SourceAuthenticationException>(() => git.ExportAsync(source, null));
        Assert.Equal(source, auth.ProgramSource);
        var svn = new SvnProgramSource(probe, credentials: _ => new(SourceLoginMode.SvnPassword, "user", "svn-fixture-secret"));
        using var snapshot = await svn.ExportAsync(Source("SVN"), null);
        Assert.Equal("42", snapshot.Version.Revision); Assert.Equal("fixture-uuid", snapshot.Version.RepositoryIdentity);
        Assert.Equal("fixture project", File.ReadAllText(snapshot.ProjectFile));
    }
    [Theory]
    [InlineData(SourceLoginMode.GitAccessToken)] [InlineData(SourceLoginMode.GitDeployToken)]
    public void Git_token_is_scoped_to_repository_child_environment_and_never_written_to_arguments(SourceLoginMode mode)
    {
        var source = Source(); var credentials = new SourceCredentials(mode, "deploy-user", "secret-token");
        var start = new ProcessStartInfo("git"); start.Environment["GIT_CONFIG_PARAMETERS"] = "untrusted inherited parameters";
        Assert.Null(SourceAuthentication.Configure(start, source, credentials));
        Assert.DoesNotContain("secret-token", string.Join(" ", start.ArgumentList));
        Assert.Equal("4", start.Environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("http.https://example.invalid/repo/.extraHeader", start.Environment["GIT_CONFIG_KEY_2"]);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("deploy-user:secret-token"));
        Assert.Equal("Authorization: Basic " + encoded, start.Environment["GIT_CONFIG_VALUE_2"]);
        Assert.Equal("", start.Environment["GIT_CONFIG_VALUE_0"]); Assert.Equal("false", start.Environment["GIT_CONFIG_VALUE_3"]);
        Assert.False(start.Environment.ContainsKey("GIT_CONFIG_PARAMETERS"));
        Assert.DoesNotContain("secret-token", credentials.ToString());
        Assert.DoesNotContain(encoded, credentials.Redact(encoded + " secret-token"));
        var special = new SourceCredentials(mode, "user", "secret value");
        Assert.Equal("[ausgeblendet]", special.Redact("secret%20value"));
    }
    [Fact]
    public void Svn_password_uses_stdin_and_explicit_methods_do_not_cache_or_fall_back()
    {
        var source = Source("SVN"); var start = new ProcessStartInfo("svn");
        Assert.Equal("secret", SourceAuthentication.Configure(start, source, new(SourceLoginMode.SvnPassword, "user", "secret")));
        Assert.Contains("--password-from-stdin", start.ArgumentList); Assert.Contains("--no-auth-cache", start.ArgumentList);
        Assert.DoesNotContain("secret", start.ArgumentList); Assert.Contains("servers:global:http-auth-types=basic;digest", start.ArgumentList);
        start = new("svn"); Assert.Null(SourceAuthentication.Configure(start, source, new(SourceLoginMode.SvnWindows)));
        Assert.Contains("servers:global:http-auth-types=negotiate", start.ArgumentList);
        Assert.DoesNotContain("--username", start.ArgumentList);
        start = new("git"); Assert.Null(SourceAuthentication.Configure(start, Source(), new(SourceLoginMode.External)));
        Assert.Empty(start.ArgumentList); Assert.Equal("unchanged", new SourceCredentials(SourceLoginMode.External).Redact("unchanged"));
    }
    [Theory]
    [InlineData("SVN", "https://example.invalid", SourceLoginMode.GitAccessToken, "u", "s")]
    [InlineData("Git", "https://example.invalid", SourceLoginMode.SvnWindows, "", "")]
    [InlineData("Other", "https://example.invalid", SourceLoginMode.SvnPassword, "u", "s")]
    [InlineData("Git", "ssh://example.invalid/repo", SourceLoginMode.GitDeployToken, "u", "s")]
    [InlineData("Git", "https://example.invalid", SourceLoginMode.GitAccessToken, "", "s")]
    [InlineData("Git", "https://example.invalid", SourceLoginMode.GitAccessToken, "u", "")]
    [InlineData("Git", "https://example.invalid", SourceLoginMode.GitAccessToken, "u:s", "s")]
    [InlineData("Git", "https://example.invalid", SourceLoginMode.GitAccessToken, "u", "s\n")]
    public void Invalid_modes_transports_and_credentials_are_rejected_before_launch(string provider, string url, SourceLoginMode mode, string user, string secret)
        => Assert.Throws<ArgumentException>(() => SourceAuthentication.Configure(new("client"), Source(provider, url), new(mode, user, secret)));
    [Theory]
    [InlineData("fatal: Authentication failed", true)] [InlineData("E170001: Authorization failed", true)]
    [InlineData("could not read Username: terminal prompts disabled", true)] [InlineData("connection refused", false)]
    [InlineData("branch missing", false)]
    public void Only_authentication_failures_trigger_a_credential_prompt(string message, bool expected)
        => Assert.Equal(expected, SourceAuthentication.IsAuthenticationFailure(message));
    [Fact]
    public void Profiles_are_local_protected_bound_to_source_and_can_be_session_only_or_removed()
    {
        var source = Source(); var credentials = new SourceCredentials(SourceLoginMode.GitAccessToken, "user", "secret-token");
        var file = Path.Combine(_root, "profiles.json"); var protector = new Protector();
        var store = new SourceCredentialStore(file, protector);
        Assert.Null(store.Get(source)); Assert.False(store.IsRemembered(source));
        store.Save(source, credentials, true); Assert.True(store.IsRemembered(source));
        Assert.DoesNotContain("secret-token", File.ReadAllText(file));
        Assert.Equal("secret-token", new SourceCredentialStore(file, protector).Get(source)!.Secret);
        Assert.Null(store.Get(source with { Repository = "https://other.invalid/repo" }));
        protector.Unreadable = true; Assert.Throws<IOException>(() => store.Get(source)); protector.Unreadable = false;
        store.Save(source, credentials, false); Assert.False(store.IsRemembered(source)); Assert.Same(credentials, store.Get(source));
        Assert.Null(new SourceCredentialStore(file, protector).Get(source));
        store.Remove(source); Assert.Null(store.Get(source));
        var memory = new SourceCredentialStore(); memory.Save(source, credentials, false); Assert.Same(credentials, memory.Get(source));
        Assert.Throws<IOException>(() => memory.Save(source, credentials, true)); memory.Remove(source); Assert.Null(memory.Get(source));
        File.WriteAllText(file, "null"); Assert.Throws<IOException>(() => store.Get(source));
        File.WriteAllText(file, "not json"); Assert.Throws<JsonException>(() => store.Get(source));
    }
}