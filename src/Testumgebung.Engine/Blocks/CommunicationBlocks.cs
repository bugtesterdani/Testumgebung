/**
 * @file
 * @brief Verbindet Kommunikationsparameter mit Übertragung und Antwortbewertung über abstrakte Ressourcen.
 */

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;

namespace Testumgebung.Engine.Blocks;

/// <summary>
/// Bausteine „Kommunikation“ (Abschnitt 1/7): Byte-Strom (RS-232/RS-485, TCP, UDP), Modbus RTU/TCP, CAN/CAN FD.
/// Zugriff über abstrakte Namen mit Reservierungsprüfung und Zugriffsprotokoll wie bei Messgeräten.
/// </summary>
public static class CommunicationBlocks
{
    public const string Category = "Kommunikation";
    public const string Send = "komm.senden";
    public const string Receive = "komm.empfangen";
    public const string Query = "komm.abfrage";
    public const string ModbusRead = "modbus.lesen";
    public const string ModbusWrite = "modbus.schreiben";
    public const string CanSend = "can.senden";
    public const string CanReceive = "can.empfangen";

    public static readonly string[] Formats = ["Text", "Hex"];
    public static readonly string[] LineEnds = ["\\r\\n", "\\n", "\\r", "Keins"];
    public static readonly string[] EndModes = ["Zeilenende", "Länge", "Ruhe"];
    public static readonly string[] TimeoutRatings = ["ERROR", "FAIL"];
    public static readonly string[] ModbusTypes = ["UInt16", "Int16", "UInt32", "Int32", "Float32", "Float64", "Bool", "Text"];
    public static readonly string[] WordOrders = ["Hoch zuerst", "Niedrig zuerst"];
    public static readonly string[] ByteOrders = ["Intel", "Motorola"];

    private static ParameterDescriptor Res(string example) => new("Ressource", "Ressource", ParameterKind.Resource, example, Required: true);

    private static IEnumerable<ParameterDescriptor> SendParameters() =>
    [
        new("Daten", "Daten", ParameterKind.Text, "*IDN?", Required: true,
            Hint: "Text mit \\r \\n \\xHH; {Ausdruck} fügt Werte ein, z. B. SET:VOLT {Sollspannung}. Hex: 01 03 00 00 00 02"),
        new("Format", "Format", ParameterKind.Choice, "Text", Choices: Formats),
        new("Zeilenende", "Zeilenende (Text)", ParameterKind.Choice, "\\r\\n", Choices: LineEnds),
    ];

    private static IEnumerable<ParameterDescriptor> ReceiveParameters() =>
    [
        new("Ende", "Antwortende", ParameterKind.Choice, "Zeilenende", Choices: EndModes,
            Hint: "Zeilenende (siehe oben), feste Länge oder Ruhe nach den letzten Bytes"),
        new("Laenge", "Länge [Bytes]", ParameterKind.Number),
        new("Zeitlimit", "Zeitlimit [ms]", ParameterKind.Number, "1000", Required: true),
        new("Muster", "Antwortmuster", ParameterKind.Text, Hint: "Regulärer Ausdruck; Gruppe 1 = Wert, z. B. ^VOLT ([-\\d.]+)$. Ohne Treffer: FAIL"),
        new("Ziel", "Zielvariable", ParameterKind.Variable),
        new("Untergrenze", "Untergrenze", ParameterKind.Expression),
        new("Obergrenze", "Obergrenze", ParameterKind.Expression),
        new("Einheit", "Einheit", ParameterKind.Text),
        new("BeiZeitlimit", "Bei Zeitüberschreitung", ParameterKind.Choice, "ERROR", Choices: TimeoutRatings),
    ];

    private static IEnumerable<ParameterDescriptor> Evaluation() =>
    [
        new("Ziel", "Zielvariable", ParameterKind.Variable),
        new("Untergrenze", "Untergrenze", ParameterKind.Expression),
        new("Obergrenze", "Obergrenze", ParameterKind.Expression),
        new("Einheit", "Einheit", ParameterKind.Text),
    ];

