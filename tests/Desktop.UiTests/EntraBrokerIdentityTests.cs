using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Testumgebung.Identity;
using Xunit;

namespace Testumgebung.Desktop.UiTests;

public sealed class EntraBrokerIdentityTests
{
    private const string Client = "11111111-2222-3333-4444-555555555555";
    private const string Tenant = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ObjectId = "00000000-1111-2222-3333-444444444444";
    private const string Callback = "https://localhost:5001/Anmeldung";
    private static readonly string BrowserKey = new('B', 64);
    private static readonly string ResultKey = new('C', 64);

    [Theory]
    [InlineData("ok")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-app")]
    [InlineData("failed")]
    [InlineData("unreachable")]
    [InlineData("cancel")]
    public async Task Desktop_binds_the_result_to_the_same_app_tenant_and_browser_attempt(string scenario)
    {
        var config = new AccessConfig
        {
            ClientId = Client, TenantId = Tenant, EntraRedirectUri = Callback, EntraBrokerTlsSha256 = new string('A', 64)
        };
        var paths = new List<string>();
        var opened = new List<Uri>();
        var polls = 0;
        using var cancellation = new CancellationTokenSource();
        var provider = new EntraBrokerIdentityProvider(config, () => new HttpClient(new FakeTransport(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            Assert.Equal(EntraBrokerProtocol.RequestVersion,
                Assert.Single(request.Headers.GetValues(EntraBrokerProtocol.RequestHeader)));
            if (path.EndsWith("/start", StringComparison.Ordinal))
            {
                if (scenario == "unreachable") throw new HttpRequestException();
                return Reply(new EntraBrokerAttempt(BrowserKey, ResultKey, Tenant, scenario == "wrong-app" ? ObjectId : Client, Callback));
            }
            Assert.Equal(ResultKey, request.Headers.Authorization!.Parameter);
            if (path.EndsWith("/cancel", StringComparison.Ordinal)) return new(HttpStatusCode.NoContent);
            Assert.EndsWith("/result", path);
            if (scenario == "cancel") cancellation.Cancel();
            if (scenario == "ok" && polls++ == 0) return Reply(new EntraBrokerResult("pending"));
            return Reply(new EntraBrokerResult(scenario == "failed" ? "failed" : "ok",
                "worker@example.test", "Worker", scenario == "wrong-tenant" ? ObjectId : Tenant, ObjectId,
                ["Testumgebung.Entwickeln"]));
        })) { BaseAddress = new Uri(EntraBrokerProtocol.Origin) }, opened.Add);

        if (scenario == "ok")
        {
            var user = await provider.SignInAsync(true, cancellation.Token);
            Assert.True(user.IsConfirmed);
            Assert.Equal(Tenant, user.TenantId);
            Assert.Equal(ObjectId, user.ObjectId);
            Assert.Contains("Testumgebung.Entwickeln", user.Roles!);
            Assert.Equal(3, paths.Count);
        }
        else if (scenario == "cancel")
        {
            await Assert.ThrowsAsync<SignInCanceledException>(() => provider.SignInAsync(true, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<SignInException>(() => provider.SignInAsync(true, cancellation.Token));
        }

        if (scenario is "wrong-app" or "unreachable") Assert.Empty(opened);
        else Assert.EndsWith("/browser/" + BrowserKey, Assert.Single(opened).AbsolutePath);
        if (scenario is not ("ok" or "unreachable")) Assert.EndsWith("/cancel", paths[^1]);
    }

    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class FakeTransport(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
