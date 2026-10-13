using System.Text.Json;

namespace Testumgebung.Instruments;

/// <summary>Portable, project-owned definition of a numeric text-protocol instrument.</summary>
public sealed class CustomDeviceProfile : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Description { get; set; } = "";
    public string ProbeQuery { get; set; } = "";
    public string SafeCommands { get; set; } = "";
    public List<CustomDeviceChannel> Channels { get; set; } = [];
    public const string Prefix = "Eigenes Gerät: ";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Adapter => Prefix + Id;
    public CustomDeviceProfile Clone() => JsonSerializer.Deserialize<CustomDeviceProfile>(JsonSerializer.Serialize(this))!;
    public string Signature() => JsonSerializer.Serialize(this);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name)) errors.Add("Geräteprofil: ID und Name erforderlich.");
        if (Channels is null || Channels.Any(c => c is null)) { errors.Add("Ungültige Kanalliste."); return errors; }
        if (Channels.Count == 0) errors.Add("Mindestens einen Kanal definieren.");
        if (Channels.Select(c => c.Name?.Trim() ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() != Channels.Count)
            errors.Add("Kanalnamen müssen eindeutig sein.");
        foreach (var channel in Channels) errors.AddRange(channel.Validate());
        if (ProbeQuery is null || SafeCommands is null) errors.Add("Befehlsfelder dürfen nicht null sein.");
        if (CustomDeviceChannel.ContainsLineBreak(ProbeQuery)) errors.Add("Prüfabfrage darf nur einen Befehl enthalten.");
        return errors;
    }

    public IReadOnlyList<InstrumentChannel> InstrumentChannels() => Channels.Select(c => new InstrumentChannel(
        c.Name, c.Unit, c.Min, c.Max,
        (string.IsNullOrWhiteSpace(c.ReadQuery) ? (ChannelAccess)0 : ChannelAccess.Read) |
        (string.IsNullOrWhiteSpace(c.WriteCommand) ? (ChannelAccess)0 : ChannelAccess.Write), c.Description)).ToList();
}

public sealed class CustomDeviceChannel
{
    public string Name { get; set; } = "Kanal";
    public string Unit { get; set; } = "";
    public double Min { get; set; } = -1e9;
    public double Max { get; set; } = 1e9;
    public string ReadQuery { get; set; } = "";
    public string WriteCommand { get; set; } = "";
    public string Description { get; set; } = "";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Kanalname fehlt.");
        if (!double.IsFinite(Min) || !double.IsFinite(Max) || Min > Max) errors.Add($"{Name}: ungültige Grenzen.");
        if (string.IsNullOrWhiteSpace(ReadQuery) && string.IsNullOrWhiteSpace(WriteCommand)) errors.Add($"{Name}: Lese- oder Schreibbefehl erforderlich.");
        if (!string.IsNullOrWhiteSpace(WriteCommand) && !WriteCommand.Contains("{value}", StringComparison.Ordinal))
            errors.Add($"{Name}: Schreibbefehl muss {{value}} enthalten.");
        if (ContainsLineBreak(ReadQuery) || ContainsLineBreak(WriteCommand))
            errors.Add($"{Name}: je Feld nur einen Befehl angeben.");
        return errors;
    }
    internal static bool ContainsLineBreak(string? command)
        => command?.IndexOfAny(['\r', '\n']) >= 0;

}
