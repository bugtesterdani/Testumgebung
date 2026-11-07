using System.Globalization;

namespace Testumgebung.Instruments;

public sealed record UlDevice(string Product, string Identifier, string Connection, object? NativeDescriptor = null);
public interface IUlApi
{
    IReadOnlyList<UlDevice> Inventory(string connection);
    UlDevice NetworkDevice(string host, int port, int timeoutMs);
    int Open(UlDevice device);
    void Close(int board);
    int Config(int board, int type, int device, int item);
    double AnalogIn(int board, int channel, int range);
    double TemperatureIn(int board, int channel);
    void AnalogOut(int board, int channel, int range, double value);
    int DigitalIn(int board, int port, int bit);
    void DigitalOut(int board, int port, int bit, int value);
    void ConfigureBit(int board, int port, int bit, bool output);
    void ConfigurePort(int board, int port, bool output);
}

/// <summary>Discovers channels from UL; whole-port direction changes require explicit configuration.</summary>
public sealed class UlIoSession : IDaqIoSession
{
    private readonly IUlApi _api;
    private readonly int _board;
    private readonly InstrumentOptions _options;
    private readonly Dictionary<string, UlChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _configuredPorts = [];
    private bool _disposed;
    private sealed record UlChannel(InstrumentChannel Info, int Index, int Range = 0, int Port = 0, int LocalBit = 0, int ApiBit = 0, int InputMask = 0, int OutputMask = 0);
    public UlIoSession(DaqModel model, string connection, string resource, InstrumentOptions options, IUlApi api)
    {
        _api = api; _options = options;
        var device = FindDevice(model, connection, resource, options, api);
        _board = api.Open(device);
        try
        {
            Identity = $"{device.Product} / {device.Connection} / {device.Identifier}";
            AddAnalog(device.Product);
            AddDigital();
            Channels = _channels.Values.Select(channel => channel.Info).ToArray();
        }
        catch { api.Close(_board); throw; }
    }
    private static UlDevice FindDevice(DaqModel model, string connection, string resource, InstrumentOptions options, IUlApi api)
    {
        var matches = api.Inventory(connection).Where(device => device.Identifier.Equals(resource, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0 && connection == "Ethernet") matches.Add(api.NetworkDevice(resource, IntegerOption(options, "Port", 54211, 65535), IntegerOption(options, "Erkennungszeit [ms]", 5000, 120000)));
        if (matches.Count != 1) throw new InstrumentException("UL-Gerät nicht eindeutig gefunden. Seriennummer, MAC-Adresse oder Ethernet-Adresse prüfen.");
        var selected = matches[0];
        if (selected.Connection != connection) throw new InstrumentException("Erkanntes UL-Gerät passt nicht zur gewählten Schnittstelle.");
        if (!model.Name.StartsWith("UL-Gerät", StringComparison.Ordinal) && ProductKey(model.Name) != ProductKey(selected.Product))
            throw new InstrumentException($"Erkanntes Modell {selected.Product} passt nicht zu {model.Name}. Für weitere unterstützte Produkte die automatische UL-Erkennung verwenden.");
        return selected;
    }
    private static int IntegerOption(InstrumentOptions options, string name, int fallback, int maximum)
    {
        if (options.Text(name) is null) return fallback;
        var value = options.Number(name);
        return value is >= 1 && value <= maximum && value == Math.Truncate(value.Value) ? (int)value.Value
            : throw new InstrumentException($"{name}: ganze Zahl von 1 bis {maximum} erforderlich.");
    }
    private static string ProductKey(string name) => name.ToUpperInvariant().Replace("REDLAB", "").Replace("USB", "").Replace("MINILAB", "").Replace("PMD", "").Replace("ME-", "").Replace("-", "").Replace(" ", "");
    public string Identity { get; }
    public IReadOnlyList<InstrumentChannel> Channels { get; }
    private int Count(int item)
    {
        var count = _api.Config(_board, 2, 0, item);
        return count is >= 0 and <= 4096 ? count : throw new InstrumentException("UL meldet eine ungültige Kanalzahl.");
    }
    private void AddAnalog(string product)
    {
        var temperatureDevice = product.Contains("TEMP", StringComparison.OrdinalIgnoreCase) || product.Contains("TC", StringComparison.OrdinalIgnoreCase);
        var hybrid = product.EndsWith("-AI", StringComparison.OrdinalIgnoreCase);
        var kinds = DaqSettings.Map(_options, "Kanalarten");
        var inputs = Count(7);
        for (var index = 0; index < inputs; index++)
        {
            var selected = kinds.GetValueOrDefault("AI" + index);
            if (selected is not (null or "Spannung" or "Temperatur")) throw new InstrumentException("Kanalarten: AI<n>=Spannung oder Temperatur angeben.");
            var temperature = selected is null ? temperatureDevice && (!hybrid || index < 4) : selected == "Temperatur";
            var range = temperature ? 0 : UlRanges.Select(_options.Text("AI-Bereich"), _api.Config(_board, 2, 0, 6));
            var limits = temperature ? (-270d, 2000d) : UlRanges.Limits(range);
            var name = (temperature ? "TI" : "AI") + index;
            _channels.Add(name, new(new(name, temperature ? "°C" : "V", limits.Item1, limits.Item2, ChannelAccess.Read, "Analogeingang; Fähigkeiten aus UL"), index, range));
        }
        var outputs = Count(13);
        for (var index = 0; index < outputs; index++)
        {
            var range = UlRanges.Select(_options.Text("AO-Bereich"), _api.Config(_board, 2, index, 114));
            var limits = UlRanges.Limits(range); var name = "AO" + index;
            _channels.Add(name, new(new(name, "V", limits.Min, limits.Max, ChannelAccess.Write, "Analogausgang; Bereich aus UL/Konfiguration"), index, range));
        }
    }
    private void AddDigital()
    {
        var total = 0;
        var ports = Count(9);
        for (var device = 0; device < ports; device++)
        {
            var type = _api.Config(_board, 3, device, 2); var bits = _api.Config(_board, 3, device, 6);
            if (bits is < 0 or > 32) throw new InstrumentException("UL meldet eine ungültige Portbreite.");
            var input = _api.Config(_board, 3, device, 8); var output = _api.Config(_board, 3, device, 9);
            var selected = DaqSettings.Map(_options, "Port-Richtungen").GetValueOrDefault("PORT" + type);
            if (selected is not (null or "Eingang" or "Ausgang")) throw new InstrumentException("Port-Richtungen: PORT<UL-Typ>=Eingang oder Ausgang angeben.");
            for (var bit = 0; bit < bits; bit++)
            {
                var name = "DIO" + total++;
                var access = selected == "Eingang" ? ChannelAccess.Read : ChannelAccess.ReadWrite;
                var apiBit = type < 10 ? bit : (type - 10) / 4 * 24 + new[] { 0, 8, 16, 20 }[(type - 10) % 4] + bit;
                _channels.Add(name, new(new(name, "bool", 0, 1, access, $"PORT{type}, Bit {bit}; Port-Richtungen bei gemeinsam umschaltbaren Ports ausdrücklich konfigurieren"), bit, Port:type, LocalBit:bit, ApiBit:apiBit, InputMask:input, OutputMask:output));
            }
        }
    }
    public double Read(string channel)
    {
        var item = _channels[channel];
        if (channel.StartsWith("TI", StringComparison.OrdinalIgnoreCase)) return _api.TemperatureIn(_board, item.Index);
        if (channel.StartsWith("AI", StringComparison.OrdinalIgnoreCase)) return _api.AnalogIn(_board, item.Index, item.Range);
        ConfigureDigital(item, false); return _api.DigitalIn(_board, item.Port < 10 ? item.Port : 10, item.ApiBit);
    }
    public void Write(string channel, double value)
    {
        var item = _channels[channel];
        if (channel.StartsWith("AO", StringComparison.OrdinalIgnoreCase)) { _api.AnalogOut(_board, item.Index, item.Range, value); return; }
        ConfigureDigital(item, true); _api.DigitalOut(_board, item.Port < 10 ? item.Port : 10, item.ApiBit, (int)value);
    }
    private void ConfigureDigital(UlChannel item, bool output)
    {
        var selected = DaqSettings.Map(_options, "Port-Richtungen").GetValueOrDefault("PORT" + item.Port);
        if (selected is not null)
        {
            if (_configuredPorts.Add(item.Port))
            {
                try { _api.ConfigurePort(_board, item.Port, selected == "Ausgang"); }
                catch { _configuredPorts.Remove(item.Port); throw; }
            }
            return;
        }
        // UL permits reading the current state of output bits without changing their direction.
        var mask = output ? item.OutputMask : item.InputMask | item.OutputMask;
        if ((mask & (1 << item.LocalBit)) != 0) return;
        if (item.Port >= 10) throw new InstrumentException($"PORT{item.Port} schaltet mehrere Leitungen gemeinsam um. Port-Richtungen ausdrücklich konfigurieren; keine automatische Änderung fremder Bits.");
        _api.ConfigureBit(_board, item.Port, item.LocalBit, output);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _api.Close(_board); }
}

internal static class UlRanges
{
    private static readonly IReadOnlyDictionary<int, (double Min, double Max)> Ranges = new Dictionary<int, (double, double)>
    { [20]=(-60,60), [23]=(-30,30), [15]=(-20,20), [21]=(-15,15), [1]=(-10,10), [0]=(-5,5), [16]=(-4,4), [2]=(-2.5,2.5), [14]=(-2,2), [3]=(-1.25,1.25), [4]=(-1,1), [5]=(-0.625,0.625), [6]=(-0.5,0.5), [12]=(-0.25,0.25), [13]=(-0.2,0.2), [7]=(-0.1,0.1), [8]=(-0.05,0.05), [9]=(-0.01,0.01), [10]=(-0.005,0.005), [11]=(-1.67,1.67), [17]=(-0.3125,0.3125), [18]=(-0.15625,0.15625), [22]=(-0.125,0.125), [19]=(-0.078125,0.078125), [100]=(0,10), [101]=(0,5), [114]=(0,4), [102]=(0,2.5), [103]=(0,2), [109]=(0,1.67), [104]=(0,1.25), [105]=(0,1), [110]=(0,0.5), [111]=(0,0.25), [112]=(0,0.2), [106]=(0,0.1), [113]=(0,0.05), [108]=(0,0.02), [107]=(0,0.01) };
    public static int Select(string? requested, int configured)
    {
        var range = requested is null ? configured : int.TryParse(requested, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw new InstrumentException("UL-Bereich benötigt eine numerische ULRange-Kennung.");
        _ = Limits(range); return range;
    }
    public static (double Min, double Max) Limits(int range) => Ranges.TryGetValue(range, out var limits) ? limits : throw new InstrumentException($"UL-Bereich {range} unbekannt oder programmierbar. AI-Bereich/AO-Bereich ausdrücklich gemäß Gerätehandbuch angeben.");
}