    public static void Register(BlockCatalog c)
    {
        NetworkBlocks.Register(c);
        c.Register(new(Send, "Senden", Category, "arrow",
            "Sendet Text oder Bytes an den Prüfling (RS-232/RS-485, TCP, UDP). Unbewertet.",
            [Res("Pruefling_Seriell"), .. NetworkBlocks.ConnectionParameters(), .. SendParameters()],
            p => new StreamAction(p.Text("Ressource"), SendSpec.From(p), null) { Endpoint = p.Optional("Adresse"), Options = p.Optional("Verbindungsoptionen") }));

        c.Register(new(Receive, "Empfangen", Category, "measure",
            "Empfängt eine Antwort (bis Zeilenende, Länge oder Ruhe), prüft optional ein Muster und bewertet einen enthaltenen Zahlenwert.",
            [Res("Pruefling_Seriell"), .. NetworkBlocks.ConnectionParameters(), new("Format", "Format", ParameterKind.Choice, "Text", Choices: Formats),
                new("Zeilenende", "Zeilenende (Text)", ParameterKind.Choice, "\\r\\n", Choices: LineEnds), .. ReceiveParameters()],
            p => new StreamAction(p.Text("Ressource"), null, ReceiveSpec.From(p)) { Endpoint = p.Optional("Adresse"), Options = p.Optional("Verbindungsoptionen") }));

        c.Register(new(Query, "Abfrage", Category, "measure",
            "Verwirft alte Eingangsdaten, sendet einen Befehl und wertet die Antwort aus (Muster, Zahlenwert, Grenzwerte).",
            [Res("Pruefling_Seriell"), .. NetworkBlocks.ConnectionParameters(), .. SendParameters(), .. ReceiveParameters()],
            p => new StreamAction(p.Text("Ressource"), SendSpec.From(p), ReceiveSpec.From(p)) { Endpoint = p.Optional("Adresse"), Options = p.Optional("Verbindungsoptionen") }));

        c.Register(new(ModbusRead, "Modbus lesen", Category, "measure",
            "Liest Register oder Bits (RTU/TCP), wandelt den Datentyp, skaliert (Wert × Faktor + Offset) und bewertet gegen Grenzwerte bzw. Sollwert.",
            [
                Res("Pruefling_Modbus"),
                new("Adresse", "Adresse", ParameterKind.Text, "HR0", Required: true, Hint: "HR100, IR5, C3, DI2 (0-basiert) oder 40101 (klassisch)"),
                new("Datentyp", "Datentyp", ParameterKind.Choice, "UInt16", Choices: ModbusTypes),
                new("Wortfolge", "Wortfolge (32/64 Bit)", ParameterKind.Choice, "Hoch zuerst", Choices: WordOrders),
                new("Laenge", "Textlänge [Zeichen]", ParameterKind.Number),
                new("Faktor", "Faktor", ParameterKind.Number, "1"),
                new("Offset", "Offset", ParameterKind.Number, "0"),
                new("Sollwert", "Sollwert", ParameterKind.Expression, Hint: "Gleichheit (z. B. für Status, Bool, Text); sonst Grenzwerte"),
                .. Evaluation(),
            ],
            p => new ModbusReadAction(p.Text("Ressource"), p.Text("Adresse"), ModbusCodec.ParseType(p.Choice("Datentyp")), p.Choice("Wortfolge") == WordOrders[1],
                (int?)p.Number("Laenge"), p.Number("Faktor") ?? 1, p.Number("Offset") ?? 0, p.Expr("Sollwert"),
                p.Optional("Ziel"), p.Expr("Untergrenze"), p.Expr("Obergrenze"), p.Text("Einheit"))));

        c.Register(new(ModbusWrite, "Modbus schreiben", Category, "arrow",
            "Schreibt einen Wert in Holding-Register oder Coils; optional Zurücklesen (Abweichung = FAIL).",
            [
                Res("Pruefling_Modbus"),
                new("Adresse", "Adresse", ParameterKind.Text, "HR0", Required: true, Hint: "HR… oder C…"),
                new("Datentyp", "Datentyp", ParameterKind.Choice, "UInt16", Choices: ModbusTypes),
                new("Wortfolge", "Wortfolge (32/64 Bit)", ParameterKind.Choice, "Hoch zuerst", Choices: WordOrders),
                new("Wert", "Wert", ParameterKind.Expression, "0", Required: true),
                new("Zuruecklesen", "Zurücklesen und vergleichen", ParameterKind.Bool, "false"),
            ],
            p => new ModbusWriteAction(p.Text("Ressource"), p.Text("Adresse"), ModbusCodec.ParseType(p.Choice("Datentyp")), p.Choice("Wortfolge") == WordOrders[1],
                p.Expr("Wert")!, p.Bool("Zuruecklesen"))));

        c.Register(new(CanSend, "CAN senden", Category, "arrow",
            "Sendet einen CAN- oder CAN-FD-Rahmen. Daten in Hex; {Ausdruck} fügt ein Byte ein.",
            [
                Res("Pruefling_CAN"),
                new("Id", "CAN-ID", ParameterKind.Text, "0x7E0", Required: true, Hint: "Hex mit 0x oder dezimal; > 0x7FF = 29 Bit"),
                new("Erweitert", "29-Bit-ID", ParameterKind.Bool, "false"),
                new("Fd", "CAN FD", ParameterKind.Bool, "false"),
                new("Brs", "Bitratenumschaltung (BRS)", ParameterKind.Bool, "false"),
                new("Daten", "Daten (Hex)", ParameterKind.Text, "02 01 0C"),
            ],
            p =>
            {
                CheckHex(p.Text("Daten"), "Daten");
                return new CanSendAction(p.Text("Ressource"), CanIdSpec.From(p, "Id", "Erweitert"), p.Bool("Fd"), p.Bool("Brs"), p.Text("Daten"));
            }));

        c.Register(new(CanReceive, "CAN empfangen", Category, "measure",
            "Wartet auf einen Rahmen (ID/Maske), optional nach einer Anfrage; prüft Daten oder liest ein Signal (Startbit, Länge, Byteorder, Faktor, Offset).",
            [
                Res("Pruefling_CAN"),
                new("Id", "CAN-ID", ParameterKind.Text, "0x7E8", Required: true),
                new("Erweitert", "29-Bit-ID", ParameterKind.Bool, "false"),
                new("Maske", "Maske", ParameterKind.Text, Hint: "leer = genau diese ID"),
                new("Zeitlimit", "Zeitlimit [ms]", ParameterKind.Number, "1000", Required: true),
                new("AnfrageId", "Anfrage: CAN-ID", ParameterKind.Text, Hint: "optional: vorher senden"),
                new("AnfrageDaten", "Anfrage: Daten (Hex)", ParameterKind.Text),
                new("Erwartet", "Erwartete Daten", ParameterKind.Text, Hint: "Hex, ** = beliebig, z. B. 04 41 0C ** **"),
                new("Startbit", "Signal: Startbit", ParameterKind.Number),
                new("Bits", "Signal: Länge [Bit]", ParameterKind.Number),
                new("Byteorder", "Signal: Byteorder", ParameterKind.Choice, "Intel", Choices: ByteOrders),
                new("Vorzeichen", "Signal: vorzeichenbehaftet", ParameterKind.Bool, "false"),
                new("Faktor", "Signal: Faktor", ParameterKind.Number, "1"),
                new("Offset", "Signal: Offset", ParameterKind.Number, "0"),
                .. Evaluation(),
                new("BeiZeitlimit", "Bei Zeitüberschreitung", ParameterKind.Choice, "ERROR", Choices: TimeoutRatings),
            ],
            p => new CanReceiveAction(p.Text("Ressource"), CanIdSpec.From(p, "Id", "Erweitert"), p.Optional("Maske"),
                TimeSpan.FromMilliseconds(p.Number("Zeitlimit") ?? 1000),
                p.Optional("AnfrageId") is { } rid ? CanIdSpec.Parse(rid, false) : null, p.Text("AnfrageDaten"),
                ExpectedBytes.Parse(p.Optional("Erwartet")),
                p.Number("Bits") is { } bits ? new CanSignal((int)(p.Number("Startbit") ?? 0), (int)bits, p.Choice("Byteorder") == ByteOrders[1], p.Bool("Vorzeichen"),
                    p.Number("Faktor") ?? 1, p.Number("Offset") ?? 0) : null,
                p.Optional("Ziel"), p.Expr("Untergrenze"), p.Expr("Obergrenze"), p.Text("Einheit"), p.Choice("BeiZeitlimit") == "FAIL")));
    }

