using Testumgebung.Identity;

namespace Testumgebung.Desktop.UiTests;

public sealed class EntraIdentityProviderFactoryTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("invalid-pin", true)]
    public void Both_applications_select_the_configured_entra_transport(string? fingerprint, bool broker)
    {
        var config = new AccessConfig
        {
            ClientId = "11111111-2222-3333-4444-555555555555",
            TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            EntraRedirectUri = broker ? "https://localhost:5001/Anmeldung" : "http://localhost",
            EntraBrokerTlsSha256 = fingerprint
        };
        var provider = EntraIdentityProviderFactory.Create(config);
        if (broker) Assert.IsType<EntraBrokerIdentityProvider>(provider);
        else Assert.IsType<MsalIdentityProvider>(provider);
    }

    [Fact]
    public async Task Invalid_broker_configuration_reports_a_signin_error()
    {
        var config = new AccessConfig
        {
            ClientId = "11111111-2222-3333-4444-555555555555",
            TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            EntraRedirectUri = "https://localhost:5001/Anmeldung",
            EntraBrokerTlsSha256 = "invalid-pin"
        };
        var provider = EntraIdentityProviderFactory.Create(config);
        var error = await Assert.ThrowsAsync<SignInException>(() => provider.SignInAsync(true, CancellationToken.None));
        Assert.Contains("Fingerabdruck", error.Message);
    }

    [Fact]
    public void Missing_entra_registration_is_reported()
        => Assert.Throws<SignInException>(() => EntraIdentityProviderFactory.Create(new AccessConfig()));
}
