using System.Reflection;
using System.Security.Claims;
using Microsoft.Identity.Client;
using Testumgebung.Identity;
using Xunit;

namespace Testumgebung.Desktop.UiTests;

public sealed class MsalIdentityContractTests
{
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
        Assert.Contains("WAM", provider.Name);
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