    // ------------------------------------------------------------------ gemeinsame Hilfen

    /// <summary>Hexdaten ohne Platzhalter beim Konfigurieren prüfen.</summary>
    internal static void CheckHex(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('{')) return;
        try
        {
            _ = CommText.ParseHex(text);
        }
        catch (FormatException ex)
        {
            throw new BlockConfigurationException($"{label}: {ex.Message}");
        }
    }

    /// <summary>Platzhalter {Ausdruck} auswerten; {{ und }} für geschweifte Klammern.</summary>
    internal static string Interpolate(string template, IStepContext ctx, Func<object, string>? format = null)
    {
        if (!template.Contains('{')) return template;
        var sb = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var ch = template[i];
            if (ch == '{' && i + 1 < template.Length && template[i + 1] == '{') { sb.Append('{'); i++; continue; }
            if (ch == '}' && i + 1 < template.Length && template[i + 1] == '}') { sb.Append('}'); i++; continue; }
            if (ch != '{') { sb.Append(ch); continue; }
            var end = template.IndexOf('}', i + 1);
            if (end < 0) throw new FormatException($"Platzhalter ohne „}}“ in „{template}“.");
            var value = Expression.Parse(template[(i + 1)..end]).Evaluate(new ContextResolver(ctx));
            sb.Append(format is not null ? format(value) : Text(value));
            i = end;
        }
        return sb.ToString();
    }

    internal static string Text(object? value) => value switch
    {
        null => "",
        string s => s,
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    internal static byte[] LineEnd(string choice) => choice switch
    {
        "\\r\\n" => [13, 10],
        "\\n" => [10],
        "\\r" => [13],
        _ => [],
    };

    internal static double? ParseNumber(string text)
    {
        var t = text.Trim();
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.GetCultureInfo("de-DE"), out d)) return d;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return h;
        return null;
    }

    /// <summary>Wert passend zum Typ der Zielvariable setzen (Text → Zahl/Bool, falls die Variable so typisiert ist).</summary>
    internal static void SetTarget(IStepContext ctx, string target, object? value)
    {
        var type = ctx.VisibleVariables().Where(v => v.Name == target).Select(v => (VarType?)v.Type).FirstOrDefault();
        if (type is { } t && value is string s && t != VarType.String)
            value = TypedValue.Parse(t == VarType.Double && ParseNumber(s) is { } n ? n.ToString("R", CultureInfo.InvariantCulture) : s.Trim(), t);
        else if (type is { } t2 && value is double d && t2 == VarType.Int)
            value = (long)Math.Round(d);
        else if (type == VarType.String && value is not string and not null)
            value = Text(value);
        ctx.Set(target, value);
    }

    /// <summary>Bewertung eines Zahlenwerts gegen Grenzwerte aus Ausdrücken.</summary>
    internal static StepOutcome Rate(IStepContext ctx, double value, Expression? low, Expression? high, string? unit, string what)
    {
        var resolver = new ContextResolver(ctx);
        double? lo = low is null ? null : BlockCatalog.ToDouble(low.Evaluate(resolver), "Untergrenze");
        double? hi = high is null ? null : BlockCatalog.ToDouble(high.Evaluate(resolver), "Obergrenze");
        var u = string.IsNullOrWhiteSpace(unit) ? null : unit;
        if (lo is null && hi is null) return new StepOutcome(ResultRating.NResult, value, u, Message: what);
        var ok = (lo is null || value >= lo) && (hi is null || value <= hi);
        return new StepOutcome(ok ? ResultRating.Pass : ResultRating.Fail, value, u, lo, hi,
            ok ? what : $"{value.ToString("0.###", CultureInfo.InvariantCulture)} {u} außerhalb {lo}–{hi} {u} · {what}");
    }

    internal static StepOutcome Timeout(CommunicationTimeoutException ex, bool asFail, bool hex)
    {
        var partial = ex.Partial.Length > 0 ? $" Empfangen: „{CommText.Format(ex.Partial, hex)}“." : "";
        return new StepOutcome(asFail ? ResultRating.Fail : ResultRating.Error, ex.Partial.Length > 0 ? CommText.Format(ex.Partial, hex) : null,
            Message: ex.Message + partial);
    }

    internal static T As<T>(IHardwareAdapter adapter, string what) where T : class
        => adapter as T ?? throw new CommunicationException($"Gerät „{adapter.Name}“ ist keine {what}-Schnittstelle.");
}

