/**
 * @file
 * @brief Führt Befehle aus einem benutzerdefinierten Geräteprofil über den gemeinsamen Texttransport aus.
 */

using System.Globalization;

namespace Testumgebung.Instruments;

/// <summary>No implicit initialization, error queries or output writes. Only configured commands are sent.</summary>
public sealed class CustomTextInstrument : ScpiInstrument
{
    private readonly CustomDeviceProfile _profile;
    public CustomTextInstrument(CustomDeviceProfile profile, ScpiSession session)
        : base(profile.Adapter, profile.Name, session)
    {
        var errors = profile.Validate();
        if (errors.Count > 0) throw new InstrumentException(string.Join(" ", errors));
        _profile = profile.Clone();
        Channels = _profile.InstrumentChannels();
        session.CheckErrors = false;
    }

    public string ProfileSignature => _profile.Signature();
    public override IReadOnlyList<InstrumentChannel> Channels { get; }
    private CustomDeviceChannel Definition(InstrumentChannel channel) => _profile.Channels.First(c => c.Name == channel.Name);
    protected override async Task<double> ReadChannelAsync(InstrumentChannel channel, CancellationToken ct)
    {
        var response = (await Session.QueryAsync(Definition(channel).ReadQuery, ct).ConfigureAwait(false)).Trim();
        if (response.Equals("ON", StringComparison.OrdinalIgnoreCase)) return 1;
        if (response.Equals("OFF", StringComparison.OrdinalIgnoreCase)) return 0;
        if (!double.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InstrumentException($"{Model}, {channel.Name}: keine endliche Zahl in Antwort „{response}“.");
        return value;
    }

    protected override Task WriteChannelAsync(InstrumentChannel channel, double value, CancellationToken ct)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Wert muss endlich sein.");
        return Session.WriteAsync(Definition(channel).WriteCommand.Replace("{value}", value.ToString("G17", CultureInfo.InvariantCulture), StringComparison.Ordinal), ct);
    }

    public override async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        var commands = _profile.SafeCommands.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (commands.Length == 0 && Channels.Any(c => c.Access.HasFlag(ChannelAccess.Write)))
            throw new InstrumentException($"{Model}: keine Befehle für den sicheren Zustand definiert.");
        var failures = new List<Exception>();
        foreach (var command in commands)
        {
            try { await Session.WriteAsync(command, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InstrumentException or TimeoutException or IOException) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new InstrumentException(string.Join("; ", failures.Select(e => e.Message)));
    }

    public override async Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_profile.ProbeQuery))
            return new(false, "Identität nicht geprüft", ["Im Geräteprofil eine lesende Prüfabfrage definieren."], Log);
        try
        {
            var identity = await Session.QueryAsync(_profile.ProbeQuery, cancellationToken).ConfigureAwait(false);
            return new(!string.IsNullOrWhiteSpace(identity), identity, ["Kanäle: " + string.Join(", ", Channels.Select(c => c.Name))], Log);
        }
        catch (Exception ex) when (ex is InstrumentException or TimeoutException or IOException)
        {
            return new(false, "nicht erreichbar", [ex.Message], Log);
        }
    }
}
