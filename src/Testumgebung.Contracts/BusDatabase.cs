/**
 * @file
 * @brief Parst Busbeschreibungen und ordnet Nachrichten, Signale und LIN-Gruppen ihren Protokollkennungen zu.
 */

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Testumgebung.Contracts;

/// <summary>Format einer Busbeschreibung.</summary>
public enum BusDatabaseFormat
{
    /// <summary>CAN-Datenbank (Vector DBC), auch J1939 und CAN FD.</summary>
    Dbc,
    /// <summary>LIN Description File (LDF, LIN 2.x).</summary>
    Ldf,
}

/// <summary>Busbeschreibung fehlerhaft.</summary>
public sealed class BusDatabaseException(string message) : FormatException(message);

/// <summary>Art eines LIN-Frames.</summary>
public enum LinFrameKind { Unconditional, EventTriggered, Sporadic }

/// <summary>Zusammengesetzter LIN-Frame (ereignisgesteuert bzw. sporadisch) mit seinen Einzelframes.</summary>
public sealed record BusFrameGroup(string Name, LinFrameKind Kind, IReadOnlyList<string> Members)
{
    /// <summary>Kollisionsauflösende ID (ereignisgesteuert) bzw. 0.</summary>
    public uint Id { get; init; }
}

/// <summary>Signal einer Botschaft: Kodierung, Einheit, Bereich, Multiplex, Wertetabelle, Startwert.</summary>
public sealed class BusSignal
{
    public BusSignal(string name, CanSignal coding)
    {
        Name = name;
        Coding = coding;
    }

    public string Name { get; }
    public CanSignal Coding { get; internal set; }
    public string Unit { get; internal set; } = "";
    public double? Minimum { get; internal set; }
    public double? Maximum { get; internal set; }

    /// <summary>Multiplexer-Signal (DBC „M“).</summary>
    public bool IsMultiplexer { get; internal set; }

    /// <summary>Nur gültig, wenn der Multiplexer diesen Wert hat (DBC „m3“).</summary>
    public long? MuxValue { get; internal set; }

    /// <summary>Wertetabelle (Rohwert → Text).</summary>
    public Dictionary<long, string> Values { get; } = [];

    public string? Comment { get; internal set; }

    /// <summary>Startwert (Rohwert), sonst 0.</summary>
    public ulong InitialRaw { get; internal set; }

    public double Physical(ReadOnlySpan<byte> data) => Coding.Value(data);

    /// <summary>Text aus der Wertetabelle zum Rohwert der Daten oder null.</summary>
    public string? Describe(ReadOnlySpan<byte> data)
        => Values.Count > 0 && !Coding.IsFloat && Values.TryGetValue(Coding.Raw(data), out var text) ? text : null;

