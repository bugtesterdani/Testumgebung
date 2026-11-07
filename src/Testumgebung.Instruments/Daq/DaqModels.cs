using System.Globalization;

namespace Testumgebung.Instruments;

public enum DaqSdk { Ljm, Ud, U12, UniversalLibrary }
public sealed record DaqModel(string Name, DaqSdk Sdk, string[] Connections, int AnalogInputs = 0, int AnalogOutputs = 0, int DigitalBits = 0);

/// <summary>Physical transports, not interchangeable protocol choices. UL can identify further supported products at runtime.</summary>
public static class DaqModels
{
    public const string LabJack = "LabJack DIO / AIO";
    public const string Simulation = "DAQ DIO / AIO (Nachbildung ohne Treiber)";
    public const string RedLab = "RedLab / MCC Universal Library";
    public const string AutoUsb = "UL-Gerät automatisch (USB)";
    public const string AutoEthernet = "UL-Gerät automatisch (Ethernet)";
    public static IReadOnlyList<DaqModel> All { get; } = Build();
    private static IReadOnlyList<DaqModel> Build()
    {
        var models = new List<DaqModel>();
        foreach (var name in new[] { "T4", "T4-OEM", "T7", "T7-OEM", "T7-Pro", "T7-Pro-OEM", "T8" })
            models.Add(new(name, DaqSdk.Ljm, name.Contains("Pro", StringComparison.Ordinal) ? ["USB", "Ethernet", "WLAN"] : ["USB", "Ethernet"], name.StartsWith("T4", StringComparison.Ordinal) ? 12 : name == "T8" ? 8 : 14, 2, name.StartsWith("T7", StringComparison.Ordinal) ? 23 : 20));
        foreach (var name in new[] { "U3-LV", "U3-HV", "U3-LV-OEM", "U3-HV-OEM", "U6", "U6-Pro", "U6-OEM", "U6-Pro-OEM", "UE9", "UE9-Pro", "UE9-OEM", "UE9-Pro-OEM" })
            models.Add(new(name, DaqSdk.Ud, name.StartsWith("UE9", StringComparison.Ordinal) ? ["USB", "Ethernet"] : ["USB"], name.StartsWith("U3", StringComparison.Ordinal) ? 16 : 14, 2, name.StartsWith("UE9", StringComparison.Ordinal) ? 23 : 20));
        models.Add(new("U12", DaqSdk.U12, ["USB"], 8, 2, 20));
        // The native UL inventory supplies the complete product identity and actual channel capabilities.
        foreach (var name in new[] { "1008", "1024LS", "1024HLS", "201", "202", "204", "205", "1208LS", "1208FS", "1208FS-Plus", "1408FS", "1408FS-Plus", "1608FS", "1608FS-Plus", "1608G", "1608GX", "1608GX-2AO", "1808", "1808X", "2408", "2408-2AO", "2416", "2416-4AO", "3101", "3102", "3103", "3104", "3105", "3106", "TC", "TEMP", "TC-AI", "TEMP-AI", "ERB08", "ERB24", "SSR08", "SSR24", "PDISO8" })
            models.Add(new("RedLab " + name, DaqSdk.UniversalLibrary, ["USB"]));
        foreach (var name in new[] { "E-1608", "E-DIO24", "E-TC" }) models.Add(new("RedLab " + name, DaqSdk.UniversalLibrary, ["Ethernet"]));
        models.Add(new("RedLab TC-32", DaqSdk.UniversalLibrary, ["USB", "Ethernet"]));
        models.Add(new(AutoUsb, DaqSdk.UniversalLibrary, ["USB"]));
        models.Add(new(AutoEthernet, DaqSdk.UniversalLibrary, ["Ethernet"]));
        return models;
    }
    public static bool IsDaq(string adapter) => adapter is LabJack or RedLab or Simulation;
    public static IReadOnlyList<string> Names(string adapter) => All.Where(model => (adapter == Simulation || (model.Sdk == DaqSdk.UniversalLibrary) == (adapter == RedLab))).Select(model => model.Name).ToArray();
    public static DaqModel Get(string adapter, string model)
        => All.FirstOrDefault(item => item.Name.Equals(model, StringComparison.OrdinalIgnoreCase) && (adapter == Simulation || (item.Sdk == DaqSdk.UniversalLibrary) == (adapter == RedLab)))
           ?? throw new InstrumentException($"Unbekanntes DAQ-Modell „{model}“. Für weitere UL-Produkte die automatische USB-/Ethernet-Erkennung wählen.");
    public static string Connection(DaqModel model, InstrumentOptions options)
    {
        var connection = options.Text("Verbindung") ?? model.Connections[0];
        return model.Connections.FirstOrDefault(value => value.Equals(connection, StringComparison.OrdinalIgnoreCase))
            ?? throw new InstrumentException($"{model.Name} unterstützt keine Verbindung „{connection}“. Erlaubt: {string.Join(", ", model.Connections)}.");
    }
    internal static int Index(string name, string prefix)
        => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(name[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : -1;
}
