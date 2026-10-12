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
        var t = text.Trim().Trim('"');
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        foreach (var (raw, label) in Values)
            if (string.Equals(label, t, StringComparison.OrdinalIgnoreCase)) return raw * Coding.Factor + Coding.Offset;
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

    public BusSignal? Multiplexer => Signals.FirstOrDefault(s => s.IsMultiplexer);

    public BusSignal? Signal(string name)
        => Signals.FirstOrDefault(s => s.Name == name) ?? Signals.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public BusSignal RequireSignal(string name)
        => Signal(name) ?? throw new BusDatabaseException($"Signal „{name}“ fehlt in Botschaft „{Name}“ ({string.Join(", ", Signals.Select(s => s.Name))}).");

    /// <summary>Signal gilt in diesen Daten (ohne Multiplex oder mit passendem Multiplexerwert).</summary>
    public bool IsActive(BusSignal signal, ReadOnlySpan<byte> data)
        => signal.MuxValue is not { } mux || Multiplexer is not { } m || m.Coding.Raw(data) == mux;

    /// <summary>Alle gültigen Signale mit Wert und Text der Wertetabelle.</summary>
    public IEnumerable<(BusSignal Signal, double Value, string? Text)> Decode(byte[] data)
    {
        foreach (var s in Signals)
        {
            if (!IsActive(s, data)) continue;
            double v;
            string? t;
            try
            {
                v = s.Physical(data);
                t = s.Describe(data);
            }
            catch (ArgumentException)
            {
                continue; // kürzerer Rahmen als beschrieben
            }
            yield return (s, v, t);
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
        long? muxValue = mux is not null && given.TryGetValue(mux, out var mv) ? (long)Math.Round((mv - mux.Coding.Offset) / mux.Coding.Factor) : null;
        foreach (var s in given.Keys.Where(s => s.MuxValue is not null))
        {
            if (muxValue is { } m && m != s.MuxValue)
                throw new BusDatabaseException($"Signal „{s.Name}“ gilt nur bei Multiplexer {s.MuxValue}, angegeben ist {m}.");
            muxValue = s.MuxValue;
        }
        foreach (var s in Signals)
        {
            if (s.MuxValue is { } own && own != (muxValue ?? (long)(mux?.InitialRaw ?? 0))) continue;
            if (s.IsMultiplexer && muxValue is { } mval) s.Coding.WriteRaw(data, unchecked((ulong)mval));
            else s.Coding.WriteRaw(data, s.InitialRaw);
        }
        foreach (var (s, v) in given)
        {
            if (s.CheckRange(v) is { } error) throw new BusDatabaseException(error);
            s.Coding.Write(data, v);
        }
        return data;
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
        => $"{Format.ToString().ToUpperInvariant()}: {Messages.Count} Botschaft(en), {Messages.Sum(m => m.Signals.Count)} Signal(e)"
           + (Protocol is { } p ? $", {p}" : "") + (Bitrate is { } b ? $", {b / 1000.0:0.#} kbit/s" : "");

    /// <summary>Botschaft über Namen oder ID („0x123“, „291“).</summary>
    public BusMessage? Message(string nameOrId)
    {
        var t = nameOrId.Trim();
        var byName = Messages.FirstOrDefault(m => m.Name == t) ?? Messages.FirstOrDefault(m => string.Equals(m.Name, t, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;
        uint id;
        var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
            : uint.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        return ok ? Messages.FirstOrDefault(m => m.Id == id) : null;
    }

    public BusMessage RequireMessage(string nameOrId)
        => Message(nameOrId) ?? throw new BusDatabaseException($"Botschaft „{nameOrId}“ ist nicht in der Datenbank.");

    /// <summary>J1939: Botschaft zur Parametergruppe.</summary>
    public BusMessage? ByPgn(uint pgn) => Messages.FirstOrDefault(m => m.Extended && m.Pgn == pgn);

    public BusFrameGroup? FrameGroup(string name)
        => FrameGroups.FirstOrDefault(g => g.Name == name) ?? FrameGroups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Signal über „Botschaft.Signal“ oder eindeutigen Signalnamen.</summary>
    public (BusMessage Message, BusSignal Signal) FindSignal(string reference)
    {
        var dot = reference.IndexOf('.');
        if (dot > 0)
        {
            var m = RequireMessage(reference[..dot]);
            return (m, m.RequireSignal(reference[(dot + 1)..]));
        }
        var hits = Messages.SelectMany(m => m.Signals.Where(s => string.Equals(s.Name, reference, StringComparison.OrdinalIgnoreCase)).Select(s => (m, s))).ToList();
        return hits.Count switch
        {
            1 => hits[0],
            0 => throw new BusDatabaseException($"Signal „{reference}“ ist nicht in der Datenbank."),
            _ => throw new BusDatabaseException($"Signal „{reference}“ kommt in mehreren Botschaften vor ({string.Join(", ", hits.Select(h => h.m.Name))}) – „Botschaft.Signal“ angeben."),
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
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var keyword = line.Split([' ', '\t', ':'], 2)[0];
            if (!multi.Contains(keyword) || keyword == line) // einzelnes Schlüsselwort: Liste unter NS_
            {
                yield return (i + 1, line);
                continue;
            }
            var start = i;
            var sb = new StringBuilder(line);
            while (!Complete(sb.ToString()) && i + 1 < lines.Length) sb.Append('\n').Append(lines[++i]);
            yield return (start + 1, sb.ToString());
        }

        static bool Complete(string s)
        {
            var quoted = false;
            for (var k = 0; k < s.Length; k++)
            {
                if (s[k] == '\\' && quoted) { k++; continue; }
                if (s[k] == '"') quoted = !quoted;
                else if (s[k] == ';' && !quoted) return true;
            }
            return false;
        }
    }

    private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\");

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
            var q = statement.IndexOf('"');
            db.Version = q >= 0 ? statement[(q + 1)..].TrimEnd().TrimEnd('"') : null;
        }

        void ReadDbcNodes(string statement)
        {
            db.Nodes.AddRange(statement[(statement.IndexOf(':') + 1)..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
            current = null;
        }

        void ReadDbcMessage(string statement)
        {
            var m = DbcMessage.Match(statement);
            if (!m.Success) throw new BusDatabaseException("BO_ unvollständig („BO_ ID Name: Länge Sender“).");
            var raw = uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (raw == 0xC0000000) // VECTOR__INDEPENDENT_SIG_MSG: Pseudo-Botschaft
            {
                current = null;
                return;
            }
            var extended = (raw & 0x80000000) != 0;
            var length = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            current = new BusMessage(raw & 0x1FFFFFFF, extended, m.Groups[2].Value, length, m.Groups[4].Value) { Fd = length > 8 };
            db.Messages.Add(current);
            byId[raw] = current;
        }

        void ReadDbcSignal(string statement)
        {
            if (current is null) return; // Signal der Pseudo-Botschaft
            var m = DbcSignal.Match(statement);
            if (!m.Success) throw new BusDatabaseException("SG_ nicht lesbar („SG_ Name : Start|Länge@1+ (Faktor,Offset) [Min|Max] \"Einheit\" Empfänger“).");
            var coding = new CanSignal(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                Motorola: m.Groups[5].Value == "0", Signed: m.Groups[6].Value == "-", Factor: Num(m.Groups[7].Value), Offset: Num(m.Groups[8].Value));
            if (coding.Length is < 1 or > 64) throw new BusDatabaseException($"Signal {m.Groups[1].Value}: Länge {coding.Length} außerhalb 1–64.");
            var signal = new BusSignal(m.Groups[1].Value, coding) { Unit = Unescape(m.Groups[11].Value) };
            if (m.Groups[9].Value.Length > 0 && m.Groups[10].Value.Length > 0)
            {
                var (lo, hi) = (Num(m.Groups[9].Value), Num(m.Groups[10].Value));
                if (lo != 0 || hi != 0) (signal.Minimum, signal.Maximum) = (lo, hi);
            }
            var mux = m.Groups[2].Value;
            if (mux == "M") signal.IsMultiplexer = true;
            else if (mux.StartsWith('m')) signal.MuxValue = long.Parse(mux[1..].TrimEnd('M'), CultureInfo.InvariantCulture);
            current.Signals.Add(signal);
        }

        void ReadDbcComment(string statement)
        {
            var q = statement.IndexOf('"');
            if (q < 0) return;
            var comment = Unescape(statement[(q + 1)..statement.LastIndexOf('"')]);
            var head = statement[3..q].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (head.Length >= 2 && head[0] == "BO_" && byId.TryGetValue(uint.Parse(head[1], CultureInfo.InvariantCulture), out var bo)) bo.Comment = comment;
            else if (head.Length >= 3 && head[0] == "SG_" && byId.TryGetValue(uint.Parse(head[1], CultureInfo.InvariantCulture), out var so) && so.Signal(head[2]) is { } sg) sg.Comment = comment;
        }

        void ReadDbcValues(string statement)
        {
            var head = statement[4..].Split([' ', '\t'], 3, StringSplitOptions.RemoveEmptyEntries);
            if (head.Length < 3 || !uint.TryParse(head[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw)) return; // VAL_ zu Umgebungsvariablen
            if (!byId.TryGetValue(raw, out var vo) || vo.Signal(head[1]) is not { } vs) return;
            foreach (Match v in Quoted.Matches(head[2])) vs.Values[long.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture)] = Unescape(v.Groups[2].Value);
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
            var t = statement[3..].Trim().TrimStart(':').Trim();
            if (t.Length > 0 && double.TryParse(t.Split([':', ' '], 2)[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var bs) && bs > 0)
                db.Bitrate = (int)(bs < 10_000 ? bs * 1000 : bs);
        }

        void FinishDbc()
        {
            foreach (var (raw, name, type) in floatTypes)
            {
                if (!byId.TryGetValue(raw, out var m) || m.Signal(name) is not { } s || type is not (1 or 2)) continue;
                if (s.Coding.Length != (type == 1 ? 32 : 64))
                {
                    db.Warnings.Add($"{m.Name}.{s.Name}: Gleitkommatyp passt nicht zur Länge {s.Coding.Length}.");
                    continue;
                }
                s.Coding = s.Coding with { IsFloat = true, Signed = false };
            }
            foreach (var (raw, name, value) in startValues)
                if (byId.TryGetValue(raw, out var m) && m.Signal(name) is { } s && !s.Coding.IsFloat)
                    s.InitialRaw = unchecked((ulong)(long)value);
            foreach (var (raw, format) in frameFormats)
                if (byId.TryGetValue(raw, out var m)) m.Fd |= format is 14 or 15;

            foreach (var m in db.Messages)
                foreach (var s in m.Signals)
                {
                    var last = s.Coding.Motorola ? MotorolaLastByte(s.Coding) : (s.Coding.StartBit + s.Coding.Length - 1) / 8;
                    if (last >= m.Length) db.Warnings.Add($"{m.Name}.{s.Name}: reicht über die {m.Length} Bytes der Botschaft hinaus.");
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

    private static int MotorolaLastByte(CanSignal s)
    {
        var bit = s.StartBit;
        for (var i = 1; i < s.Length; i++) bit = bit % 8 == 0 ? bit + 15 : bit - 1;
        return bit / 8;
    }

    // ================================================================== LDF

    /// <summary>Element der LDF-Struktur: Kopf (Token bis „;“ bzw. „{“) und optionaler Block.</summary>
    private sealed record LdfItem(List<string> Head, List<LdfItem>? Body);

    private static List<string> LdfTokens(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }
            if (c == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0) throw new BusDatabaseException("LDF: Anführungszeichen nicht geschlossen.");
                tokens.Add(text[i..(end + 1)]);
                i = end + 1;
                continue;
            }
            if ("{};,:=".Contains(c)) { tokens.Add(c.ToString()); i++; continue; }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && !"{};,:=\"".Contains(text[i])) i++;
            tokens.Add(text[start..i]);
        }
        return tokens;
    }

    private static List<LdfItem> LdfParse(List<string> tokens, ref int pos, bool nested)
    {
        var items = new List<LdfItem>();
        var head = new List<string>();
        while (pos < tokens.Count)
        {
            var t = tokens[pos++];
            switch (t)
            {
                case ";":
                    if (head.Count > 0) items.Add(new LdfItem(head, null));
                    head = [];
                    break;
                case "{" when head.Count > 0 && head[^1] is "," or "=":
                    {
                        // Feldwert wie {0x00, 0xFF}: als ein Token übernehmen
                        var sb = new StringBuilder("{");
                        var depth = 1;
                        while (pos < tokens.Count && depth > 0)
                        {
                            var x = tokens[pos++];
                            if (x == "{") depth++;
                            else if (x == "}") depth--;
                            sb.Append(depth == 0 ? "}" : x);
                        }
                        head.Add(sb.ToString());
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
                    head.Add(t);
                    break;
            }
        }
        if (nested) throw new BusDatabaseException("LDF: Block nicht geschlossen („}“ fehlt).");
        if (head.Count > 0) items.Add(new LdfItem(head, null));
        return items;
    }

    private static List<string> Values(IEnumerable<string> head) => head.Where(t => t is not ("," or ":" or "=")).ToList();

    private static long LdfInt(string text)
    {
        var t = text.Trim();
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : (long)double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
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
                    var v = Values(item.Head.Skip(1));
                    if (name == "LIN_protocol_version" && v.Count > 0) db.Version = v[0].Trim('"');
                    if (name == "LIN_speed" && v.Count > 0)
                    {
                        var speed = double.Parse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture);
                        db.Bitrate = (int)(v.Count > 1 && v[1].StartsWith("kbps", StringComparison.OrdinalIgnoreCase) || speed < 1000 ? speed * 1000 : speed);
                    }
                    continue;
                }
                switch (name)
                {
                    case "Nodes":
                        foreach (var n in item.Body)
                        {
                            var v = Values(n.Head);
                            if (v.Count < 2) continue;
                            if (v[0] == "Master") { db.MasterNode = v[1]; db.Nodes.Add(v[1]); }
                            else if (v[0] == "Slaves") db.Nodes.AddRange(v.Skip(1));
                        }
                        break;
                    case "Signals":
                        foreach (var s in item.Body)
                        {
                            var v = Values(s.Head);
                            if (v.Count < 3) continue;
                            var init = v[2].StartsWith('{') ? 0UL : unchecked((ulong)LdfInt(v[2]));
                            signals[v[0]] = ((int)LdfInt(v[1]), init, v.Count > 3 ? v[3] : "");
                        }
                        break;
                    case "Signal_encoding_types":
                        foreach (var e in item.Body.Where(e => e.Body is not null)) encodings[e.Head[0]] = e.Body!;
                        break;
                    case "Signal_representation":
                        foreach (var r in item.Body)
                        {
                            var v = Values(r.Head);
                            foreach (var s in v.Skip(1)) representation[s] = v[0];
                        }
                        break;
                }
            }

        }

        void ReadLdfFrames()
        {
            var frames = items.FirstOrDefault(i => i.Head.FirstOrDefault() == "Frames" && i.Body is not null);
            if (frames is null) throw new BusDatabaseException("LDF: Abschnitt „Frames“ fehlt.");
            foreach (var f in frames.Body!)
            {
                var v = Values(f.Head);
                if (v.Count < 4) continue;
                var id = (uint)LdfInt(v[1]);
                if (id > 63) throw new BusDatabaseException($"Frame {v[0]}: ID {id} größer als 63.");
                var message = new BusMessage(id, false, v[0], (int)LdfInt(v[3]), v[2]);
                foreach (var entry in f.Body ?? [])
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
            foreach (var etf in items.Where(i => i.Head.FirstOrDefault() == "Event_triggered_frames" && i.Body is not null).SelectMany(i => i.Body!))
            {
                var v = Values(etf.Head);
                if (v.Count < 2) continue;
                uint resolveId = 0;
                var members = v.Skip(1).ToList();
                if (members.Count > 0 && (members[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) || members[0].All(char.IsDigit)))
                {
                    resolveId = (uint)LdfInt(members[0]);
                    members = members.Skip(1).ToList();
                }
                db.FrameGroups.Add(new BusFrameGroup(v[0], LinFrameKind.EventTriggered, members) { Id = resolveId });
            }
            // Sporadische Frames: SF { Name: Frame1, Frame2 }
            foreach (var sf in items.Where(i => i.Head.FirstOrDefault() == "Sporadic_frames" && i.Body is not null).SelectMany(i => i.Body!))
            {
                var v = Values(sf.Head);
                if (v.Count < 2) continue;
                db.FrameGroups.Add(new BusFrameGroup(v[0], LinFrameKind.Sporadic, v.Skip(1).ToList()));
            }
            foreach (var g in db.FrameGroups)
                foreach (var m in g.Members.Where(m => db.Messages.All(x => !string.Equals(x.Name, m, StringComparison.OrdinalIgnoreCase))))
                    db.Warnings.Add($"{(g.Kind == LinFrameKind.EventTriggered ? "Ereignis-" : "Sporadischer ")}Frame {g.Name}: Einzelframe {m} nicht definiert.");
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
            var v = Values(rule.Head);
            if (v.Count == 0) continue;
            if (v[0] == "logical_value" && v.Count >= 2)
                signal.Values[LdfInt(v[1])] = v.Count >= 3 ? v[2].Trim('"') : v[1];
            else if (v[0] == "physical_value" && v.Count >= 5 && !physicalSet)
            {
                var factor = double.Parse(v[3], NumberStyles.Float, CultureInfo.InvariantCulture);
                var offset = double.Parse(v[4], NumberStyles.Float, CultureInfo.InvariantCulture);
                signal.Coding = signal.Coding with { Factor = factor, Offset = offset };
                signal.Minimum = LdfInt(v[1]) * factor + offset;
                signal.Maximum = LdfInt(v[2]) * factor + offset;
                if (v.Count >= 6) signal.Unit = v[5].Trim('"');
                physicalSet = true;
            }
        }
    }
}
