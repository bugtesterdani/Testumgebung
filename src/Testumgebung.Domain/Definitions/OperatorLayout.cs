/**
 * @file
 * @brief Beschreibt die Anordnung der Werkeransicht als Teil des Prüfprogramms.
 */

namespace Testumgebung.Domain.Definitions;

/// <summary>Bereich für „Aktueller Schritt und Fortschritt“ in der Werkeransicht (Abschnitt 4).</summary>
public enum ProgressPosition
{
    /// <summary>Über dem aktiven Inhalt.</summary>
    Top,
    /// <summary>In der Ablaufübersicht links (Standard).</summary>
    Left,
    /// <summary>Unter dem aktiven Inhalt.</summary>
    Bottom,
}

/// <summary>
/// Layout und Darstellung der Werkeransicht je Ablauf (Teil des Projekts und damit des Release-Pakets).
/// Einzelne Schritte und Messwerte blendet der Entwickler über den Filter „Anzeigen (am Prüfplatz)“ aus.
/// </summary>
public sealed class OperatorLayout
{
    public ProgressPosition ProgressPosition { get; set; } = ProgressPosition.Left;
    public bool ShowOverview { get; set; } = true;
    public bool ShowProtocol { get; set; } = true;

    /// <summary>Alle Messwerte standardmäßig anzeigen; aus = nur auffällige (FAIL/ERROR), der Werker kann alle einblenden.</summary>
    public bool ShowAllMeasurements { get; set; } = true;

    public OperatorLayout Clone() => (OperatorLayout)MemberwiseClone();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDefault => ProgressPosition == ProgressPosition.Left && ShowOverview && ShowProtocol && ShowAllMeasurements;
}
