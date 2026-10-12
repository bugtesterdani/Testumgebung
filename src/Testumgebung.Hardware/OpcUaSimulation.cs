using System.Collections.Concurrent;
using System.Globalization;
using Testumgebung.Contracts;

namespace Testumgebung.Hardware;

/// <summary>
/// In-Prozess-OPC-UA-Server für Simulation und Tests: Variablenknoten, Methoden und Abonnements ohne echten Server.
/// Konfiguration über Regeln je Zeile:
/// <list type="bullet">
/// <item><c>ns=2;s=Spannung = 230.5</c> – Variablenknoten mit Startwert (Zahl, true/false oder "Text").</item>
/// <item><c>methode ns=2;s=Obj ns=2;s=Add => summe</c> – Methode: Ausgabe = Summe der Eingänge (für Tests);
///   weitere eingebaute Methoden: <c>spiegeln</c> (Argumente umgekehrt zurück), <c>konstante:V</c> (festes Ergebnis).</item>
/// </list>
/// </summary>
public sealed class SimulatedOpcUaServer : IOpcUaClient
{
    private sealed record Method(string Objekt, string Node, string Kind, object? Constant);

    private readonly ConcurrentDictionary<string, object?> _nodes = new(StringComparer.Ordinal);
    private readonly List<Method> _methods = [];
    private readonly ConcurrentDictionary<string, List<Subscription>> _subscriptions = new(StringComparer.Ordinal);
    private bool _connected;

    public SimulatedOpcUaServer(string rules = "")
    {
        foreach (var raw in rules.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("methode ", StringComparison.OrdinalIgnoreCase))
            {
                var arrow = line.IndexOf("=>", StringComparison.Ordinal);
                var head = (arrow >= 0 ? line[8..arrow] : line[8..]).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (head.Length < 2) throw new FormatException($"OPC-UA-Methode „{line}“: Objekt- und Methodenknoten angeben.");
                var kind = arrow >= 0 ? line[(arrow + 2)..].Trim() : "summe";
                object? constant = kind.StartsWith("konstante:", StringComparison.OrdinalIgnoreCase) ? ParseValue(kind["konstante:".Length..]) : null;
                _methods.Add(new Method(head[0], head[1], kind.StartsWith("konstante:", StringComparison.OrdinalIgnoreCase) ? "konstante" : kind, constant));
                continue;
            }
            // NodeIds enthalten selbst „=“ (ns=2;s=Name); der Wert ist ein Skalar ohne „=“ → am letzten „=“ trennen.
            var eq = line.LastIndexOf('=');
            if (eq < 0) throw new FormatException($"OPC-UA-Regel „{line}“: NodeId = Wert erwartet.");
            _nodes[line[..eq].Trim()] = ParseValue(line[(eq + 1)..].Trim());
        }
    }

    internal static object? ParseValue(string text)
    {
        var t = text.Trim();
        if (t.StartsWith('"') && t.EndsWith('"') && t.Length >= 2) return t[1..^1];
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return t;
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (!_connected) throw new OpcUaException("OPC-UA-Simulation: nicht verbunden.");
    }

    public Task<object?> ReadAsync(string nodeId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (!_nodes.TryGetValue(nodeId, out var value)) throw new OpcUaException($"OPC-UA-Simulation: Knoten „{nodeId}“ unbekannt.");
        return Task.FromResult(value);
    }

    public Task WriteAsync(string nodeId, object? value, CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (!_nodes.ContainsKey(nodeId)) throw new OpcUaException($"OPC-UA-Simulation: Knoten „{nodeId}“ unbekannt (nicht beschreibbar).");
        _nodes[nodeId] = value;
        if (_subscriptions.TryGetValue(nodeId, out var subs))
            lock (subs) foreach (var s in subs) s.Push(value);
        return Task.FromResult(0);
    }

    public Task<IReadOnlyList<object?>> CallAsync(string objectNodeId, string methodNodeId, IReadOnlyList<object?> arguments, CancellationToken cancellationToken)
    {
        EnsureConnected();
        var method = _methods.FirstOrDefault(m => m.Objekt == objectNodeId && m.Node == methodNodeId)
            ?? throw new OpcUaException($"OPC-UA-Simulation: Methode „{methodNodeId}“ an „{objectNodeId}“ unbekannt.");
        IReadOnlyList<object?> result = method.Kind switch
        {
            "konstante" => [method.Constant],
            "spiegeln" => [.. arguments.Reverse()],
            _ => [arguments.Sum(a => Convert.ToDouble(a, CultureInfo.InvariantCulture))],
        };
        return Task.FromResult(result);
    }

    public Task<IOpcUaSubscription> SubscribeAsync(string nodeId, int samplingIntervalMs, CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (!_nodes.ContainsKey(nodeId)) throw new OpcUaException($"OPC-UA-Simulation: Knoten „{nodeId}“ unbekannt (nicht abonnierbar).");
        var sub = new Subscription(nodeId, this);
        _subscriptions.GetOrAdd(nodeId, _ => []).Add(sub);
        sub.Push(_nodes[nodeId]); // Erstwert
        return Task.FromResult<IOpcUaSubscription>(sub);
    }

    /// <summary>Für Tests: einen Knotenwert ändern (löst Abonnements aus), als käme die Änderung vom Server.</summary>
    public void Set(string nodeId, object? value) => WriteAsync(nodeId, value, CancellationToken.None).GetAwaiter().GetResult();

    private void Remove(Subscription sub)
    {
        if (_subscriptions.TryGetValue(sub.NodeId, out var subs)) lock (subs) subs.Remove(sub);
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }

    private sealed class Subscription(string nodeId, SimulatedOpcUaServer server) : IOpcUaSubscription
    {
        private readonly SemaphoreSlim _signal = new(0);
        private readonly object _gate = new();

        public string NodeId => nodeId;
        public object? Current { get; private set; }
        public int Changes { get; private set; }

        public void Push(object? value)
        {
            lock (_gate)
            {
                Current = value;
                Changes++;
            }
            _signal.Release();
        }

        public async Task<object?> WaitForChangeAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (!await _signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
                throw new CommunicationTimeoutException($"OPC-UA-Abo „{nodeId}“: keine Änderung innerhalb von {timeout.TotalMilliseconds:0} ms.", []);
            lock (_gate) return Current;
        }

        public ValueTask DisposeAsync()
        {
            server.Remove(this);
            return ValueTask.CompletedTask;
        }
    }
}