// ---------------------------------------------------------------------- Byte-Strom

internal sealed record SendSpec(string Data, bool Hex, byte[] LineEnd)
{
    public static SendSpec From(BlockParameters p)
    {
        var hex = p.Choice("Format") == "Hex";
        var data = p.Text("Daten");
        if (hex) CommunicationBlocks.CheckHex(data, "Daten"); // früh prüfen
        return new SendSpec(data, hex, hex ? [] : CommunicationBlocks.LineEnd(p.Choice("Zeilenende")));
    }

    public byte[] Build(IStepContext ctx)
    {
        var text = CommunicationBlocks.Interpolate(Data, ctx, Hex ? v => Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString("X2", CultureInfo.InvariantCulture) : null);
        var bytes = CommText.Parse(text, Hex);
        return [.. bytes, .. LineEnd];
    }
}

internal sealed record ReceiveSpec(bool Hex, string EndMode, byte[] LineEnd, int? Length, TimeSpan Timeout, Regex? Pattern, string? Target,
    Expression? Low, Expression? High, string Unit, bool TimeoutAsFail)
{
    public static ReceiveSpec From(BlockParameters p)
    {
        var hex = p.Choice("Format") == "Hex";
        var end = p.Choice("Ende");
        var length = (int?)p.Number("Laenge");
        if (end == "Länge" && length is not > 0) throw new BlockConfigurationException("Antwortende „Länge“: Länge in Bytes angeben.");
        if (end == "Zeilenende" && (hex || p.Choice("Zeilenende") == "Keins"))
            throw new BlockConfigurationException("Antwortende „Zeilenende“ braucht Format Text und ein Zeilenende – sonst „Länge“ oder „Ruhe“.");
        Regex? pattern = null;
        if (p.Optional("Muster") is { } m)
        {
            try
            {
                pattern = new Regex(m, RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new BlockConfigurationException($"Antwortmuster: {ex.Message}");
            }
        }
        return new ReceiveSpec(hex, end, CommunicationBlocks.LineEnd(p.Choice("Zeilenende")), length,
            TimeSpan.FromMilliseconds(p.Number("Zeitlimit") ?? 1000), pattern, p.Optional("Ziel"), p.Expr("Untergrenze"), p.Expr("Obergrenze"),
            p.Text("Einheit"), p.Choice("BeiZeitlimit") == "FAIL");
    }

    public ReceiveRule Rule => EndMode switch
    {
        "Zeilenende" => new ReceiveRule(LineEnd, null, Timeout),
        "Länge" => new ReceiveRule(null, Length, Timeout),
        _ => new ReceiveRule(null, null, Timeout),
    };
}

/// <summary>Senden, Empfangen oder Abfrage (Senden nach Verwerfen alter Eingangsdaten, dann Empfangen).</summary>
internal sealed class StreamAction(string resource, SendSpec? send, ReceiveSpec? receive) : IStepAction
{
    public string? Endpoint { get; init; }
    public string? Options { get; init; }
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        var request = send?.Build(context);
        var operation = send is not null && receive is not null ? "Abfrage" : send is not null ? "Senden" : "Empfangen";
        byte[] response;
        try
        {
            response = await context.UseDeviceAsync(resource, operation, async (adapter, channel, token) =>
            {
                var stream = CommunicationBlocks.As<IByteStreamAdapter>(adapter, "Byte-Strom");
                await NetworkBlocks.ConfigureAsync(adapter, Endpoint, Options, context, token).ConfigureAwait(false);
                if (request is not null)
                {
                    if (receive is not null) stream.DiscardInput(channel);
                    await stream.SendAsync(channel, request, token).ConfigureAwait(false);
                }
                return receive is null ? [] : await stream.ReceiveAsync(channel, receive.Rule, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (CommunicationTimeoutException ex) when (receive is not null)
        {
            return CommunicationBlocks.Timeout(ex, receive.TimeoutAsFail, receive.Hex);
        }
        if (receive is null) return StepOutcome.Unrated(CommText.Format(request!, send!.Hex));
        return Evaluate(context, response, receive, request is null ? null : CommText.Trace(request));
    }

    private static StepOutcome Evaluate(IStepContext ctx, byte[] response, ReceiveSpec spec, string? request)
    {
        var shown = CommText.Format(response, spec.Hex);
        var text = spec.Hex ? shown : CommText.Raw(response);
        var info = request is null ? $"Antwort „{shown}“" : $"„{request}“ → „{shown}“";
        string extracted = text;
        var matched = false;
        if (spec.Pattern is { } pattern)
        {
            var m = pattern.Match(text);
            if (!m.Success)
            {
                if (spec.Target is not null) CommunicationBlocks.SetTarget(ctx, spec.Target, shown);
                return new StepOutcome(ResultRating.Fail, shown, Message: $"Antwort passt nicht zum Muster {pattern} · {info}");
            }
            matched = true;
            extracted = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
        }
        if (spec.Low is not null || spec.High is not null)
            return EvaluateNumber(ctx, extracted, spec, info);
        if (spec.Target is not null) CommunicationBlocks.SetTarget(ctx, spec.Target, extracted);
        object value = CommunicationBlocks.ParseNumber(extracted) is { } n && !string.IsNullOrWhiteSpace(spec.Unit) ? n : extracted;
        return new StepOutcome(matched ? ResultRating.Pass : ResultRating.NResult, value, string.IsNullOrWhiteSpace(spec.Unit) ? null : spec.Unit, Message: info);
    }
    private static StepOutcome EvaluateNumber(IStepContext ctx, string extracted, ReceiveSpec spec, string info)
    {
        if (CommunicationBlocks.ParseNumber(extracted) is not { } number)
            return new StepOutcome(ResultRating.Fail, extracted, Message: $"Kein Zahlenwert in der Antwort · {info}");
        if (spec.Target is not null) CommunicationBlocks.SetTarget(ctx, spec.Target, number);
        return CommunicationBlocks.Rate(ctx, number, spec.Low, spec.High, spec.Unit, info);
    }

}

// ---------------------------------------------------------------------- Modbus

internal static class ModbusAddress
{
    public static (ModbusArea Area, int Address) Parse(string text)
    {
        try
        {
            return ModbusCodec.ParseAddress(text);
        }
        catch (FormatException ex)
        {
            throw new BlockConfigurationException(ex.Message);
        }
    }
}

internal sealed class ModbusReadAction : IStepAction
{
    private readonly string _resource;
    private readonly ModbusArea _area;
    private readonly int _address;
    private readonly ModbusDataType _type;
    private readonly bool _lowFirst;
    private readonly int _textLength;
    private readonly double _factor;
    private readonly double _offset;
    private readonly Expression? _expected;
    private readonly string? _target;
    private readonly Expression? _low;
    private readonly Expression? _high;
    private readonly string _unit;
    private readonly string _label;

    public ModbusReadAction(string resource, string address, ModbusDataType type, bool lowFirst, int? textLength, double factor, double offset,
        Expression? expected, string? target, Expression? low, Expression? high, string unit)
    {
        (_area, _address) = ModbusAddress.Parse(address);
        if (_area is ModbusArea.Coil or ModbusArea.DiscreteInput) type = ModbusDataType.Bool;
        if (type == ModbusDataType.Text && textLength is not > 0) throw new BlockConfigurationException("Datentyp Text: Textlänge angeben.");
        (_resource, _type, _lowFirst, _textLength, _factor, _offset, _expected, _target, _low, _high, _unit) =
            (resource, type, lowFirst, textLength ?? 0, factor, offset, expected, target, low, high, unit);
        _label = $"{ModbusCodec.AreaPrefix(_area)}{_address}";
    }

    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        var raw = await context.UseDeviceAsync(_resource, "Modbus lesen " + _label, async (adapter, channel, token) =>
        {
            var modbus = CommunicationBlocks.As<IModbusAdapter>(adapter, "Modbus");
            if (_area is ModbusArea.Coil or ModbusArea.DiscreteInput)
                return (object)(await modbus.ReadBitsAsync(channel, _area, _address, 1, token).ConfigureAwait(false))[0];
            var regs = await modbus.ReadRegistersAsync(channel, _area, _address, ModbusCodec.Registers(_type, _textLength), token).ConfigureAwait(false);
            return ModbusCodec.Decode(regs, _type, _lowFirst);
        }).ConfigureAwait(false);

        object value = raw switch
        {
            long l when _factor != 1 || _offset != 0 => l * _factor + _offset,
            double d => d * _factor + _offset,
            _ => raw,
        };
        if (_target is not null) CommunicationBlocks.SetTarget(context, _target, value);
        var unit = string.IsNullOrWhiteSpace(_unit) ? null : _unit;
        if (_expected is not null)
        {
            return RateExpected(context, value, unit);
        }
        if (value is long or double && (_low is not null || _high is not null))
            return CommunicationBlocks.Rate(context, Convert.ToDouble(value, CultureInfo.InvariantCulture), _low, _high, _unit, _label);
        return new StepOutcome(ResultRating.NResult, value, unit, Message: _label);
    }
    private StepOutcome RateExpected(IStepContext context, object value, string? unit)
    {
        var expected = _expected!.Evaluate(new ContextResolver(context));
        var ok = value is double or long && expected is double or long
            ? Math.Abs(Convert.ToDouble(value, CultureInfo.InvariantCulture) - Convert.ToDouble(expected, CultureInfo.InvariantCulture)) < 1e-9
            : string.Equals(CommunicationBlocks.Text(value), CommunicationBlocks.Text(expected), StringComparison.Ordinal);
        return new StepOutcome(ok ? ResultRating.Pass : ResultRating.Fail, value, unit,
            Message: ok ? $"{_label} = {CommunicationBlocks.Text(value)}" : $"{_label} = {CommunicationBlocks.Text(value)}, erwartet {CommunicationBlocks.Text(expected)}");
    }

}

internal sealed class ModbusWriteAction : IStepAction
{
    private readonly string _resource;
    private readonly ModbusArea _area;
    private readonly int _address;
    private readonly ModbusDataType _type;
    private readonly bool _lowFirst;
    private readonly Expression _value;
    private readonly bool _verify;

    public ModbusWriteAction(string resource, string address, ModbusDataType type, bool lowFirst, Expression value, bool verify)
    {
        (_area, _address) = ModbusAddress.Parse(address);
        if (_area is not (ModbusArea.HoldingRegister or ModbusArea.Coil))
            throw new BlockConfigurationException("Schreiben nur in Holding-Register (HR) oder Coils (C).");
        if (_area == ModbusArea.Coil) type = ModbusDataType.Bool;
        (_resource, _type, _lowFirst, _value, _verify) = (resource, type, lowFirst, value, verify);
    }

    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        var value = _value.Evaluate(new ContextResolver(context));
        var label = $"{ModbusCodec.AreaPrefix(_area)}{_address}";
        ushort[] regs;
        try
        {
            regs = _area == ModbusArea.Coil ? [] : ModbusCodec.Encode(value, _type, _lowFirst);
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            return StepOutcome.Error($"{label}: Wert {CommunicationBlocks.Text(value)} passt nicht in {_type} ({ex.Message}).");
        }
        var mismatch = await context.UseDeviceAsync(_resource, "Modbus schreiben " + label, async (adapter, channel, token) =>
        {
            var modbus = CommunicationBlocks.As<IModbusAdapter>(adapter, "Modbus");
            if (_area == ModbusArea.Coil)
            {
                var bit = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                await modbus.WriteBitsAsync(channel, _address, [bit], token).ConfigureAwait(false);
                if (!_verify) return null;
                var back = (await modbus.ReadBitsAsync(channel, ModbusArea.Coil, _address, 1, token).ConfigureAwait(false))[0];
                return back == bit ? null : $"zurückgelesen {(back ? 1 : 0)}";
            }
            await modbus.WriteRegistersAsync(channel, _address, regs, token).ConfigureAwait(false);
            if (!_verify) return null;
            var read = await modbus.ReadRegistersAsync(channel, ModbusArea.HoldingRegister, _address, regs.Length, token).ConfigureAwait(false);
            return read.SequenceEqual(regs) ? null : $"zurückgelesen {CommunicationBlocks.Text(ModbusCodec.Decode(read, _type, _lowFirst))}";
        }).ConfigureAwait(false);
        return mismatch is null
            ? StepOutcome.Unrated(value, $"{label} ← {CommunicationBlocks.Text(value)}")
            : new StepOutcome(ResultRating.Fail, value, Message: $"{label} ← {CommunicationBlocks.Text(value)}, {mismatch}");
    }
}

// ---------------------------------------------------------------------- CAN

internal sealed record CanIdSpec(uint Id, bool Extended)
{
    public static CanIdSpec From(BlockParameters p, string id, string extended) => Parse(p.Text(id), p.Bool(extended));

    public static CanIdSpec Parse(string text, bool extended)
    {
        var t = text.Trim();
        var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
            : uint.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        if (!ok) throw new BlockConfigurationException($"CAN-ID „{text}“ ungültig (z. B. 0x7E0 oder 2016).");
        var ext = extended || id > 0x7FF;
        if (id > 0x1FFFFFFF) throw new BlockConfigurationException($"CAN-ID 0x{id:X} größer als 29 Bit.");
        return new CanIdSpec(id, ext);
    }
}

/// <summary>Erwartete Bytes mit Platzhaltern „**“.</summary>
internal sealed record ExpectedBytes(byte?[] Bytes)
{
    public static ExpectedBytes? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return new ExpectedBytes(text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b is "**" or "??" ? (byte?)null : byte.Parse(b.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? b[2..] : b, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToArray());
        }
        catch (FormatException)
        {
            throw new BlockConfigurationException($"Erwartete Daten „{text}“: Hexbytes mit Leerzeichen, ** = beliebig.");
        }
    }

    public bool Matches(byte[] data) => data.Length == Bytes.Length && Bytes.Select((b, i) => b is null || b == data[i]).All(x => x);

    public override string ToString() => string.Join(" ", Bytes.Select(b => b?.ToString("X2", CultureInfo.InvariantCulture) ?? "**"));
}

internal sealed class CanSendAction(string resource, CanIdSpec id, bool fd, bool brs, string data) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        var bytes = CommText.ParseHex(CommunicationBlocks.Interpolate(data, context,
            v => Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString("X2", CultureInfo.InvariantCulture)));
        var frame = new CanFrame(id.Id, bytes, id.Extended, fd, fd && brs);
        if (frame.Validate() is { } error) return StepOutcome.Error(error);
        await context.UseDeviceAsync(resource, "CAN senden", async (adapter, channel, token) =>
        {
            await CommunicationBlocks.As<ICanAdapter>(adapter, "CAN").SendAsync(channel, frame, token).ConfigureAwait(false);
            return 0;
        }).ConfigureAwait(false);
        return StepOutcome.Unrated(frame.ToString());
    }
}

