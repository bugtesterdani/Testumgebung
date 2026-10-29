/** @file @brief Begrenzte Live-Ausgabe eines Schritts, getrennt von seiner Bewertung. */
namespace Testumgebung.Domain.Definitions;

/// <summary>Text anhängen und optional Status/Fortschritt aktualisieren. Reset beginnt eine neue Anzeige desselben Kanals.</summary>
public sealed record LiveOutputUpdate(string Text, double? Percent = null, string? Status = null, string Channel = "Ausgabe", bool Reset = false)
{
    public void Validate()
    {
        if (Percent is { } p && (!double.IsFinite(p) || p < 0 || p > 100)) throw new ArgumentOutOfRangeException(nameof(Percent), "Fortschritt muss zwischen 0 und 100 liegen.");
        if (string.IsNullOrWhiteSpace(Channel) || Channel.Length > 128) throw new ArgumentException("Live-Kanal muss 1 bis 128 Zeichen enthalten.");
        if (Text.Length > 65536 || Status?.Length > 2048) throw new ArgumentException("Live-Ausgabe: maximal 65536 Text- und 2048 Statuszeichen je Aufruf.");
    }
}
