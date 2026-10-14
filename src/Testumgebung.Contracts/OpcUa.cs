/**
 * @file
 * @brief Beschreibt OPC-UA-Verbindungen und Abonnements unabhängig von der verwendeten Clientbibliothek.
 */

namespace Testumgebung.Contracts;

/// <summary>Fehler beim OPC-UA-Zugriff (Verbindung, unbekannter Knoten, abgelehnter Methodenaufruf).</summary>
public sealed class OpcUaException(string message, Exception? inner = null) : CommunicationException(message, inner);

/// <summary>
/// OPC-UA-Client zum Lesen und Schreiben von Variablenknoten, Aufrufen von Methoden und Abonnieren (Datenänderungen).
/// Werte sind null, bool, long, double oder string; NodeIds im Format <c>ns=2;s=Name</c> bzw. <c>ns=2;i=42</c>.
/// </summary>
public interface IOpcUaClient : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);

    Task<object?> ReadAsync(string nodeId, CancellationToken cancellationToken);

    Task WriteAsync(string nodeId, object? value, CancellationToken cancellationToken);

    /// <summary>Methode aufrufen (Objektknoten + Methodenknoten) und die Ausgangsargumente liefern.</summary>
    Task<IReadOnlyList<object?>> CallAsync(string objectNodeId, string methodNodeId, IReadOnlyList<object?> arguments, CancellationToken cancellationToken);

    /// <summary>Datenänderungen eines Knotens abonnieren (Intervall in ms); liefert ein Abo, das den jeweils neuesten Wert hält.</summary>
    Task<IOpcUaSubscription> SubscribeAsync(string nodeId, int samplingIntervalMs, CancellationToken cancellationToken);
}

/// <summary>Laufendes OPC-UA-Abonnement eines Knotens.</summary>
public interface IOpcUaSubscription : IAsyncDisposable
{
    string NodeId { get; }

    /// <summary>Zuletzt gemeldeter Wert (null, solange noch keine Meldung kam).</summary>
    object? Current { get; }

    /// <summary>Anzahl gemeldeter Änderungen seit Beginn.</summary>
    int Changes { get; }

    /// <summary>Auf die nächste Datenänderung warten (oder Zeitüberschreitung).</summary>
    Task<object?> WaitForChangeAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Gerät, das je Kanal einen OPC-UA-Client bereitstellt (Simulation oder realer Server).</summary>
public interface IOpcUaDevice
{
    IOpcUaClient OpcUa(string channel);
}
