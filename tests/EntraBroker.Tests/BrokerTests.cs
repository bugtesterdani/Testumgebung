using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testumgebung.Identity;
using Xunit;

namespace Testumgebung.EntraBroker.Tests;

public sealed class BrokerTests
{
    [Fact]
    public void Browser_key_cannot_retrieve_identity_and_result_is_consumed_once()
    {
        var store = new PendingSignIns(new Clock());
        var attempt = store.Create("tenant", "client", EntraBrokerProtocol.Origin)!;
        var identity = new EntraBrokerResult("ok", "worker@example.test");

        Assert.Null(store.Take(attempt.BrowserKey));
        Assert.False(store.Complete(attempt.BrowserKey, identity));
        Assert.True(store.Begin(attempt.BrowserKey));
        Assert.False(store.Begin(attempt.BrowserKey));
        Assert.Equal("pending", store.Take(attempt.ResultKey)!.Status);
        Assert.True(store.Complete(attempt.BrowserKey, identity));
        Assert.False(store.Complete(attempt.BrowserKey, new("failed")));
        Assert.Equal(identity, store.Take(attempt.ResultKey));
        Assert.Null(store.Take(attempt.ResultKey));
    }

    [Fact]
    public void Cancelled_and_expired_attempts_cannot_accept_a_callback()
    {
        var clock = new Clock();
        var store = new PendingSignIns(clock);
        var cancelled = store.Create("tenant", "client", EntraBrokerProtocol.Origin)!;
        Assert.True(store.Begin(cancelled.BrowserKey));
        store.Cancel(cancelled.ResultKey);
        Assert.False(store.Complete(cancelled.BrowserKey, new("ok")));
        Assert.Null(store.Take(cancelled.ResultKey));

        var expired = store.Create("tenant", "client", EntraBrokerProtocol.Origin)!;
        Assert.True(store.Begin(expired.BrowserKey));
        clock.Now += EntraBrokerProtocol.Lifetime;
        Assert.False(store.Complete(expired.BrowserKey, new("ok")));
        Assert.Null(store.Take(expired.ResultKey));
    }

    [Fact]
    public async Task App_certificate_assertion_is_signed_for_the_configured_token_endpoint()
    {
        const string client = "11111111-2222-3333-4444-555555555555";
        const string endpoint = "https://login.microsoftonline.com/example/oauth2/v2.0/token";
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Testumgebung Fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var assertion = BrokerCredentials.CreateAssertion(certificate, client, endpoint);
        var handler = new JsonWebTokenHandler();
        var parsed = handler.ReadJsonWebToken(assertion);
        Assert.Equal("PS256", parsed.Alg);
        Assert.Equal(client, parsed.Subject);
        Assert.Equal(Base64UrlEncoder.Encode(certificate.GetCertHash(HashAlgorithmName.SHA256)),
            parsed.GetHeaderValue<string>("x5t#S256"));
        var verified = await handler.ValidateTokenAsync(assertion, new TokenValidationParameters
        {
            ValidIssuer = client,
            ValidAudience = endpoint,
            IssuerSigningKey = new RsaSecurityKey(rsa),
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true
        });
        Assert.True(verified.IsValid, verified.Exception?.Message);
        var second = handler.ReadJsonWebToken(BrokerCredentials.CreateAssertion(certificate, client, endpoint));
        Assert.NotEqual(parsed.Id, second.Id);
    }

    [Theory]
    [InlineData("https://localhost:5001", true)]
    [InlineData("https://localhost:5001/Anmeldung", true)]
    [InlineData("https://localhost:5002/Anmeldung", false)]
    [InlineData("http://localhost:5001/Anmeldung", false)]
    [InlineData("https://localhost.example.test:5001/Anmeldung", false)]
    [InlineData("https://localhost:5001/_testumgebung/entra/v1/start", false)]
    public void Callback_only_allows_the_local_https_registration(string callback, bool expected) =>
        Assert.Equal(expected, EntraBrokerProtocol.IsCallback(callback));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
