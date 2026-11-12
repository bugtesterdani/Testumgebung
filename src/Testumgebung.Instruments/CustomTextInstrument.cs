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
    private CustomDeviceChannel Definition(InstrumentChannel channel) => _profile.Channels.First(customDeviceChannel => customDeviceChannel.Name == channel.Name);
    protected override async Task<double> ReadChannelAsync(InstrumentChannel channel, CancellationToken ct)
    {
        var definition = Definition(channel);
        var commands = ChannelCommands(definition, definition.ReadQuery, definition.ReadEndings);
        var response = (await Session.ExecuteCustomAsync(commands, readReply: true, ct).ConfigureAwait(false)).Trim();
        if (response.Equals("ON", StringComparison.OrdinalIgnoreCase)) return 1;
        if (response.Equals("OFF", StringComparison.OrdinalIgnoreCase)) return 0;
        if (!TryReadNumber(response, channel.Unit, out var value))
            throw new InstrumentException($"{Model}, {channel.Name}: keine endliche Zahl in Antwort „{response}“.");
        return value;
    }

    private static bool TryReadNumber(string response, string? unit, out double value)
    {
        if (double.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return double.IsFinite(value);

        var suffix = unit?.Trim();
        if (string.IsNullOrEmpty(suffix) || !response.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        var number = response.AsSpan(0, response.Length - suffix.Length);
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    protected override Task WriteChannelAsync(InstrumentChannel channel, double value, CancellationToken ct)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Wert muss endlich sein.");
        var definition = Definition(channel);
        var command = definition.WriteCommand.Replace("{value}", value.ToString("G17", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return Session.ExecuteCustomAsync(ChannelCommands(definition, command, definition.WriteEndings), readReply: false, ct);
    }

    private CustomCommand[] ChannelCommands(CustomDeviceChannel channel, string commands, string endings) =>
    [
        ..CustomCommandSequence.Parse(channel.SelectCommands, channel.SelectEndings, split: true),
        ..CustomCommandSequence.Parse(commands, endings, _profile.SplitCommandSequences)
    ];

    public override async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        var commands = CustomCommandSequence.Parse(_profile.SafeCommands, _profile.SafeEndings, _profile.SplitCommandSequences, multiline: true);
        if (commands.Length == 0 && Channels.Any(channel => channel.Access.HasFlag(ChannelAccess.Write)))
            throw new InstrumentException($"{Model}: keine Befehle für den sicheren Zustand definiert.");
        var failures = new List<Exception>();
        await Session.ExecuteCustomAsync(commands, readReply: false, cancellationToken, failures).ConfigureAwait(false);
        if (failures.Count > 0) throw new InstrumentException(string.Join("; ", failures.Select(error => error.Message)));
    }

    public override async Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_profile.ProbeQuery))
            return new(false, "Identität nicht geprüft", ["Im Geräteprofil eine lesende Prüfabfrage definieren."], Log);
        try
        {
            var commands = CustomCommandSequence.Parse(_profile.ProbeQuery, _profile.ProbeEndings, _profile.SplitCommandSequences);
            var identity = await Session.ExecuteCustomAsync(commands, readReply: true, cancellationToken).ConfigureAwait(false);
            return new(!string.IsNullOrWhiteSpace(identity), identity, ["Kanäle: " + string.Join(", ", Channels.Select(channel => channel.Name))], Log);
        }
        catch (Exception ex) when (ex is InstrumentException or TimeoutException or IOException)
        {
            return new(false, "nicht erreichbar", [ex.Message], Log);
        }
    }
}
