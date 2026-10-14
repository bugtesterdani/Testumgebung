/**
 * @file
 * @brief Definiert Ressourcenanforderungen, Besitz und Gerätezugriffe unabhängig von realer Hardware oder Simulation.
 */

namespace Testumgebung.Contracts;

/// <summary>Anforderung einer oder mehrerer Ressourcen durch eine Gruppe (Scope) eines Laufs.</summary>
/// <param name="OwnerRunId">Lauf (Prüfplatz-Run), dem die Sperre gehört.</param>
/// <param name="ScopeId">Scope, an den die Sperre gebunden ist.</param>
/// <param name="Resources">Sperrschlüssel; ohne Prüfplatzkonfiguration die abstrakten Namen (z. B. <c>AC_Quelle</c>),
/// sonst das zugeordnete Gerät/der Kanal – damit sperren zwei Prüfplätze dasselbe Gerät auch unter verschiedenen Namen gemeinsam.</param>
/// <param name="OwnerName">Anzeigename des Besitzers (z. B. „Prüfplatz 02“).</param>
/// <param name="ScopeName">Anzeigename des Gültigkeitsbereichs (Gruppe).</param>
public sealed record ResourceRequest(string OwnerRunId, string ScopeId, IReadOnlyList<string> Resources, string? OwnerName = null, string? ScopeName = null);

/// <summary>Erworbene Sperre. <see cref="IDisposable.Dispose"/> gibt sie frei.</summary>
public interface IResourceLease : IDisposable
{
    ResourceRequest Request { get; }
}

/// <summary>Zeitüberschreitung beim Warten auf Ressourcen. Ergibt in der Engine ERROR.</summary>
public sealed class ResourceTimeoutException(string message) : Exception(message);

/// <summary>
/// Die Anforderung würde gegenseitiges Warten (Verklemmung) erzeugen: Sicherheitsnetz zur statischen Zyklusprüfung.
/// Die Anforderung wird nicht eingereiht; in der Engine ergibt das ERROR wie ein Zeitlimit.
/// </summary>
public sealed class ResourceDeadlockException(string message) : Exception(message);

/// <summary>Belegung eines Sperrschlüssels: Besitzer (Run), Referenzzähler, wartende Runs.</summary>
public sealed record ResourceOwnership(string Resource, string? OwnerRunId, int ReferenceCount, IReadOnlyList<string> WaitingRuns,
    string? OwnerName = null, IReadOnlyList<string>? WaitingNames = null, string? ScopeName = null, DateTimeOffset? Since = null);

/// <summary>
/// Zentrale Ressourcenvermittlung (Abschnitt 7): atomarer Erwerb mehrerer Ressourcen,
/// faire Warteschlange, Besitz an Run gebunden, Referenzzählung bei Verschachtelung.
/// </summary>
public interface IResourceManager
{
    /// <summary>
    /// Reserviert alle Ressourcen atomar. Wartet bei Belegung.
    /// <paramref name="timeout"/> = null: ohne Limit bis Verfügbarkeit oder Abbruch.
    /// Abbruch entfernt die Anforderung aus der Warteschlange.
    /// </summary>
    /// <exception cref="ResourceTimeoutException">Zeitlimit überschritten.</exception>
    /// <exception cref="OperationCanceledException">Abbruch während des Wartens.</exception>
    Task<IResourceLease> AcquireAsync(ResourceRequest request, TimeSpan? timeout, CancellationToken cancellationToken);

    bool IsHeldBy(string resource, string runId);

    IReadOnlyList<ResourceOwnership> Snapshot();
}

/// <summary>Fähigkeiten eines Hardwarekanals, wie sie ein Adapter meldet.</summary>
public sealed record ChannelCapabilities(string Unit, double Min, double Max, bool SupportsWaveform = false, bool SynchronousTimebase = false);

/// <summary>
/// Minimaler Adaptervertrag für Etappe 0. Kurven, Synchronisation und Ausgabe-Workflow
/// (Laden/Übertragen/Starten/Stoppen) folgen in Etappe 8.
/// </summary>
public interface IHardwareAdapter
{
    string Name { get; }

    ChannelCapabilities GetCapabilities(string channel);

    Task<double> ReadAsync(string channel, CancellationToken cancellationToken);

    Task WriteAsync(string channel, double value, CancellationToken cancellationToken);
}

/// <summary>
/// Gerät mit definiertem sicheren Zustand (Ausgänge aus, Analogausgänge 0 V). Wird nach Prüfungsende/Abbruch und beim
/// Beenden der Anwendung zusätzlich zu den Exit-Gruppen angefahren. Ersetzt keine hardwareseitige Abschaltung.
/// </summary>
public interface ISafeStateAdapter
{
    Task SafeStateAsync(CancellationToken cancellationToken);
}

/// <summary>Zuordnung eines abstrakten Namens (z. B. <c>Spannung_Pruefling</c>) zu Adapter und Kanal.</summary>
/// <param name="RequiresLock">Gemeinsam genutzte Hardware: Zugriff nur mit eigener Reservierung.</param>
/// <param name="LockKey">Sperrschlüssel des physischen Geräts/Kanals; ohne Angabe der abstrakte Name.</param>
public sealed record ResourceBinding(string Name, IHardwareAdapter Adapter, string Channel, bool RequiresLock, string? LockKey = null)
{
    /// <summary>Schlüssel für die Ressourcenvermittlung.</summary>
    public string Key => LockKey ?? Name;
}

/// <summary>Zugriff auf Hardware ohne gültige Reservierung.</summary>
public sealed class UnreservedHardwareAccessException(string message) : Exception(message);

/// <summary>Rechte (Abschnitt 6.1): Entwickeln und Freigeben getrennt, Kombination möglich.</summary>
[Flags]
public enum UserRights
{
    None = 0,
    /// <summary>Entwurf, Test, Simulation, Debugger, Einstellungen.</summary>
    Develop = 1,
    /// <summary>Geprüfte Pakete ausdrücklich freigeben.</summary>
    Release = 2,
}
