/**
 * @file
 * @brief Verbindet Session, Zugangskonfiguration und den zur Konfiguration passenden Anmeldeanbieter.
 */

using Testumgebung.Identity;

namespace Testumgebung.Presentation.Releases;

/// <summary>Anmeldung, Zugangskonfiguration und der dazu passende Anbieter (Entra über MSAL/WAM oder simuliert).</summary>
public sealed class IdentityServices
{
    private readonly Func<AccessConfig?, IIdentityProvider> _providerFactory;

    public IdentityServices(SessionService session, AccessConfigStore? store, Func<AccessConfig?, IIdentityProvider> providerFactory)
    {
        Session = session;
        Store = store;
        _providerFactory = providerFactory;
    }

    public SessionService Session { get; }
    public AccessConfigStore? Store { get; }

    /// <summary>Simulierte Anmeldung ohne Zugangsdatei (Entwicklungsmodus, Tests).</summary>
    public static IdentityServices Simulated(Func<string> name, AccessConfig? config = null)
        => new(new SessionService(new SimulatedIdentityProvider(name), config, allowDevelopmentMode: true), null, _ => new SimulatedIdentityProvider(name));

    /// <summary>Zugangsdatei neu laden (nach Bearbeitung) und Anbieter passend wählen.</summary>
    public void Reload()
    {
        var config = Store?.Load();
        Session.Reconfigure(_providerFactory(config), config);
    }
}
