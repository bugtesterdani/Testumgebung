/**
 * @file
 * @brief Definiert die laufbezogene Freigabe zwischen Seiten einer gemeinsamen Auftragssitzung.
 */

namespace Testumgebung.Domain.Definitions;

/// <summary>Sitzungsbezogene Übergabe zwischen zwei Prüfseiten; Kanäle sind unabhängig.</summary>
public interface IStationHandover
{
    /// <summary>Wartet auf den Freigabekanal und übernimmt ihn für die angegebene Seite.</summary>
    /// <param name="side">Prüfseite 1 oder 2.</param>
    /// <param name="channel">Sitzungsinterner Kanalname der Übergabe.</param>
    /// <param name="firstSide">Seite, die den noch unbenutzten Kanal zuerst erhalten soll.</param>
    /// <param name="cancellationToken">Bricht das Warten ab, ohne eine Freigabe zu übernehmen.</param>
    Task WaitAsync(int side, string channel, int firstSide, CancellationToken cancellationToken);
    /// <summary>Gibt den gehaltenen Kanal an die andere Prüfseite weiter.</summary>
    /// <param name="side">Seite, die den Kanal derzeit hält.</param>
    /// <param name="channel">Name des zuvor übernommenen Freigabekanals.</param>
    void Release(int side, string channel);
}

public sealed record PairedStationContext(IStationHandover Handover, int Side);
