using Testumgebung.Identity;

namespace Testumgebung.Desktop;

/// <summary>Shared Entra transport selection for the desktop and remote administration applications.</summary>
internal static class EntraIdentityProviderFactory
{
    public static IIdentityProvider Create(AccessConfig config)
    {
        if (!config.UsesEntra) throw new SignInException("Entra-Anwendungs-ID und Mandant fehlen.");
        return !string.IsNullOrEmpty(config.EntraBrokerTlsSha256)
            ? new EntraBrokerIdentityProvider(config)
            : new MsalIdentityProvider(config.ClientId!, config.TenantId!, config.EntraRedirectUri);
    }
}
