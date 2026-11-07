using System.Reflection;
using System.Security.Claims;
using Microsoft.Identity.Client;
using Testumgebung.Identity;
using Xunit;

namespace Testumgebung.Desktop.UiTests;

public sealed class MsalIdentityContractTests
{
    [Fact]
    public void System_browser_provider_can_be_constructed_without_a_native_broker_or_parent_window()
    {
        var provider = new MsalIdentityProvider("11111111-2222-3333-4444-555555555555", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        Assert.Contains("Systembrowser", provider.Name);
    }

    [Theory]
    [InlineData(null, "/", 80)]
    [InlineData("http://localhost:5001/Anmeldung", "/Anmeldung", 5001)]
    [InlineData("http://localhost/Anmeldung/", "/Anmeldung/", 0)]
    public void Application_uses_the_configured_path_and_port(string? configuredUri, string path, int port)
    {
        var app = MsalIdentityProvider.CreateApplication("11111111-2222-3333-4444-555555555555",
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", configuredUri);
        var redirect = new Uri(app.AppConfig.RedirectUri);
        Assert.Equal(path, redirect.AbsolutePath);
        if (port == 0) Assert.InRange(redirect.Port, 1024, 65535);
        else Assert.Equal(port, redirect.Port);
    }

    [Theory]
    [InlineData("https://localhost:5001/Anmeldung")]
    [InlineData("http://localhost.example.test:5001/Anmeldung")]
    [InlineData("http://localhost:5001/Anmeldung?x=y")]
    public async Task Invalid_redirect_is_reported_at_login_without_opening_a_browser(string uri)
    {
        var provider = new MsalIdentityProvider("11111111-2222-3333-4444-555555555555",
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", uri);
        var error = await Assert.ThrowsAsync<SignInException>(() => provider.SignInAsync(true, default));
        Assert.Contains("Umleitungs-URI ungültig", error.Message);
    }

    public class AccountPeer : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        { "get_Username" => "worker@example.test", "get_Environment" => "fixture", "get_HomeAccountId" => new AccountId("id", "object", "tenant"), _ => throw new NotSupportedException(method.Name) };
    }
    [Theory]
    [InlineData("none", false)]
    [InlineData("empty", true)]
    [InlineData("name", false)]
    [InlineData("roles", true)]
    public async Task Confirmed_identity_preserves_claims_and_always_requests_account_selection(string claims, bool selectAccount)
    {
        var account = DispatchProxy.Create<IAccount, AccountPeer>();
        var principal = claims == "none" ? null : new ClaimsPrincipal(new ClaimsIdentity(claims == "name" ? [new Claim("name", "Worker Name")] : claims == "roles" ? [new Claim("roles", "Testumgebung.Entwickeln"), new Claim("roles", "Testumgebung.Freigeben")] : []));
        var result = new AuthenticationResult("fixture-token", false, "object", DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(1), "tenant", account, null, ["User.Read"], Guid.Empty, claimsPrincipal: principal);
        var removed = new List<IAccount>(); using var cancellation = new CancellationTokenSource(); var count = 0;
        var provider = new MsalIdentityProvider((scopes, prompt, token) => { count++; Assert.Equal(new[] { "User.Read" }, scopes); Assert.Equal(Prompt.SelectAccount, prompt); Assert.Equal(cancellation.Token, token); return Task.FromResult(result); }, removedAccount => { removed.Add(removedAccount); return Task.CompletedTask; });
        Assert.Contains("Systembrowser", provider.Name);
        var identity = await provider.SignInAsync(selectAccount, cancellation.Token);
        Assert.Equal(1, count); Assert.Equal("worker@example.test", identity.Upn); Assert.Equal(claims == "name" ? "Worker Name" : identity.Upn, identity.DisplayName);
        Assert.True(identity.IsConfirmed); Assert.Equal("tenant", identity.TenantId); Assert.Equal("object", identity.ObjectId);
        Assert.Equal(claims == "roles" ? new[] { "Testumgebung.Entwickeln", "Testumgebung.Freigeben" } : [], identity.Roles);
        await provider.SignOutAsync(identity, default); Assert.Same(account, Assert.Single(removed));
        await provider.SignOutAsync(identity, default); Assert.Single(removed);
    }
    [Theory]
    [InlineData("cancel")]
    [InlineData("client")]
    [InlineData("service")]
    [InlineData("token")]
    public async Task Sign_in_distinguishes_user_cancellation_and_provider_errors(string failure)
    {
        Exception exception = failure switch { "cancel" => new MsalClientException(MsalError.AuthenticationCanceledError, "cancelled"), "client" => new MsalClientException("fixture", "client error"), "service" => new MsalServiceException("fixture", "service error"), _ => new OperationCanceledException() };
        var provider = new MsalIdentityProvider((_, _, _) => Task.FromException<AuthenticationResult>(exception), _ => throw new InvalidOperationException());
        if (failure == "cancel") await Assert.ThrowsAsync<SignInCanceledException>(() => provider.SignInAsync(true, default));
        else if (failure == "token") await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SignInAsync(true, default));
        else { var error = await Assert.ThrowsAsync<SignInException>(() => provider.SignInAsync(true, default)); Assert.Same(exception, error.InnerException); Assert.Contains("Entra-Anmeldung fehlgeschlagen", error.Message); }
        await provider.SignOutAsync(new UserIdentity("test", "test", "test", true), default);
    }
    [Fact]
    public async Task Browser_login_explains_when_entra_expects_confidential_client_credentials()
    {
        var failure = new MsalServiceException("invalid_client", "AADSTS7000218: fixture server response");
        var provider = new MsalIdentityProvider(
            (_, _, _) => Task.FromException<AuthenticationResult>(failure),
            _ => throw new InvalidOperationException("Failed login cannot cache an account."));

        var error = await Assert.ThrowsAsync<SignInException>(() => provider.SignInAsync(true, default));
        Assert.Contains("AADSTS7000218", error.Message);
        Assert.Contains("vertraulicher Anmeldedienst", error.Message);
        Assert.DoesNotContain("fixture server response", error.Message);
        await provider.SignOutAsync(new UserIdentity("test", "test", "test", true), default);
    }

    [Fact]
    public async Task Sign_out_clears_account_even_when_provider_fails()
    {
        var account = DispatchProxy.Create<IAccount, AccountPeer>(); var count = 0;
        var result = new AuthenticationResult("fixture-token", false, "object", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "tenant", account, null, ["User.Read"], Guid.Empty);
        var provider = new MsalIdentityProvider((_, _, _) => Task.FromResult(result), _ => { count++; throw new MsalClientException("fixture", "remove failed"); });
        var identity = await provider.SignInAsync(true, default);
        var error = await Assert.ThrowsAsync<SignInException>(() => provider.SignOutAsync(identity, default)); Assert.Contains("remove failed", error.Message);
        await provider.SignOutAsync(identity, default); Assert.Equal(1, count);
    }
}
