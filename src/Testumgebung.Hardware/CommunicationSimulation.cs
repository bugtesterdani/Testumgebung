/**
 * @file
 * @brief Erzeugt regelbasierte Antworten simulierter Prüflinge für Byte-Ströme, Modbus und Feldbusse.
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Testumgebung.Contracts;

namespace Testumgebung.Hardware;

/// <summary>Art einer simulierten Kommunikationsschnittstelle des Prüflings.</summary>
public enum CommKind
{
    /// <summary>Byte-Strom (RS-232/RS-485, TCP, UDP): Anfrage-Muster → Antwort.</summary>
    Stream,
    /// <summary>Modbus-Registerabbild.</summary>
    Modbus,
    /// <summary>CAN-Antwortregeln, zyklische Rahmen, UDS-Steuergerät über ISO-TP.</summary>
    Can,
    /// <summary>LIN-Slave-Antworten.</summary>
    Lin,
    /// <summary>UDS-Steuergerät über DoIP (TCP).</summary>
    DoIp,
    /// <summary>OPC-UA-Server (Variablen, Methoden, Abonnements).</summary>
    OpcUa,
}

/// <summary>
/// Simulierte Prüflingskommunikation nach Regeln (Simulation als erster Entwicklungsweg, Abschnitt 7). Antworten kommen sofort
/// und deterministisch; „keine passende Regel“ = der Prüfling schweigt (Zeitüberschreitung beim Empfang).
/// </summary>
public static class CommSimulation
{
    public static object Create(CommKind kind, string rules, int seed = 1) => kind switch
    {
        CommKind.Stream => new SimulatedStreamDut(rules, seed),
        CommKind.Modbus => new SimulatedModbusDut(rules, seed),
        CommKind.Lin => new SimulatedLinDut(rules),
        CommKind.DoIp => new SimulatedDoIpDut(rules),
        CommKind.OpcUa => new SimulatedOpcUaServer(rules),
        _ => new SimulatedCanDut(rules),
    };

    /// <summary>Regeln prüfen, ohne sie auszuführen (Editor).</summary>
    public static IReadOnlyList<string> Validate(CommKind kind, string rules)
    {
        try
        {
            _ = Create(kind, rules);
            return [];
        }
        catch (FormatException ex)
        {
            return [ex.Message];
        }
        catch (ArgumentException ex)
        {
            return [ex.Message];
        }
    }

    internal static IEnumerable<(int Line, string Text)> Lines(string rules)
        => rules.Split('\n').Select((line, index) => (index + 1, line.Trim().TrimEnd('\r'))).Where(entry => entry.Item2.Length > 0 && !entry.Item2.StartsWith('#'));

    /// <summary>„230±2“ → Wert mit gleichverteiltem Rauschen; sonst fester Wert.</summary>
    internal static Func<double> Value(string text, Random random)
    {
        var parts = text.Split('±', 2, StringSplitOptions.TrimEntries);
        var value = double.Parse(parts[0], CultureInfo.InvariantCulture);
        if (parts.Length == 1) return () => value;
        var noise = double.Parse(parts[1], CultureInfo.InvariantCulture);
        return () =>
        {
            lock (random) return value + (random.NextDouble() * 2 - 1) * noise;
        };
    }
}

/// <summary>
/// Byte-Strom-Prüfling. Regeln je Zeile <c>MUSTER =&gt; ANTWORT</c>: MUSTER ist ein regulärer Ausdruck über die Anfrage
/// (ohne Zeilenende), ANTWORT ein Text mit Escapes (\r \n \xHH), Gruppen $1…$9 und Zufallswerten {zufall:220:240:1}.
/// Binär: <c>hex: 01 03 ?? ?? =&gt; hex: 01 03 02 00 64</c>. Einstellungen: <c>antwortende=\r\n</c> (Standard: wie die Anfrage).
/// </summary>
public sealed class SimulatedStreamDut : IByteStreamAdapter
{
    private sealed record Rule(Regex? Pattern, byte?[]? HexPattern, string Response, bool HexResponse);

    private readonly List<Rule> _rules = [];
    private readonly ConcurrentQueue<byte> _input = new();
    private readonly Random _random;
    private readonly byte[]? _responseEnd;

    public SimulatedStreamDut(string rules, int seed = 1)
    {
        _random = new Random(seed);
        foreach (var (line, text) in CommSimulation.Lines(rules))
        {
            if (text.StartsWith("antwortende=", StringComparison.OrdinalIgnoreCase))
            {
                _responseEnd = CommText.Parse(text["antwortende=".Length..], hex: false);
                continue;
            }
            var arrow = text.IndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0) throw new FormatException($"Zeile {line}: „MUSTER => ANTWORT“ erwartet.");
            var pattern = text[..arrow].Trim();
            var response = text[(arrow + 2)..].Trim();
            var hexResponse = response.StartsWith("hex:", StringComparison.OrdinalIgnoreCase);
            if (hexResponse) response = response[4..].Trim();
            if (pattern.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = pattern[4..].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                    .Select(byteToken => byteToken == "??" ? (byte?)null : byte.Parse(byteToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                _rules.Add(new Rule(null, bytes, response, hexResponse));
            }
            else
            {
                try
                {
                    _rules.Add(new Rule(new Regex("^(?:" + pattern + ")$", RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1)), null, response, hexResponse));
                }
                catch (ArgumentException ex)
                {
                    throw new FormatException($"Zeile {line}: Muster ungültig – {ex.Message}");
                }
            }
        }
    }

    /// <summary>Empfangene Anfragen (Tests, Anzeige).</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public Task SendAsync(string channel, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var bytes = data.ToArray();
        Requests.Enqueue(CommText.Trace(bytes));
        var (body, end) = SplitLineEnd(bytes);
        var text = CommText.Raw(body);
        foreach (var rule in _rules)
        {
            var response = MatchResponse(rule, text, bytes);
            if (response is null) continue;
            var payload = rule.HexResponse ? CommText.ParseHex(response) : CommText.Parse(response, hex: false);
            foreach (var payload2 in payload) _input.Enqueue(payload2);
            if (!rule.HexResponse)
                foreach (var end2 in _responseEnd ?? end) _input.Enqueue(end2);
            break;
        }
        return Task.CompletedTask;
    }

    private string? MatchResponse(Rule rule, string text, byte[] bytes)
    {
        string? response = null;
        if (rule.Pattern is { } regex && regex.Match(text) is { Success: true } match)
            response = Substitute(rule.Response, match);
        else if (rule.HexPattern is { } hp && hp.Length == bytes.Length && hp.Select((dataByte, index) => dataByte is null || dataByte == bytes[index]).All(flag => flag))
            response = rule.Response;
        return response;
    }

    private static (byte[] Body, byte[] End) SplitLineEnd(byte[] bytes)
    {
        var length = bytes.Length;
        if (length >= 2 && bytes[length - 2] == '\r' && bytes[length - 1] == '\n') return (bytes[..^2], [13, 10]);
        if (length >= 1 && bytes[length - 1] is (byte)'\n' or (byte)'\r') return (bytes[..^1], [bytes[length - 1]]);
        return (bytes, []);
    }

    private string Substitute(string template, Match match2)
    {
        var text = Regex.Replace(template, @"\$(\d)", match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) < match2.Groups.Count ? match2.Groups[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)].Value : "");
        return Regex.Replace(text, @"\{zufall:([-\d.]+):([-\d.]+)(?::(\d+))?\}", match =>
        {
            var min = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var max = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var dec = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            double value;
            lock (_random) value = min + _random.NextDouble() * (max - min);
            return Math.Round(value, dec).ToString("F" + dec, CultureInfo.InvariantCulture);
        });
    }

    public Task<byte[]> ReceiveAsync(string channel, ReceiveRule rule, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>();
        while (_input.TryPeek(out _))
        {
            if (rule.Length is { } len && buffer.Count >= len) break;
            _input.TryDequeue(out var dataByte);
            buffer.Add(dataByte);
            if (rule.Terminator is { Length: > 0 } bytes && EndsWith(buffer, bytes))
                return Task.FromResult(buffer.Take(buffer.Count - bytes.Length).ToArray());
        }
        if (rule.Length is { } length && buffer.Count >= length) return Task.FromResult(buffer.Take(length).ToArray());
        if (rule.Terminator is null && rule.Length is null && buffer.Count > 0) return Task.FromResult(buffer.ToArray());
        // Unvollständig: Rest zurücklegen ist bei einem echten Gerät nicht möglich – verworfen wie nach einem Zeitlimit.
        throw new CommunicationTimeoutException($"Keine vollständige Antwort innerhalb von {rule.Timeout.TotalMilliseconds:0} ms (Simulation: keine passende Regel).", buffer.ToArray());
    }

    private static bool EndsWith(List<byte> buffer, byte[] bytes)
    {
        if (buffer.Count < bytes.Length) return false;
        for (var index = 0; index < bytes.Length; index++)
            if (buffer[buffer.Count - bytes.Length + index] != bytes[index]) return false;
        return true;
    }

    public void DiscardInput(string channel) => _input.Clear();
}

/// <summary>
/// Modbus-Prüfling als Registerabbild. Zeilen <c>HR100=1234</c>, <c>HR200:float32=230.5</c>, <c>IR10:float32=230±2</c>,
/// <c>IR0:int16=-5</c>, <c>C3=1</c>, <c>DI1=0</c>, <c>HR300:text=ABC123</c>, Wortfolge vertauscht mit <c>wortfolge=niedrig</c>.
/// Nicht genannte Adressen antworten mit Ausnahme 02 (unzulässige Datenadresse), außer <c>alle=0</c>.
/// </summary>
public sealed class SimulatedModbusDut : IModbusAdapter
{
    private readonly ConcurrentDictionary<(ModbusArea, int), Func<ushort>> _map = new();
    private readonly bool _allZero;

    public SimulatedModbusDut(string rules, int seed = 1)
    {
        var random = new Random(seed);
        var lowFirst = false;
        foreach (var (line, text) in CommSimulation.Lines(rules))
        {
            var eq = text.IndexOf('=');
            if (eq < 0) throw new FormatException($"Zeile {line}: „ADRESSE[:TYP]=WERT“ erwartet.");
            var left = text[..eq].Trim();
            var right = text[(eq + 1)..].Trim();
            if (left.Equals("wortfolge", StringComparison.OrdinalIgnoreCase))
            {
                lowFirst = right.StartsWith("niedrig", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (left.Equals("alle", StringComparison.OrdinalIgnoreCase))
            {
                _allZero = true;
                continue;
            }
            try
            {
                AddRegisters(left, right, random, lowFirst);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new FormatException($"Zeile {line}: {ex.Message}");
            }
        }
    }

    private void AddRegisters(string left, string right, Random random, bool lowFirst)
    {
        var (area, address, type) = ParseRegisterPoint(left);
        if (type == ModbusDataType.Text) { AddTextRegisters(area, address, right, type); return; }
        var value = type == ModbusDataType.Bool && !right.Contains('±')
            ? (right is "1" or "true" or "wahr" ? () => 1.0 : () => 0.0)
            : CommSimulation.Value(right, random);
        var count = ModbusCodec.Registers(type);
        for (var index2 = 0; index2 < count; index2++)
        {
            var index = index2;
            var type2 = type;
            _map[(area, address + index2)] = () => ModbusCodec.Encode(type2 is ModbusDataType.Float32 or ModbusDataType.Float64 or ModbusDataType.Bool ? value() : Math.Round(value()), type2, lowFirst)[index];
        }
    }

    private static (ModbusArea Area, int Address, ModbusDataType Type) ParseRegisterPoint(string left)
    {
        var colon = left.IndexOf(':');
        var (area, address) = ModbusCodec.ParseAddress(colon < 0 ? left : left[..colon]);
        var type = colon < 0 ? (area is ModbusArea.Coil or ModbusArea.DiscreteInput ? ModbusDataType.Bool : ModbusDataType.UInt16) : ModbusCodec.ParseType(left[(colon + 1)..]);
        return (area, address, type);
    }

    private void AddTextRegisters(ModbusArea area, int address, string right, ModbusDataType type)
    {
        var regs = ModbusCodec.Encode(right, type);
        for (var index = 0; index < regs.Length; index++)
        {
            var registerValue = regs[index];
            _map[(area, address + index)] = () => registerValue;
        }
    }

    /// <summary>Geschriebene Werte (Tests): (Bereich, Adresse) → Rohwert.</summary>
    public ConcurrentQueue<string> Writes { get; } = new();

    private ushort Get(ModbusArea area, int address, byte function)
        => _map.TryGetValue((area, address), out var callback) ? callback() : _allZero ? (ushort)0 : throw new ModbusException(function, 2);

    public Task<ushort[]> ReadRegistersAsync(string channel, ModbusArea area, int address, int count, CancellationToken cancellationToken)
    {
        if (area is not (ModbusArea.HoldingRegister or ModbusArea.InputRegister)) throw new ArgumentException("Register lesen: HR oder IR.");
        var function = area == ModbusArea.HoldingRegister ? (byte)3 : (byte)4;
        return Task.FromResult(Enumerable.Range(address, count).Select(address2 => Get(area, address2, function)).ToArray());
    }

    public Task<bool[]> ReadBitsAsync(string channel, ModbusArea area, int address, int count, CancellationToken cancellationToken)
    {
        if (area is not (ModbusArea.Coil or ModbusArea.DiscreteInput)) throw new ArgumentException("Bits lesen: C oder DI.");
        var function = area == ModbusArea.Coil ? (byte)1 : (byte)2;
        return Task.FromResult(Enumerable.Range(address, count).Select(address2 => Get(area, address2, function) != 0).ToArray());
    }

    public Task WriteRegistersAsync(string channel, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
    {
        for (var index = 0; index < values.Count; index++)
        {
            var number = values[index];
            if (!_allZero && !_map.ContainsKey((ModbusArea.HoldingRegister, address + index))) throw new ModbusException(values.Count == 1 ? (byte)6 : (byte)16, 2);
            _map[(ModbusArea.HoldingRegister, address + index)] = () => number;
            Writes.Enqueue($"HR{address + index}={number}");
        }
        return Task.CompletedTask;
    }

    public Task WriteBitsAsync(string channel, int address, IReadOnlyList<bool> values, CancellationToken cancellationToken)
    {
        for (var index = 0; index < values.Count; index++)
        {
            var number = values[index] ? (ushort)1 : (ushort)0;
            if (!_allZero && !_map.ContainsKey((ModbusArea.Coil, address + index))) throw new ModbusException(values.Count == 1 ? (byte)5 : (byte)15, 2);
            _map[(ModbusArea.Coil, address + index)] = () => number;
            Writes.Enqueue($"C{address + index}={number}");
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// CAN-Prüfling. Regeln je Zeile:
/// <list type="bullet">
/// <item><c>ID DATEN =&gt; ID DATEN</c> (Anfrage mit ** als Platzhalter → Antwort mit $n = n-tes Anfragebyte); mehrere
/// Antwortrahmen mit „;“ getrennt (z. B. J1939-Transportprotokoll).</item>
/// <item><c>zyklisch ID DATEN</c>: Rahmen, der immer anliegt.</item>
/// <item><c>uds 0x7E0 0x7E8 [fd] [tx=0x10 rx=0xF1]</c>: ISO-TP-Adressen (optional erweiterte Adressierung) für folgende <c>uds: ANFRAGE =&gt; ANTWORT[; ANTWORT]</c>-Regeln
/// (Bytes in Hex, "Text" als ASCII, $n, ** in der Anfrage). Segmentierung und Flusssteuerung wie ein Steuergerät.</item>
/// </list>
/// IDs in Hex; mehr als 3 Stellen oder Endung x = 29 Bit; „fd“ nach der ID = CAN FD.
/// </summary>
public sealed class SimulatedCanDut : ICanAdapter
{
    private sealed record Frame(uint Id, bool Extended, bool Fd, string[] Bytes);

    private sealed record Rule(CanFrame Request, bool[] Wildcards, IReadOnlyList<Frame> Responses);

    internal sealed record UdsRule(byte?[] Request, IReadOnlyList<string[]> Responses);

    private sealed class UdsEndpoint(uint request, uint response, bool extended, bool fd)
    {
        public uint RequestId { get; } = request;
        public uint ResponseId { get; } = response;
        public bool Extended { get; } = extended;
        public bool Fd { get; } = fd;
        public List<UdsRule> Rules { get; } = [];
        /// <summary>Sicherheitszugang je Stufe: Seed und erwarteter Schlüssel.</summary>
        public Dictionary<byte, (byte[] Seed, byte[] Key)> Security { get; } = [];
        public List<byte>? Incoming { get; set; }
        public int IncomingLength { get; set; }
        public Queue<byte[]> PendingConsecutive { get; } = new();

        /// <summary>Erweiterte Adressierung: erstes Byte der Anfragen bzw. der Antworten.</summary>
        public byte? InAddress { get; set; }
        public byte? OutAddress { get; set; }
        public int Size => (Fd ? 64 : 8) - (OutAddress is null ? 0 : 1);
    }

    private readonly List<Rule> _rules = [];
    private readonly List<UdsEndpoint> _uds = [];
    private readonly List<CanFrame> _cyclic = [];
    private readonly ConcurrentQueue<CanFrame> _input = new();
    private readonly object _gate = new();
    private int _cyclicTurn = -1;

    public SimulatedCanDut(string rules)
    {
        UdsEndpoint? endpoint = null;
        foreach (var (line, text) in CommSimulation.Lines(rules))
        {
            try
            {
                if (text.StartsWith("zyklisch", StringComparison.OrdinalIgnoreCase))
                {
                    var frame = ParseFrame(text["zyklisch".Length..]);
                    _cyclic.Add(new CanFrame(frame.Id, frame.Bytes.Select(Hex).ToArray(), frame.Extended, frame.Fd));
                    continue;
                }
                if (text.StartsWith("uds:", StringComparison.OrdinalIgnoreCase))
                {
                    if (endpoint is null)
                    {
                        endpoint = new UdsEndpoint(0x7E0, 0x7E8, false, false);
                        _uds.Add(endpoint);
                    }
                    endpoint.Rules.Add(ParseUds(text[4..]));
                    continue;
                }
                if (text.StartsWith("sicherheit ", StringComparison.OrdinalIgnoreCase))
                {
                    if (endpoint is null)
                    {
                        endpoint = new UdsEndpoint(0x7E0, 0x7E8, false, false);
                        _uds.Add(endpoint);
                    }
                    ParseSecurity(endpoint, text["sicherheit ".Length..]);
                    continue;
                }
                if (text.StartsWith("uds ", StringComparison.OrdinalIgnoreCase))
                {
                    endpoint = AddUdsEndpoint(text);
                    continue;
                }
                var split = text.IndexOf("=>", StringComparison.Ordinal);
                if (split < 0) throw new FormatException("„ID DATEN => ID DATEN“, „zyklisch ID DATEN“ oder „uds: …“ erwartet.");
                var req = ParseFrame(text[..split]);
                var answers = text[(split + 2)..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(ParseFrame).ToList();
                if (answers.Count == 0) throw new FormatException("Antwortrahmen fehlt.");
                foreach (var frame2 in answers) Check(frame2.Bytes, req.Bytes.Length);
                var requestFrame = new CanFrame(req.Id, req.Bytes.Select(byteToken => byteToken == "**" ? (byte)0 : Hex(byteToken)).ToArray(), req.Extended, req.Fd);
                _rules.Add(new Rule(requestFrame, req.Bytes.Select(byteToken => byteToken == "**").ToArray(), answers));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new FormatException($"Zeile {line}: {ex.Message}");
            }
        }
    }

    /// <summary>„ANFRAGE => ANTWORT[; ANTWORT]“ (nach „uds:“).</summary>
    internal static UdsRule ParseUds(string body)
    {
        var arrow = body.IndexOf("=>", StringComparison.Ordinal);
        if (arrow < 0) throw new FormatException("„uds: ANFRAGE => ANTWORT“ erwartet.");
        var request = Tokens(body[..arrow]).Select(token => token == "**" ? (byte?)null : Hex(token)).ToArray();
        if (request.Length == 0) throw new FormatException("UDS-Anfrage leer.");
        var responses = body[(arrow + 2)..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => Tokens(entry).ToArray()).ToList();
        foreach (var parts in responses) Check(parts, request.Length);
        return new UdsRule(request, responses);
    }

    /// <summary>„sicherheit 01 seed=11 22 33 44 xor=5A“ (oder add=, invert, addrotxor=AA BB).</summary>
    private UdsEndpoint AddUdsEndpoint(string text)
    {
        var parts = text[4..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new FormatException("„uds ANFRAGE-ID ANTWORT-ID [fd]“ erwartet, z. B. uds 0x7E0 0x7E8.");
        var (rq, ext1) = Id(parts[0]);
        var (rs, ext2) = Id(parts[1]);
        var endpoint = new UdsEndpoint(rq, rs, ext1 || ext2, parts.Skip(2).Any(skip => skip.Equals("fd", StringComparison.OrdinalIgnoreCase)));
        foreach (var option in parts.Skip(2))
        {
            if (option.StartsWith("tx=", StringComparison.OrdinalIgnoreCase)) endpoint.InAddress = Hex(option[3..]);
            else if (option.StartsWith("rx=", StringComparison.OrdinalIgnoreCase)) endpoint.OutAddress = Hex(option[3..]);
        }
        _uds.Add(endpoint);
        return endpoint;
    }

    private static void ParseSecurity(UdsEndpoint udsEndpoint, string text)
    {
        var tokens = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw new FormatException("„sicherheit STUFE seed=… <algorithmus>“ erwartet.");
        var level = Hex(tokens[0]);
        var seed = new List<byte>();
        var param = new List<byte>();
        Testumgebung.Contracts.SecurityKey.Method method = Testumgebung.Contracts.SecurityKey.Method.Xor;
        List<byte>? current = null;
        foreach (var skip in tokens.Skip(1))
        {
            var eq = skip.IndexOf('=');
            var kw = (eq >= 0 ? skip[..eq] : skip).ToLowerInvariant();
            switch (kw)
            {
                case "seed": current = seed; break;
                case "xor": method = Testumgebung.Contracts.SecurityKey.Method.Xor; current = param; break;
                case "add": method = Testumgebung.Contracts.SecurityKey.Method.Add; current = param; break;
                case "addrotxor": method = Testumgebung.Contracts.SecurityKey.Method.AddRotateXor; current = param; break;
                case "invert": method = Testumgebung.Contracts.SecurityKey.Method.Invert; current = null; break;
                default: (current ?? throw new FormatException($"„{skip}“ ohne seed=/xor=/…")).Add(Hex(skip)); continue;
            }
            if (eq >= 0 && eq + 1 < skip.Length) current!.Add(Hex(skip[(eq + 1)..]));
        }
        if (seed.Count == 0) throw new FormatException("Sicherheitszugang: seed= fehlt.");
        udsEndpoint.Security[level] = (seed.ToArray(), Testumgebung.Contracts.SecurityKey.Compute(method, seed.ToArray(), param.ToArray()));
    }

    /// <summary>Antworten der ersten passenden Regel (leer = Schweigen).</summary>
    internal static IReadOnlyList<byte[]> AnswerUds(IEnumerable<UdsRule> rules, byte[] request)
    {
        var rule = rules.FirstOrDefault(udsRule => udsRule.Request.Length == request.Length && udsRule.Request.Select((dataByte, index) => dataByte is null || dataByte == request[index]).All(flag => flag));
        return rule is null ? [] : rule.Responses.Select(parts => Build(parts, request)).ToList();
    }

    internal static byte Hex(string b) => byte.Parse(b.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? b[2..] : b, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    internal static void Check(string[] bytes, int requestLength)
    {
        foreach (var byteToken in bytes)
        {
            if (byteToken.StartsWith('$'))
            {
                if (!int.TryParse(byteToken[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 1 || count > requestLength)
                    throw new FormatException($"„{byteToken}“: Anfrage hat nur {requestLength} Bytes.");
            }
            else _ = Hex(byteToken);
        }
    }

    /// <summary>Bytes in Hex, "Text" als ASCII-Bytes.</summary>
    internal static IEnumerable<string> Tokens(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]) || text[index] == ',') { index++; continue; }
            if (text[index] == '"')
            {
                var end = text.IndexOf('"', index + 1);
                if (end < 0) throw new FormatException("Anführungszeichen nicht geschlossen.");
                foreach (var dataByte in Encoding.Latin1.GetBytes(text[(index + 1)..end])) yield return dataByte.ToString("X2", CultureInfo.InvariantCulture);
                index = end + 1;
                continue;
            }
            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != ',') index++;
            yield return text[start..index];
        }
    }

    internal static (uint Id, bool Extended) Id(string text)
    {
        var idText = text.Trim();
        var ext = idText.EndsWith('x') || idText.EndsWith('X');
        if (ext) idText = idText[..^1];
        if (idText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) idText = idText[2..];
        ext |= idText.Length > 3;
        return (uint.Parse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture), ext);
    }

    private static Frame ParseFrame(string text)
    {
        var parts = Tokens(text).ToList();
        if (parts.Count == 0) throw new FormatException("CAN-ID fehlt.");
        var (id, ext) = Id(parts[0]);
        parts.RemoveAt(0);
        var fd = parts.Count > 0 && parts[0].Equals("fd", StringComparison.OrdinalIgnoreCase);
        if (fd) parts.RemoveAt(0);
        return new Frame(id, ext, fd, parts.ToArray());
    }

    internal static byte[] Build(string[] tokens, byte[] request)
        => tokens.Select(token => token.StartsWith('$') ? request[int.Parse(token[1..], CultureInfo.InvariantCulture) - 1] : Hex(token)).ToArray();

    public ConcurrentQueue<CanFrame> Sent { get; } = new();

    public Task SendAsync(string channel, CanFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Validate() is { } error) throw new CommunicationException(error);
        Sent.Enqueue(frame);
        lock (_gate)
        {
            if (_uds.FirstOrDefault(udsEndpoint => udsEndpoint.RequestId == frame.Id && udsEndpoint.Extended == frame.Extended) is { } endpoint)
            {
                HandleIsoTp(endpoint, frame);
                return Task.CompletedTask;
            }
        }
        foreach (var rule in _rules)
        {
            if (rule.Request.Id != frame.Id || rule.Request.Extended != frame.Extended || rule.Request.Data.Length != frame.Data.Length) continue;
            if (!rule.Request.Data.Select((dataByte, index) => rule.Wildcards[index] || dataByte == frame.Data[index]).All(flag => flag)) continue;
            foreach (var frame2 in rule.Responses) _input.Enqueue(new CanFrame(frame2.Id, Build(frame2.Bytes, frame.Data), frame2.Extended, frame2.Fd));
            break;
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ ISO-TP/UDS-Steuergerät

    private void Reply(UdsEndpoint udsEndpoint, List<byte> data)
    {
        if (udsEndpoint.OutAddress is { } address) data.Insert(0, address);
        var target = udsEndpoint.Fd ? CanFrame.FdLengths.First(fdLength => fdLength >= Math.Max(8, data.Count)) : 8;
        while (data.Count < target) data.Add(0xAA);
        _input.Enqueue(new CanFrame(udsEndpoint.ResponseId, [.. data], udsEndpoint.Extended, udsEndpoint.Fd, udsEndpoint.Fd));
    }

    private void HandleIsoTp(UdsEndpoint udsEndpoint, CanFrame frame)
    {
        var data = frame.Data;
        if (udsEndpoint.InAddress is { } address)
        {
            if (data.Length == 0 || data[0] != address) return; // anderes Steuergerät
            data = data[1..];
        }
        if (data.Length == 0) return;
        switch (data[0] >> 4)
        {
            case 0:
                ProcessSingleFrame(udsEndpoint, data);
                break;
            case 1:
                BeginIncomingMessage(udsEndpoint, data);
                break;
            case 2 when udsEndpoint.Incoming is not null:
                ContinueIncomingMessage(udsEndpoint, data);
                break;
            case 3 when (data[0] & 0x0F) == 0:
                while (udsEndpoint.PendingConsecutive.TryDequeue(out var cf)) Reply(udsEndpoint, [.. cf]);
                break;
        }
    }

    private void ProcessSingleFrame(UdsEndpoint endpoint, byte[] data)
    {
        var length = data[0] & 0x0F;
        var start = 1;
        if (length == 0 && data.Length > 8)
        {
            length = data[1];
            start = 2;
        }
        if (length > 0 && start + length <= data.Length)
            Process(endpoint, data.AsSpan(start, length).ToArray());
    }

    private void BeginIncomingMessage(UdsEndpoint endpoint, byte[] data)
    {
        endpoint.IncomingLength = (data[0] & 0x0F) << 8 | data[1];
        endpoint.Incoming = [.. data.Skip(2).Take(endpoint.IncomingLength)];
        Reply(endpoint, [0x30, 0x00, 0x00]);
    }

    private void ContinueIncomingMessage(UdsEndpoint endpoint, byte[] data)
    {
        var incoming = endpoint.Incoming!;
        incoming.AddRange(data.Skip(1).Take(endpoint.IncomingLength - incoming.Count));
        if (incoming.Count < endpoint.IncomingLength) return;

        var request = incoming.ToArray();
        endpoint.Incoming = null;
        Process(endpoint, request);
    }

    private void Process(UdsEndpoint udsEndpoint, byte[] request)
    {
        if (request.Length >= 2 && request[0] == 0x27 && udsEndpoint.Security.Count > 0)
        {
            if (SecurityResponse(udsEndpoint, request) is { } sec) SendPayload(udsEndpoint, sec);
            return;
        }
        foreach (var payload in AnswerUds(udsEndpoint.Rules, request)) SendPayload(udsEndpoint, payload); // keine Regel = Schweigen
    }

    private void SendPayload(UdsEndpoint udsEndpoint, byte[] payload)
    {
        var size = udsEndpoint.Size;
        var classic = udsEndpoint.Fd ? size - 57 : size - 1;
        var single = udsEndpoint.Fd ? size - 2 : classic;
        if (payload.Length <= classic) Reply(udsEndpoint, [(byte)payload.Length, .. payload]);
        else if (payload.Length <= single) Reply(udsEndpoint, [0x00, (byte)payload.Length, .. payload]);
        else
        {
            Reply(udsEndpoint, [(byte)(0x10 | payload.Length >> 8), (byte)payload.Length, .. payload.Take(size - 2)]);
            var offset = size - 2;
            var sequence = 1;
            while (offset < payload.Length)
            {
                udsEndpoint.PendingConsecutive.Enqueue([(byte)(0x20 | sequence++ & 0x0F), .. payload.Skip(offset).Take(size - 1)]);
                offset += size - 1;
            }
        }
    }

    private static byte[]? SecurityResponse(UdsEndpoint udsEndpoint, byte[] request)
    {
        var sub = request[1];
        var level = (byte)(sub % 2 == 1 ? sub : sub - 1);
        if (!udsEndpoint.Security.TryGetValue(level, out var sk)) return [0x7F, 0x27, 0x12]; // subFunctionNotSupported
        if (sub % 2 == 1) return [0x67, sub, .. sk.Seed];                          // Seed liefern
        var key = request[2..];
        return key.SequenceEqual(sk.Key) ? [0x67, sub] : [0x7F, 0x27, 0x35];       // invalidKey
    }

    // ------------------------------------------------------------------ Empfang

    public Task<CanFrame> ReceiveAsync(string channel, CanFilter filter, TimeSpan timeout, CancellationToken cancellationToken)
        => ReceiveAnyAsync(channel, [filter], timeout, cancellationToken);

    public Task<CanFrame> ReceiveAnyAsync(string channel, IReadOnlyList<CanFilter> filters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Gepufferte Antworten in Reihenfolge; nicht passende werden übersprungen (wie ein Empfangsfilter).
        while (_input.TryDequeue(out var canFrame))
            if (filters.Any(canFilter => canFilter.Matches(canFrame))) return Task.FromResult(canFrame);
        // Zyklische Rahmen: mehrere passende (z. B. Multiplex-Seiten) abwechselnd
        var cyclic = _cyclic.Where(canFrame2 => filters.Any(canFilter => canFilter.Matches(canFrame2))).ToList();
        if (cyclic.Count > 0) return Task.FromResult(cyclic[(int)((uint)Interlocked.Increment(ref _cyclicTurn) % (uint)cyclic.Count)]);
        throw new CommunicationTimeoutException($"Kein CAN-Rahmen mit ID 0x{filters[0].Id:X} innerhalb von {timeout.TotalMilliseconds:0} ms (Simulation).", []);
    }

    public void DiscardInput(string channel) => _input.Clear();
}

/// <summary>
/// LIN-Slaves. Regeln je Zeile: <c>ID =&gt; DATEN</c> (Antwort auf den Header), <c>ID =&gt; @ID2</c> (Antwort = zuletzt vom Master
/// auf ID2 gesendete Daten, z. B. Schreiben und Zurücklesen). IDs 0x00–0x3F, Daten in Hex; ohne Regel antwortet kein Slave.
/// </summary>
public sealed class SimulatedLinDut : ILinAdapter
{
    private readonly Dictionary<byte, byte[]> _responses = [];
    private readonly Dictionary<byte, byte> _echo = [];
    private readonly ConcurrentDictionary<byte, byte[]> _published = new();

    public SimulatedLinDut(string rules)
    {
        foreach (var (line, text) in CommSimulation.Lines(rules))
        {
            try
            {
                var arrow = text.IndexOf("=>", StringComparison.Ordinal);
                if (arrow < 0) throw new FormatException("„ID => DATEN“ oder „ID => @ID“ erwartet, z. B. 0x21 => 01 02 03 04.");
                var id = ParseId(text[..arrow]);
                var response = text[(arrow + 2)..].Trim();
                if (response.StartsWith('@')) _echo[id] = ParseId(response[1..]);
                else
                {
                    var data = CommText.ParseHex(response);
                    if (Lin.Validate(id, data.Length) is { } error) throw new FormatException(error);
                    _responses[id] = data;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new FormatException($"Zeile {line}: {ex.Message}");
            }
        }
    }

    private static byte ParseId(string text)
    {
        var text2 = text.Trim();
        var id = text2.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.Parse(text2[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : byte.Parse(text2, NumberStyles.Integer, CultureInfo.InvariantCulture);
        return id <= 0x3F ? id : throw new FormatException($"LIN-ID {text2} größer als 0x3F.");
    }

    /// <summary>Vom Master gesendete Frames (Tests, Anzeige).</summary>
    public ConcurrentQueue<(byte Id, byte[] Data)> Sent { get; } = new();

    public Task<byte[]> RequestAsync(string channel, byte frameId, int length, LinChecksum checksum, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Lin.Validate(frameId, length) is { } error) throw new CommunicationException(error);
        byte[]? data = _responses.TryGetValue(frameId, out var bytes) ? bytes
            : _echo.TryGetValue(frameId, out var source) && _published.TryGetValue(source, out var bytes2) ? bytes2 : null;
        if (data is null) throw new CommunicationTimeoutException($"LIN 0x{frameId:X2}: kein Slave antwortet (Simulation).", []);
        if (data.Length != length) throw new CommunicationException($"LIN 0x{frameId:X2}: Slave antwortet mit {data.Length} statt {length} Bytes (Simulation).");
        return Task.FromResult(data.ToArray());
    }

    public Task SendAsync(string channel, byte frameId, byte[] data, LinChecksum checksum, CancellationToken cancellationToken)
    {
        if (Lin.Validate(frameId, data.Length) is { } error) throw new CommunicationException(error);
        _published[frameId] = data.ToArray();
        Sent.Enqueue((frameId, data.ToArray()));
        return Task.CompletedTask;
    }
}

/// <summary>
/// DoIP-Steuergerät (ISO 13400-2) als Byte-Strom: Routing-Aktivierung, Quittung je Diagnosenachricht, UDS-Regeln wie beim
/// CAN-Steuergerät (<c>uds: 22 F1 90 =&gt; 62 F1 90 "TEXT"</c>). Einstellungen: <c>doip 0x1000</c> (logische Adresse des
/// Steuergeräts), <c>routing=abgelehnt</c> (Aktivierung mit Code 0x00 ablehnen).
/// </summary>
public sealed class SimulatedDoIpDut : IByteStreamAdapter
{
    private readonly List<SimulatedCanDut.UdsRule> _rules = [];
    private readonly List<byte> _received = [];
    private readonly ConcurrentQueue<byte> _output = new();
    private readonly object _gate = new();
    private readonly ushort _address = 0x1000;
    private readonly bool _refuseRouting;
    private bool _active;

    public SimulatedDoIpDut(string rules)
    {
        foreach (var (line, text) in CommSimulation.Lines(rules))
        {
            try
            {
                if (text.StartsWith("uds:", StringComparison.OrdinalIgnoreCase)) _rules.Add(SimulatedCanDut.ParseUds(text[4..]));
                else if (text.StartsWith("doip ", StringComparison.OrdinalIgnoreCase))
                    _address = (ushort)SimulatedCanDut.Id(text[5..].Trim()).Id;
                else if (text.Equals("routing=abgelehnt", StringComparison.OrdinalIgnoreCase)) _refuseRouting = true;
                else throw new FormatException("„doip ADRESSE“, „routing=abgelehnt“ oder „uds: ANFRAGE => ANTWORT“ erwartet.");
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new FormatException($"Zeile {line}: {ex.Message}");
            }
        }
    }

    /// <summary>Empfangene UDS-Anfragen (Tests).</summary>
    public ConcurrentQueue<byte[]> Requests { get; } = new();

    private void Reply(byte version, ushort type, byte[] payload)
    {
        foreach (var frame in DoIpTransport.Frame(version, type, payload)) _output.Enqueue(frame);
    }

    public Task SendAsync(string channel, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _received.AddRange(data.ToArray());
            while (_received.Count >= 8)
            {
                var length = _received[4] << 24 | _received[5] << 16 | _received[6] << 8 | _received[7];
                if (_received.Count < 8 + length) break;
                var version = _received[0];
                var type = (ushort)(_received[2] << 8 | _received[3]);
                var payload = _received.Skip(8).Take(length).ToArray();
                _received.RemoveRange(0, 8 + length);
                Handle(version, type, payload);
            }
        }
        return Task.CompletedTask;
    }

    private void Handle(byte version, ushort type, byte[] payload)
    {
        byte[] Addr(ushort value) => [(byte)(value >> 8), (byte)value];
        switch (type)
        {
            case DoIpTransport.RoutingActivationRequest when payload.Length >= 2:
                _active = !_refuseRouting;
                Reply(version, DoIpTransport.RoutingActivationResponse, [payload[0], payload[1], .. Addr(_address), (byte)(_refuseRouting ? 0x00 : 0x10), 0, 0, 0, 0]);
                break;
            case DoIpTransport.DiagnosticMessage when payload.Length > 4:
                {
                    var tester = (ushort)(payload[0] << 8 | payload[1]);
                    var target = (ushort)(payload[2] << 8 | payload[3]);
                    if (!_active) { Reply(version, DoIpTransport.DiagnosticNack, [.. Addr(target), .. Addr(tester), 0x06]); break; }
                    if (target != _address) { Reply(version, DoIpTransport.DiagnosticNack, [.. Addr(target), .. Addr(tester), 0x03]); break; }
                    Reply(version, DoIpTransport.DiagnosticAck, [.. Addr(_address), .. Addr(tester), 0x00]);
                    var request = payload[4..];
                    Requests.Enqueue(request);
                    foreach (var response in SimulatedCanDut.AnswerUds(_rules, request))
                        Reply(version, DoIpTransport.DiagnosticMessage, [.. Addr(_address), .. Addr(tester), .. response]);
                    break;
                }
        }
    }

    public Task<byte[]> ReceiveAsync(string channel, ReceiveRule rule, CancellationToken cancellationToken)
    {
        var count = rule.Length ?? _output.Count;
        if (_output.Count < count || count == 0)
            throw new CommunicationTimeoutException($"DoIP-Simulation: keine Antwort ({_output.Count} von {count} Bytes).", [.. _output]);
        var result = new byte[count];
        for (var index = 0; index < count; index++) _output.TryDequeue(out result[index]);
        return Task.FromResult(result);
    }

    public void DiscardInput(string channel) => _output.Clear();
}
