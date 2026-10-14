/**
 * @file
 * @brief Bindet einen OPC-UA-Client als konfigurierbares Gerät ein; die reale Clientfabrik kommt aus der Desktop-Schicht.
 */

using Testumgebung.Contracts;

namespace Testumgebung.Instruments.Communication;

/// <summary>
/// OPC-UA-Gerät: stellt den OPC-UA-Client bereit, der je nach Ausgabe von der Anwendung eingehängt wird
/// (<see cref="OpcUaClientFactory"/>). Der Kanal ist frei; Knoten werden in den Bausteinen angegeben.
/// </summary>
public sealed class OpcUaDevice : CommDevice, IOpcUaDevice
{
    public const string AdapterName = "OPC UA";

    /// <summary>Realer OPC-UA-Client (Desktop/OPC-Foundation-Stack); null = nur Simulation verfügbar.</summary>
    public static Func<string, InstrumentOptions, IOpcUaClient>? ClientFactory { get; set; }

    private readonly IOpcUaClient _client;

    public OpcUaDevice(string resource, InstrumentOptions options, IOpcUaClient? client = null)
        : base(AdapterName, "OPC UA", resource, options)
    {
        _client = client ?? (ClientFactory ?? throw new InstrumentException("OPC UA ist in dieser Ausgabe nicht verfügbar."))(resource, options);
    }

    public override IReadOnlyList<InstrumentChannel> Channels { get; } =
        [new("OPC", "", 0, 0, ChannelAccess.ReadWrite, "OPC-UA-Knoten (Bausteine OPC UA / CodeBlock opcua_*)")];

    public IOpcUaClient OpcUa(string channel) => _client;

    public override async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        if (Options.Text("Sicherer Zustand") is not { } writes) return;
        await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        foreach (var pair in writes.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0) await _client.WriteAsync(pair[..eq].Trim(), ParseValue(pair[(eq + 1)..].Trim()), cancellationToken).ConfigureAwait(false);
        }
    }

    private static object? ParseValue(string t)
        => t.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
            : t.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
            : long.TryParse(t, out var l) ? l
            : double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d
            : t.Trim('"');

    public override async Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (Options.Text("Prüfknoten") is { } node)
            {
                var value = await _client.ReadAsync(node, cancellationToken).ConfigureAwait(false);
                return new InstrumentProbe(true, Resource, [$"{node} = {value}"], Log);
            }
            return new InstrumentProbe(true, Resource, ["verbunden"], Log);
        }
        catch (CommunicationException ex)
        {
            return new InstrumentProbe(false, "nicht erreichbar", [ex.Message], Log);
        }
    }

    public override void Dispose()
    {
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }
}