    /// <summary>Wert aus Text: Zahl (physikalisch) oder Eintrag der Wertetabelle.</summary>
    public double ParseValue(string text)
    {
        var text2 = text.Trim().Trim('"');
        if (double.TryParse(text2, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        foreach (var (raw, label) in Values)
            if (string.Equals(label, text2, StringComparison.OrdinalIgnoreCase)) return raw * Coding.Factor + Coding.Offset;
        throw new FormatException($"Signal „{Name}“: „{text}“ ist weder Zahl noch Eintrag der Wertetabelle ({string.Join(", ", Values.Values)}).");
    }

    /// <summary>Prüft, ob der physikalische Wert kodierbar ist; Fehlermeldung oder null.</summary>
    public string? CheckRange(double physical)
    {
        if (Coding.IsFloat) return null;
        var raw = Math.Round((physical - Coding.Offset) / Coding.Factor);
        var (min, max) = Coding.RawRange;
        return raw < min || raw > max
            ? $"Signal „{Name}“: {physical.ToString(CultureInfo.InvariantCulture)} {Unit} nicht darstellbar ({Coding.Length} Bit, Faktor {Coding.Factor.ToString(CultureInfo.InvariantCulture)}, Offset {Coding.Offset.ToString(CultureInfo.InvariantCulture)})."
            : null;
    }

    public override string ToString() => $"{Name} ({Coding.StartBit}|{Coding.Length}{(Coding.Motorola ? " Motorola" : "")}{(string.IsNullOrEmpty(Unit) ? "" : ", " + Unit)})";
}

/// <summary>Botschaft (CAN-Rahmen bzw. LIN-Frame) mit Signalen.</summary>
public sealed class BusMessage
{
    public BusMessage(uint id, bool extended, string name, int length, string sender)
    {
        Id = id;
        Extended = extended;
        Name = name;
        Length = length;
        Sender = sender;
    }

    public uint Id { get; }
    public bool Extended { get; }
    public string Name { get; }
    public int Length { get; }
    public string Sender { get; }
    public List<BusSignal> Signals { get; } = [];
    public int? CycleMs { get; internal set; }
    public string? Comment { get; internal set; }

    /// <summary>CAN FD (Länge &gt; 8 oder Attribut VFrameFormat).</summary>
    public bool Fd { get; internal set; }

    /// <summary>J1939-Parametergruppe (aus der ID ohne Priorität, Zieladresse und Quelladresse).</summary>
    public uint Pgn => J1939.PgnOf(Id);

    public BusSignal? Multiplexer => Signals.FirstOrDefault(busSignal => busSignal.IsMultiplexer);

    public BusSignal? Signal(string name)
        => Signals.FirstOrDefault(busSignal => busSignal.Name == name) ?? Signals.FirstOrDefault(busSignal => string.Equals(busSignal.Name, name, StringComparison.OrdinalIgnoreCase));

    public BusSignal RequireSignal(string name)
        => Signal(name) ?? throw new BusDatabaseException($"Signal „{name}“ fehlt in Botschaft „{Name}“ ({string.Join(", ", Signals.Select(busSignal => busSignal.Name))}).");

    /// <summary>Signal gilt in diesen Daten (ohne Multiplex oder mit passendem Multiplexerwert).</summary>
    public bool IsActive(BusSignal signal, ReadOnlySpan<byte> data)
        => signal.MuxValue is not { } mux || Multiplexer is not { } busSignal || busSignal.Coding.Raw(data) == mux;

    /// <summary>Alle gültigen Signale mit Wert und Text der Wertetabelle.</summary>
    public IEnumerable<(BusSignal Signal, double Value, string? Text)> Decode(byte[] data)
    {
        foreach (var busSignal in Signals)
        {
            if (!IsActive(busSignal, data)) continue;
            double value;
            string? text;
            try
            {
                value = busSignal.Physical(data);
                text = busSignal.Describe(data);
            }
            catch (ArgumentException)
            {
                continue; // kürzerer Rahmen als beschrieben
            }
            yield return (busSignal, value, text);
        }
    }

    /// <summary>
    /// Daten aus Signalwerten (physikalisch). Nicht genannte Signale erhalten ihren Startwert; für gemultiplexte Signale wird
    /// der Multiplexer gesetzt, sofern er nicht selbst angegeben ist.
    /// </summary>
    public byte[] Encode(IReadOnlyDictionary<string, double> values, int? length = null)
    {
        var data = new byte[length ?? Length];
        var given = values.ToDictionary(kv => RequireSignal(kv.Key), kv => kv.Value);
        var mux = Multiplexer;
        var muxValue = ResolveMultiplexer(given, mux);
        foreach (var busSignal in Signals)
        {
            if (busSignal.MuxValue is { } own && own != (muxValue ?? (long)(mux?.InitialRaw ?? 0))) continue;
            if (busSignal.IsMultiplexer && muxValue is { } mval) busSignal.Coding.WriteRaw(data, unchecked((ulong)mval));
            else busSignal.Coding.WriteRaw(data, busSignal.InitialRaw);
        }
        foreach (var (busSignal2, value) in given)
        {
            if (busSignal2.CheckRange(value) is { } error) throw new BusDatabaseException(error);
            busSignal2.Coding.Write(data, value);
        }
        return data;
    }

    private static long? ResolveMultiplexer(Dictionary<BusSignal, double> given, BusSignal? mux)
    {
        long? muxValue = mux is not null && given.TryGetValue(mux, out var mv) ? (long)Math.Round((mv - mux.Coding.Offset) / mux.Coding.Factor) : null;
        foreach (var busSignal in given.Keys.Where(busSignal2 => busSignal2.MuxValue is not null))
        {
            if (muxValue is { } number && number != busSignal.MuxValue)
                throw new BusDatabaseException($"Signal „{busSignal.Name}“ gilt nur bei Multiplexer {busSignal.MuxValue}, angegeben ist {number}.");
            muxValue = busSignal.MuxValue;
        }
        return muxValue;
    }

    public override string ToString() => $"{Name} (0x{Id:X}{(Extended ? " 29 Bit" : "")}, {Length} Bytes)";
}

/// <summary>
/// Importierte Busbeschreibung (DBC für CAN/CAN FD/J1939, LDF für LIN): Botschaften, Signale, Wertetabellen.
/// Unbekannte Abschnitte werden übersprungen und als Hinweis geführt.
/// </summary>
public sealed class BusDatabase
{
    private BusDatabase(BusDatabaseFormat format) => Format = format;

    public BusDatabaseFormat Format { get; }
    public List<BusMessage> Messages { get; } = [];
    /// <summary>LIN: Ereignisgesteuerte und sporadische Frames (Name → zugehörige Einzelframes, in Fahrplanreihenfolge).</summary>
    public List<BusFrameGroup> FrameGroups { get; } = [];
    public List<string> Nodes { get; } = [];
    public List<string> Warnings { get; } = [];
    public string? Version { get; private set; }

    /// <summary>Protokoll laut Datenbank (z. B. „J1939“).</summary>
    public string? Protocol { get; private set; }

    /// <summary>LIN: Bitrate [bit/s]; DBC: BusSpeed, falls angegeben.</summary>
    public int? Bitrate { get; private set; }

    /// <summary>LIN: Name des Masterknotens.</summary>
    public string? MasterNode { get; private set; }

    public bool IsJ1939 => string.Equals(Protocol, "J1939", StringComparison.OrdinalIgnoreCase);

    public string Summary
        => $"{Format.ToString().ToUpperInvariant()}: {Messages.Count} Botschaft(en), {Messages.Sum(busMessage => busMessage.Signals.Count)} Signal(e)"
           + (Protocol is { } path ? $", {path}" : "") + (Bitrate is { } bit ? $", {bit / 1000.0:0.#} kbit/s" : "");

    /// <summary>Botschaft über Namen oder ID („0x123“, „291“).</summary>
    public BusMessage? Message(string nameOrId)
    {
        var text = nameOrId.Trim();
        var byName = Messages.FirstOrDefault(busMessage => busMessage.Name == text) ?? Messages.FirstOrDefault(busMessage => string.Equals(busMessage.Name, text, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;
        uint id;
        var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
            : uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        return ok ? Messages.FirstOrDefault(busMessage => busMessage.Id == id) : null;
    }

    public BusMessage RequireMessage(string nameOrId)
        => Message(nameOrId) ?? throw new BusDatabaseException($"Botschaft „{nameOrId}“ ist nicht in der Datenbank.");

    /// <summary>J1939: Botschaft zur Parametergruppe.</summary>
    public BusMessage? ByPgn(uint pgn) => Messages.FirstOrDefault(busMessage => busMessage.Extended && busMessage.Pgn == pgn);

    public BusFrameGroup? FrameGroup(string name)
        => FrameGroups.FirstOrDefault(busFrameGroup => busFrameGroup.Name == name) ?? FrameGroups.FirstOrDefault(busFrameGroup => string.Equals(busFrameGroup.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Signal über „Botschaft.Signal“ oder eindeutigen Signalnamen.</summary>
    public (BusMessage Message, BusSignal Signal) FindSignal(string reference)
    {
        var dot = reference.IndexOf('.');
        if (dot > 0)
        {
            var busMessage = RequireMessage(reference[..dot]);
            return (busMessage, busMessage.RequireSignal(reference[(dot + 1)..]));
        }
        var hits = Messages.SelectMany(busMessage2 => busMessage2.Signals.Where(busSignal => string.Equals(busSignal.Name, reference, StringComparison.OrdinalIgnoreCase)).Select(busSignal => (m: busMessage2, s: busSignal))).ToList();
        return hits.Count switch
        {
            1 => hits[0],
            0 => throw new BusDatabaseException($"Signal „{reference}“ ist nicht in der Datenbank."),
            _ => throw new BusDatabaseException($"Signal „{reference}“ kommt in mehreren Botschaften vor ({string.Join(", ", hits.Select(entry => entry.m.Name))}) – „Botschaft.Signal“ angeben."),
        };
    }

    public static BusDatabaseFormat DetectFormat(string fileName, string text)
    {
        if (fileName.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase) || text.Contains("LIN_description_file", StringComparison.Ordinal)) return BusDatabaseFormat.Ldf;
        return BusDatabaseFormat.Dbc;
    }

    public static BusDatabase Parse(string text, BusDatabaseFormat format)
        => format == BusDatabaseFormat.Ldf ? ParseLdf(text) : ParseDbc(text);

    // ================================================================== DBC

    private static readonly Regex DbcMessage = new(@"^BO_\s+(\d+)\s+(\w+)\s*:\s*(\d+)\s+(\w+)", RegexOptions.CultureInvariant);

    private static readonly Regex DbcSignal = new(
        @"^SG_\s+(\w+)\s*(M|m\d+M?)?\s*:\s*(\d+)\s*\|\s*(\d+)\s*@\s*([01])\s*([+-])\s*\(\s*([^,]+?)\s*,\s*([^)]+?)\s*\)\s*\[\s*([^|\]]*?)\s*\|\s*([^\]]*?)\s*\]\s*""((?:[^""\\]|\\.)*)""\s*(.*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Quoted = new(@"(-?\d+)\s+""((?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant);

    private static double Num(string text) => double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Anweisungen über mehrere Zeilen (CM_, VAL_, BA_ …) bis zum „;“ außerhalb von Anführungszeichen.</summary>
    private static IEnumerable<(int Line, string Text)> DbcStatements(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string[] multi = ["CM_", "VAL_", "BA_", "BA_DEF_", "BA_DEF_DEF_", "SIG_VALTYPE_", "VAL_TABLE_", "SIG_GROUP_", "BO_TX_BU_", "EV_", "ENVVAR_DATA_", "SIG_TYPE_REF_", "SG_MUL_VAL_"];
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var keyword = line.Split([' ', '\t', ':'], 2)[0];
            if (!multi.Contains(keyword) || keyword == line) // einzelnes Schlüsselwort: Liste unter NS_
            {
                yield return (index + 1, line);
                continue;
            }
            var start = index;
            var sb = new StringBuilder(line);
            while (!Complete(sb.ToString()) && index + 1 < lines.Length) sb.Append('\n').Append(lines[++index]);
            yield return (start + 1, sb.ToString());
        }

        static bool Complete(string text2)
        {
            var quoted = false;
            for (var previousIndex = 0; previousIndex < text2.Length; previousIndex++)
            {
                if (text2[previousIndex] == '\\' && quoted) { previousIndex++; continue; }
                if (text2[previousIndex] == '"') quoted = !quoted;
                else if (text2[previousIndex] == ';' && !quoted) return true;
            }
            return false;
        }
    }

    private static string Unescape(string text) => text.Replace("\\\"", "\"").Replace("\\\\", "\\");

    private static BusDatabase ParseDbc(string text)
    {
        var db = new BusDatabase(BusDatabaseFormat.Dbc);
        BusMessage? current = null;
        var byId = new Dictionary<uint, BusMessage>();
        var floatTypes = new List<(uint Raw, string Signal, int Type)>();
        var startValues = new List<(uint Raw, string Signal, double Value)>();
        var frameFormats = new List<(uint Raw, int Format)>();
        void ReadDbcVersion(string statement)
        {
            var index2 = statement.IndexOf('"');
            db.Version = index2 >= 0 ? statement[(index2 + 1)..].TrimEnd().TrimEnd('"') : null;
        }

        void ReadDbcNodes(string statement)
        {
            db.Nodes.AddRange(statement[(statement.IndexOf(':') + 1)..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
            current = null;
        }

        void ReadDbcMessage(string statement)
        {
            var match = DbcMessage.Match(statement);
            if (!match.Success) throw new BusDatabaseException("BO_ unvollständig („BO_ ID Name: Länge Sender“).");
            var raw = uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (raw == 0xC0000000) // VECTOR__INDEPENDENT_SIG_MSG: Pseudo-Botschaft
            {
                current = null;
                return;
            }
            var extended = (raw & 0x80000000) != 0;
            var length = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            current = new BusMessage(raw & 0x1FFFFFFF, extended, match.Groups[2].Value, length, match.Groups[4].Value) { Fd = length > 8 };
            db.Messages.Add(current);
            byId[raw] = current;
        }

        void ReadDbcSignal(string statement)
        {
            if (current is null) return; // Signal der Pseudo-Botschaft
            var match2 = DbcSignal.Match(statement);
            if (!match2.Success) throw new BusDatabaseException("SG_ nicht lesbar („SG_ Name : Start|Länge@1+ (Faktor,Offset) [Min|Max] \"Einheit\" Empfänger“).");
            var coding = new CanSignal(int.Parse(match2.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(match2.Groups[4].Value, CultureInfo.InvariantCulture),
                Motorola: match2.Groups[5].Value == "0", Signed: match2.Groups[6].Value == "-", Factor: Num(match2.Groups[7].Value), Offset: Num(match2.Groups[8].Value));
            if (coding.Length is < 1 or > 64) throw new BusDatabaseException($"Signal {match2.Groups[1].Value}: Länge {coding.Length} außerhalb 1–64.");
            var signal = new BusSignal(match2.Groups[1].Value, coding) { Unit = Unescape(match2.Groups[11].Value) };
            if (match2.Groups[9].Value.Length > 0 && match2.Groups[10].Value.Length > 0)
            {
                var (lo, hi) = (Num(match2.Groups[9].Value), Num(match2.Groups[10].Value));
                if (lo != 0 || hi != 0) (signal.Minimum, signal.Maximum) = (lo, hi);
            }
            var mux = match2.Groups[2].Value;
            if (mux == "M") signal.IsMultiplexer = true;
            else if (mux.StartsWith('m')) signal.MuxValue = long.Parse(mux[1..].TrimEnd('M'), CultureInfo.InvariantCulture);
            current.Signals.Add(signal);
        }

        void ReadDbcComment(string statement)
        {
            var index3 = statement.IndexOf('"');
            if (index3 < 0) return;
            var comment = Unescape(statement[(index3 + 1)..statement.LastIndexOf('"')]);
            var head = statement[3..index3].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (head.Length >= 2 && head[0] == "BO_" && byId.TryGetValue(uint.Parse(head[1], CultureInfo.InvariantCulture), out var bo)) bo.Comment = comment;
            else if (head.Length >= 3 && head[0] == "SG_" && byId.TryGetValue(uint.Parse(head[1], CultureInfo.InvariantCulture), out var so) && so.Signal(head[2]) is { } sg) sg.Comment = comment;
        }

        void ReadDbcValues(string statement)
        {
            var head = statement[4..].Split([' ', '\t'], 3, StringSplitOptions.RemoveEmptyEntries);
            if (head.Length < 3 || !uint.TryParse(head[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw)) return; // VAL_ zu Umgebungsvariablen
            if (!byId.TryGetValue(raw, out var vo) || vo.Signal(head[1]) is not { } vs) return;
            foreach (Match match3 in Quoted.Matches(head[2])) vs.Values[long.Parse(match3.Groups[1].Value, CultureInfo.InvariantCulture)] = Unescape(match3.Groups[2].Value);
        }

        void ReadDbcFloatType(string statement)
        {
            var parts = statement[12..].Replace(":", " ").Replace(";", " ").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3) floatTypes.Add((uint.Parse(parts[0], CultureInfo.InvariantCulture), parts[1], int.Parse(parts[2], CultureInfo.InvariantCulture)));
        }

        void ReadDbcAttribute(string statement)
        {
            var q1 = statement.IndexOf('"');
            var q2 = statement.IndexOf('"', q1 + 1);
            if (q1 < 0 || q2 < 0) return;
            var attribute = statement[(q1 + 1)..q2];
            var rest = statement[(q2 + 1)..].Trim().TrimEnd(';').Trim();
            var parts = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            string Value(int index) => string.Join(' ', parts.Skip(index)).Trim('"');
            switch (attribute)
            {
                case "ProtocolType" when parts.Length >= 1 && !parts[0].EndsWith('_'):
                    db.Protocol = Value(0);
                    break;
                case "Baudrate" or "BusSpeed" when parts.Length >= 1 && !parts[0].EndsWith('_'):
                    if (double.TryParse(Value(0), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)) db.Bitrate = (int)(rate < 10_000 ? rate * 1000 : rate);
                    break;
                default:
                    ReadSignalAndFrameAttribute(attribute, parts);
                    break;
            }
        }

        void ReadSignalAndFrameAttribute(string attribute, string[] parts)
        {
            switch (attribute)
            {
                case "GenMsgCycleTime" when parts.Length >= 3 && parts[0] == "BO_":
                    if (byId.TryGetValue(uint.Parse(parts[1], CultureInfo.InvariantCulture), out var cm)) cm.CycleMs = (int)Num(parts[2]);
                    break;
                case "VFrameFormat" when parts.Length >= 3 && parts[0] == "BO_":
                    frameFormats.Add((uint.Parse(parts[1], CultureInfo.InvariantCulture), (int)Num(parts[2])));
                    break;
                case "GenSigStartValue" when parts.Length >= 4 && parts[0] == "SG_":
                    startValues.Add((uint.Parse(parts[1], CultureInfo.InvariantCulture), parts[2], Num(parts[3])));
                    break;
            }
        }

        void ReadDbcBitrate(string statement)
        {
            var text2 = statement[3..].Trim().TrimStart(':').Trim();
            if (text2.Length > 0 && double.TryParse(text2.Split([':', ' '], 2)[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var bs) && bs > 0)
                db.Bitrate = (int)(bs < 10_000 ? bs * 1000 : bs);
        }

        void ApplyFloatTypes()
        {
            foreach (var (raw, name, type) in floatTypes)
            {
                if (!byId.TryGetValue(raw, out var busMessage) || busMessage.Signal(name) is not { } busSignal || type is not (1 or 2)) continue;
                if (busSignal.Coding.Length != (type == 1 ? 32 : 64))
                {
                    db.Warnings.Add($"{busMessage.Name}.{busSignal.Name}: Gleitkommatyp passt nicht zur Länge {busSignal.Coding.Length}.");
                    continue;
                }
                busSignal.Coding = busSignal.Coding with { IsFloat = true, Signed = false };
            }
        }

        void FinishDbc()
        {
            ApplyFloatTypes();
            foreach (var (raw, name, value) in startValues)
                if (byId.TryGetValue(raw, out var busMessage2) && busMessage2.Signal(name) is { } busSignal2 && !busSignal2.Coding.IsFloat)
                    busSignal2.InitialRaw = unchecked((ulong)(long)value);
            foreach (var (raw, format) in frameFormats)
                if (byId.TryGetValue(raw, out var busMessage3)) busMessage3.Fd |= format is 14 or 15;

            foreach (var busMessage4 in db.Messages)
                foreach (var busSignal3 in busMessage4.Signals)
                {
                    var last = busSignal3.Coding.Motorola ? MotorolaLastByte(busSignal3.Coding) : (busSignal3.Coding.StartBit + busSignal3.Coding.Length - 1) / 8;
                    if (last >= busMessage4.Length) db.Warnings.Add($"{busMessage4.Name}.{busSignal3.Name}: reicht über die {busMessage4.Length} Bytes der Botschaft hinaus.");
                }
        }

        foreach (var (line, statement) in DbcStatements(text))
        {
            try
            {
                if (statement.StartsWith("VERSION", StringComparison.Ordinal))
                {
                    ReadDbcVersion(statement);
                }
                else if (statement.StartsWith("BU_", StringComparison.Ordinal))
                {
                    ReadDbcNodes(statement);
                }
                else if (statement.StartsWith("BO_ ", StringComparison.Ordinal))
                {
                    ReadDbcMessage(statement);
                }
                else if (statement.StartsWith("SG_ ", StringComparison.Ordinal))
                {
                    ReadDbcSignal(statement);
                }
                else if (statement.StartsWith("CM_ ", StringComparison.Ordinal))
                {
                    ReadDbcComment(statement);
                }
                else if (statement.StartsWith("VAL_ ", StringComparison.Ordinal))
                {
                    ReadDbcValues(statement);
                }
                else if (statement.StartsWith("SIG_VALTYPE_ ", StringComparison.Ordinal))
                {
                    ReadDbcFloatType(statement);
                }
                else if (statement.StartsWith("BA_ ", StringComparison.Ordinal))
                {
                    ReadDbcAttribute(statement);
                }
                else if (statement.StartsWith("BS_", StringComparison.Ordinal))
                {
                    ReadDbcBitrate(statement);
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new BusDatabaseException($"DBC Zeile {line}: {ex.Message}");
            }
        }

        FinishDbc();
        if (db.Messages.Count == 0) throw new BusDatabaseException("Keine Botschaften (BO_) gefunden – ist das eine DBC-Datei?");
        return db;
    }

    private static int MotorolaLastByte(CanSignal canSignal)
    {
        var bit = canSignal.StartBit;
        for (var index = 1; index < canSignal.Length; index++) bit = bit % 8 == 0 ? bit + 15 : bit - 1;
        return bit / 8;
    }

    // ================================================================== LDF

    /// <summary>Element der LDF-Struktur: Kopf (Token bis „;“ bzw. „{“) und optionaler Block.</summary>
    private sealed record LdfItem(List<string> Head, List<LdfItem>? Body);

    private static List<string> LdfTokens(string text)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character)) { index++; continue; }
            if (SkipLdfComment(text, ref index)) continue;
            if (character == '"')
            {
                var end = text.IndexOf('"', index + 1);
                if (end < 0) throw new BusDatabaseException("LDF: Anführungszeichen nicht geschlossen.");
                tokens.Add(text[index..(end + 1)]);
                index = end + 1;
                continue;
            }
            if ("{};,:=".Contains(character)) { tokens.Add(character.ToString()); index++; continue; }
            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && !"{};,:=\"".Contains(text[index])) index++;
            tokens.Add(text[start..index]);
        }
        return tokens;
    }

    private static bool SkipLdfComment(string text, ref int index)
    {
        if (text[index] != '/' || index + 1 >= text.Length) return false;
        if (text[index + 1] == '/')
        {
            while (index < text.Length && text[index] != '\n') index++;
            return true;
        }
        if (text[index + 1] != '*') return false;
        var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
        index = end < 0 ? text.Length : end + 2;
        return true;
    }

    private static List<LdfItem> LdfParse(List<string> tokens, ref int pos, bool nested)
    {
        var items = new List<LdfItem>();
        var head = new List<string>();
        while (pos < tokens.Count)
        {
            var text = tokens[pos++];
            switch (text)
            {
                case ";":
                    if (head.Count > 0) items.Add(new LdfItem(head, null));
                    head = [];
                    break;
                case "{" when head.Count > 0 && head[^1] is "," or "=":
                    {
                        // Feldwert wie {0x00, 0xFF}: als ein Token übernehmen
                        head.Add(LdfArrayValue(tokens, ref pos));
                        break;
                    }
                case "{":
                    var body = LdfParse(tokens, ref pos, nested: true);
                    items.Add(new LdfItem(head, body));
                    head = [];
                    break;
                case "}":
                    if (!nested) throw new BusDatabaseException("LDF: „}“ ohne „{“.");
                    if (head.Count > 0) items.Add(new LdfItem(head, null));
                    return items;
                default:
                    head.Add(text);
                    break;
            }
        }
        if (nested) throw new BusDatabaseException("LDF: Block nicht geschlossen („}“ fehlt).");
        if (head.Count > 0) items.Add(new LdfItem(head, null));
        return items;
    }

    private static string LdfArrayValue(List<string> tokens, ref int pos)
    {
        var sb = new StringBuilder("{");
        var depth = 1;
        while (pos < tokens.Count && depth > 0)
        {
            var text = tokens[pos++];
            if (text == "{") depth++;
            else if (text == "}") depth--;
            sb.Append(depth == 0 ? "}" : text);
        }
        return sb.ToString();
    }

    private static List<string> Values(IEnumerable<string> head) => head.Where(head2 => head2 is not ("," or ":" or "=")).ToList();

    private static long LdfInt(string text)
    {
        var text2 = text.Trim();
        return text2.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(text2[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : (long)double.Parse(text2, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static BusDatabase ParseLdf(string text)
    {
        var db = new BusDatabase(BusDatabaseFormat.Ldf) { Protocol = "LIN" };
        var pos = 0;
        var items = LdfParse(LdfTokens(text), ref pos, nested: false);
        var signals = new Dictionary<string, (int Size, ulong Init, string Publisher)>(StringComparer.Ordinal);
        var encodings = new Dictionary<string, List<LdfItem>>(StringComparer.Ordinal);
        var representation = new Dictionary<string, string>(StringComparer.Ordinal);
        void ReadLdfMetadata()
        {
            foreach (var item in items)
            {
                var name = item.Head.FirstOrDefault() ?? "";
                if (item.Body is null)
                {
                    var items2 = Values(item.Head.Skip(1));
                    if (name == "LIN_protocol_version" && items2.Count > 0) db.Version = items2[0].Trim('"');
                    if (name == "LIN_speed" && items2.Count > 0)
                    {
                        var speed = double.Parse(items2[0], NumberStyles.Float, CultureInfo.InvariantCulture);
                        db.Bitrate = (int)(items2.Count > 1 && items2[1].StartsWith("kbps", StringComparison.OrdinalIgnoreCase) || speed < 1000 ? speed * 1000 : speed);
                    }
                    continue;
                }
                switch (name)
                {
                    case "Nodes":
                        foreach (var ldfItem in item.Body)
                        {
                            var items3 = Values(ldfItem.Head);
                            if (items3.Count < 2) continue;
                            if (items3[0] == "Master") { db.MasterNode = items3[1]; db.Nodes.Add(items3[1]); }
                            else if (items3[0] == "Slaves") db.Nodes.AddRange(items3.Skip(1));
                        }
                        break;
                    case "Signals":
                        foreach (var ldfItem2 in item.Body)
                        {
                            var items4 = Values(ldfItem2.Head);
                            if (items4.Count < 3) continue;
                            var init = items4[2].StartsWith('{') ? 0UL : unchecked((ulong)LdfInt(items4[2]));
                            signals[items4[0]] = ((int)LdfInt(items4[1]), init, items4.Count > 3 ? items4[3] : "");
                        }
                        break;
                    case "Signal_encoding_types":
                        foreach (var ldfItem3 in item.Body.Where(ldfItem4 => ldfItem4.Body is not null)) encodings[ldfItem3.Head[0]] = ldfItem3.Body!;
                        break;
                    case "Signal_representation":
                        foreach (var ldfItem4 in item.Body)
                        {
                            var items5 = Values(ldfItem4.Head);
                            foreach (var skip in items5.Skip(1)) representation[skip] = items5[0];
                        }
                        break;
                }
            }

        }

        void ReadLdfFrames()
        {
            var frames = items.FirstOrDefault(ldfItem5 => ldfItem5.Head.FirstOrDefault() == "Frames" && ldfItem5.Body is not null);
            if (frames is null) throw new BusDatabaseException("LDF: Abschnitt „Frames“ fehlt.");
            foreach (var ldfItem5 in frames.Body!)
            {
                var items6 = Values(ldfItem5.Head);
                if (items6.Count < 4) continue;
                var id = (uint)LdfInt(items6[1]);
                if (id > 63) throw new BusDatabaseException($"Frame {items6[0]}: ID {id} größer als 63.");
                var message = new BusMessage(id, false, items6[0], (int)LdfInt(items6[3]), items6[2]);
                foreach (var entry in ldfItem5.Body ?? [])
                {
                    var sv = Values(entry.Head);
                    if (sv.Count < 2) continue;
                    if (!signals.TryGetValue(sv[0], out var def)) { db.Warnings.Add($"Frame {message.Name}: Signal {sv[0]} nicht unter „Signals“."); continue; }
                    var signal = new BusSignal(sv[0], new CanSignal((int)LdfInt(sv[1]), def.Size)) { InitialRaw = def.Init };
                    if (representation.TryGetValue(sv[0], out var enc) && encodings.TryGetValue(enc, out var rules))
                        ApplyEncoding(signal, rules);
                    message.Signals.Add(signal);
                }
                db.Messages.Add(message);
            }

        }

        void ReadLdfFrameGroups()
        {
            // Ereignisgesteuerte Frames: ETF { Name, auflösende-ID, Frame1, Frame2, … }
            foreach (var etf in items.Where(ldfItem6 => ldfItem6.Head.FirstOrDefault() == "Event_triggered_frames" && ldfItem6.Body is not null).SelectMany(ldfItem6 => ldfItem6.Body!))
            {
                var items7 = Values(etf.Head);
                if (items7.Count < 2) continue;
                uint resolveId = 0;
                var members = items7.Skip(1).ToList();
                if (members.Count > 0 && (members[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) || members[0].All(char.IsDigit)))
                {
                    resolveId = (uint)LdfInt(members[0]);
                    members = members.Skip(1).ToList();
                }
                db.FrameGroups.Add(new BusFrameGroup(items7[0], LinFrameKind.EventTriggered, members) { Id = resolveId });
            }
            // Sporadische Frames: SF { Name: Frame1, Frame2 }
            foreach (var sf in items.Where(ldfItem6 => ldfItem6.Head.FirstOrDefault() == "Sporadic_frames" && ldfItem6.Body is not null).SelectMany(ldfItem6 => ldfItem6.Body!))
            {
                var items8 = Values(sf.Head);
                if (items8.Count < 2) continue;
                db.FrameGroups.Add(new BusFrameGroup(items8[0], LinFrameKind.Sporadic, items8.Skip(1).ToList()));
            }
            foreach (var busFrameGroup in db.FrameGroups)
                foreach (var message2 in busFrameGroup.Members.Where(member => db.Messages.All(busMessage => !string.Equals(busMessage.Name, member, StringComparison.OrdinalIgnoreCase))))
                    db.Warnings.Add($"{(busFrameGroup.Kind == LinFrameKind.EventTriggered ? "Ereignis-" : "Sporadischer ")}Frame {busFrameGroup.Name}: Einzelframe {message2} nicht definiert.");
        }

        try
        {
            ReadLdfMetadata();
            ReadLdfFrames();
            ReadLdfFrameGroups();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new BusDatabaseException(ex is BusDatabaseException ? ex.Message : "LDF: " + ex.Message);
        }
        if (db.Messages.Count == 0) throw new BusDatabaseException("LDF: keine Frames gefunden.");
        return db;
    }

    private static void ApplyEncoding(BusSignal signal, List<LdfItem> rules)
    {
        var physicalSet = false;
        foreach (var rule in rules)
        {
            var items = Values(rule.Head);
            if (items.Count == 0) continue;
            if (items[0] == "logical_value" && items.Count >= 2)
                signal.Values[LdfInt(items[1])] = items.Count >= 3 ? items[2].Trim('"') : items[1];
            else if (items[0] == "physical_value" && items.Count >= 5 && !physicalSet)
            {
                var factor = double.Parse(items[3], NumberStyles.Float, CultureInfo.InvariantCulture);
                var offset = double.Parse(items[4], NumberStyles.Float, CultureInfo.InvariantCulture);
                signal.Coding = signal.Coding with { Factor = factor, Offset = offset };
                signal.Minimum = LdfInt(items[1]) * factor + offset;
                signal.Maximum = LdfInt(items[2]) * factor + offset;
                if (items.Count >= 6) signal.Unit = items[5].Trim('"');
                physicalSet = true;
            }
        }
    }
}