internal sealed class CanReceiveAction : IStepAction
{
    private readonly string _resource;
    private readonly CanFilter _filter;
    private readonly TimeSpan _timeout;
    private readonly CanIdSpec? _requestId;
    private readonly string _requestData;
    private readonly ExpectedBytes? _expected;
    private readonly CanSignal? _signal;
    private readonly string? _target;
    private readonly Expression? _low;
    private readonly Expression? _high;
    private readonly string _unit;
    private readonly bool _timeoutAsFail;

    public CanReceiveAction(string resource, CanIdSpec id, string? mask, TimeSpan timeout, CanIdSpec? requestId, string requestData,
        ExpectedBytes? expected, CanSignal? signal, string? target, Expression? low, Expression? high, string unit, bool timeoutAsFail)
    {
        var m = mask is null ? 0x1FFFFFFFu : CanIdSpec.Parse(mask, true).Id;
        _filter = new CanFilter(id.Id, m, id.Extended);
        if (requestId is null && !string.IsNullOrWhiteSpace(requestData)) throw new BlockConfigurationException("Anfrage-Daten ohne Anfrage-ID.");
        CommunicationBlocks.CheckHex(requestData, "Anfrage: Daten");
        if (signal is { Length: < 1 or > 64 }) throw new BlockConfigurationException("Signallänge 1–64 Bit.");
        (_resource, _timeout, _requestId, _requestData, _expected, _signal, _target, _low, _high, _unit, _timeoutAsFail) =
            (resource, timeout, requestId, requestData, expected, signal, target, low, high, unit, timeoutAsFail);
    }

    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        CanFrame? request = null;
        if (_requestId is not null)
        {
            var bytes = CommText.ParseHex(CommunicationBlocks.Interpolate(_requestData, context,
                v => Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString("X2", CultureInfo.InvariantCulture)));
            request = new CanFrame(_requestId.Id, bytes, _requestId.Extended);
            if (request.Validate() is { } error) return StepOutcome.Error(error);
        }
        CanFrame frame;
        try
        {
            frame = await context.UseDeviceAsync(_resource, request is null ? "CAN empfangen" : "CAN Abfrage", async (adapter, channel, token) =>
            {
                var can = CommunicationBlocks.As<ICanAdapter>(adapter, "CAN");
                if (request is not null)
                {
                    can.DiscardInput(channel);
                    await can.SendAsync(channel, request, token).ConfigureAwait(false);
                }
                return await can.ReceiveAsync(channel, _filter, _timeout, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (CommunicationTimeoutException ex)
        {
            return CommunicationBlocks.Timeout(ex, _timeoutAsFail, hex: true);
        }

        var shown = frame.ToString();
        if (_expected is not null && !_expected.Matches(frame.Data))
            return new StepOutcome(ResultRating.Fail, frame.DataHex, Message: $"{shown} – erwartet {_expected}");
        if (_signal is not null)
        {
            double value;
            try
            {
                value = _signal.Value(frame.Data);
            }
            catch (ArgumentException ex)
            {
                return new StepOutcome(ResultRating.Fail, frame.DataHex, Message: $"{shown}: {ex.Message}");
            }
            if (_target is not null) CommunicationBlocks.SetTarget(context, _target, value);
            var rated = CommunicationBlocks.Rate(context, value, _low, _high, _unit, shown);
            return _expected is not null && rated.Rating == ResultRating.NResult ? rated with { Rating = ResultRating.Pass } : rated;
        }
        if (_target is not null) CommunicationBlocks.SetTarget(context, _target, frame.DataHex);
        return new StepOutcome(_expected is not null ? ResultRating.Pass : ResultRating.NResult, frame.DataHex, Message: shown);
    }
}
