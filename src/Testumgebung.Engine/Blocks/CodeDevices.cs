using System.Globalization;
using Testumgebung.Contracts;

namespace Testumgebung.Engine.Blocks;

/// <summary>
/// Kommunikationsoperationen für CodeBlocks (Python, C#, C++): dieselben Protokolle wie die Bausteine, aufgerufen über
/// <see cref="Domain.Code.ICodeHost.DeviceAsync"/>. Argumente kommen als JSON-Werte (bool, long, double, string).
/// </summary>
public static class CodeDevices
{
    public static readonly IReadOnlyList<string> Operations =
    [
        "comm.send", "comm.receive", "comm.query", "modbus.read", "modbus.write", "can.send", "can.receive", "uds",
        "j1939.send", "j1939.receive", "lin.read", "lin.write",
        "opcua.read", "opcua.write", "opcua.call",
        "comm.configure", "http.request", "ssh.exec", "sftp.upload", "sftp.download",
    ];

    public static bool IsDeviceOperation(string? operation) => operation is not null && Operations.Contains(operation);

    private sealed class Args(IReadOnlyDictionary<string, object?> values, string operation)
    {
        public object? Raw(string name) => values.TryGetValue(name, out var v) ? v : null;

        public string Text(string name, string? fallback = null)
            => Raw(name) switch
            {
                null => fallback ?? throw new CommunicationException($"{operation}: Argument „{name}“ fehlt."),
                string s => s,
                var v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
            };

        public long Long(string name, long? fallback = null)
            => Raw(name) switch
            {
                null => fallback ?? throw new CommunicationException($"{operation}: Argument „{name}“ fehlt."),
                long l => l,
                double d => (long)d,
                bool b => b ? 1 : 0,
                string s when s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => long.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                string s => long.Parse(s, CultureInfo.InvariantCulture),
                var v => Convert.ToInt64(v, CultureInfo.InvariantCulture),
            };

        public long? OptionalLong(string name) => Raw(name) is null ? null : Long(name);

        public bool Flag(string name) => Raw(name) switch
        {
            null => false,
            bool b => b,
            long l => l != 0,
            string s => s is "1" or "true" or "True" or "ja",
            _ => false,
        };

        public TimeSpan Timeout(string name, int fallbackMs) => TimeSpan.FromMilliseconds(Raw(name) is null ? fallbackMs : Long(name));

        /// <summary>Ein Skalar aus Text: true/false, Zahl oder Zeichenkette.</summary>
        public static object? Scalar(string text)
        {
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return text;
        }
    }

    /// <summary>Operation auf einem aufgelösten Gerät ausführen.</summary>
    public static async Task<object?> ExecuteAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments,
        CancellationToken token)
    {
        var a = new Args(arguments, operation);
        if (!OperationHandlers.TryGetValue(operation, out var handler))
            throw new CommunicationException($"Unbekannte Kommunikationsoperation: {operation}.");
        return await handler(adapter, channel, operation, arguments, a, token).ConfigureAwait(false);
    }

    private delegate Task<object?> DeviceOperation(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token);

    private static readonly IReadOnlyDictionary<string, DeviceOperation> OperationHandlers = new Dictionary<string, DeviceOperation>(StringComparer.Ordinal)
    {
        ["comm.configure"] = ExecuteCommConfigureAsync,
        ["http.request"] = ExecuteHttpRequestAsync,
        ["ssh.exec"] = ExecuteHttpRequestAsync,
        ["sftp.upload"] = ExecuteHttpRequestAsync,
        ["sftp.download"] = ExecuteHttpRequestAsync,
        ["comm.send"] = ExecuteCommSendAsync,
        ["comm.receive"] = ExecuteCommReceiveAsync,
        ["comm.query"] = ExecuteCommReceiveAsync,
        ["modbus.read"] = ExecuteModbusReadAsync,
        ["modbus.write"] = ExecuteModbusWriteAsync,
        ["can.send"] = ExecuteCanSendAsync,
        ["can.receive"] = ExecuteCanReceiveAsync,
        ["uds"] = ExecuteUdsAsync,
        ["j1939.send"] = ExecuteJ1939SendAsync,
        ["j1939.receive"] = ExecuteJ1939ReceiveAsync,
        ["lin.read"] = ExecuteLinReadAsync,
        ["lin.write"] = ExecuteLinWriteAsync,
        ["opcua.read"] = ExecuteOpcuaReadAsync,
        ["opcua.write"] = ExecuteOpcuaWriteAsync,
        ["opcua.call"] = ExecuteOpcuaCallAsync,
    };

    private static async Task<object?> ExecuteCommConfigureAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var values = ParseOptions(a.Text("options", ""));
        await CommunicationBlocks.As<IConfigurableConnection>(adapter, "konfigurierbare Verbindung")
            .ConfigureAsync(a.Text("address"), values, token).ConfigureAwait(false);
        return null;

    }

    private static async Task<object?> ExecuteHttpRequestAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var request = new RemoteRequest(operation == "http.request" ? "http" : operation == "ssh.exec" ? "ssh" : operation,
            a.Text(operation == "http.request" ? "path" : operation == "ssh.exec" ? "command" : "remote", operation == "http.request" ? "/" : ""),
            a.Text("method", "GET"), a.Text("body", ""), a.Text("headers", ""), a.Text("local", ""), a.Timeout("timeout", 30000), a.Flag("overwrite"));
        var response = await CommunicationBlocks.As<IRemoteConnection>(adapter, "SSH/SFTP/HTTP").ExecuteAsync(request, token).ConfigureAwait(false);
        return new Dictionary<string, object?> { ["text"] = response.Text, ["code"] = response.Code, ["error"] = response.Error };

    }

    private static async Task<object?> ExecuteCommSendAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var stream = CommunicationBlocks.As<IByteStreamAdapter>(adapter, "Byte-Strom");
        await stream.SendAsync(channel, CommText.Parse(a.Text("data"), a.Flag("hex")), token).ConfigureAwait(false);
        return null;

    }

    private static async Task<object?> ExecuteCommReceiveAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var stream = CommunicationBlocks.As<IByteStreamAdapter>(adapter, "Byte-Strom");
        var hex = a.Flag("hex");
        var end = CommText.Parse(a.Text("end", "\r\n"), hex: false);
        if (operation == "comm.query")
        {
            stream.DiscardInput(channel);
            byte[] request = [.. CommText.Parse(a.Text("data"), hex), .. hex ? [] : end];
            await stream.SendAsync(channel, request, token).ConfigureAwait(false);
        }
        var length = a.OptionalLong("length");
        var rule = length is { } n ? new ReceiveRule(null, (int)n, a.Timeout("timeout", 1000))
            : end.Length > 0 && !hex ? new ReceiveRule(end, null, a.Timeout("timeout", 1000))
            : new ReceiveRule(null, null, a.Timeout("timeout", 1000));
        var response = await stream.ReceiveAsync(channel, rule, token).ConfigureAwait(false);
        return hex ? Convert.ToHexString(response) : CommText.Raw(response);

    }

    private static async Task<object?> ExecuteModbusReadAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var modbus = CommunicationBlocks.As<IModbusAdapter>(adapter, "Modbus");
        var (area, address) = ModbusCodec.ParseAddress(a.Text("address"));
        if (area is ModbusArea.Coil or ModbusArea.DiscreteInput)
            return (await modbus.ReadBitsAsync(channel, area, address, 1, token).ConfigureAwait(false))[0];
        var type = ModbusCodec.ParseType(a.Text("type", "UInt16"));
        var regs = await modbus.ReadRegistersAsync(channel, area, address, ModbusCodec.Registers(type, (int)a.Long("length", 0)), token).ConfigureAwait(false);
        return ModbusCodec.Decode(regs, type, a.Flag("lowFirst"));

    }

    private static async Task<object?> ExecuteModbusWriteAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var modbus = CommunicationBlocks.As<IModbusAdapter>(adapter, "Modbus");
        var (area, address) = ModbusCodec.ParseAddress(a.Text("address"));
        var value = a.Raw("value") ?? throw new CommunicationException("modbus.write: Argument „value“ fehlt.");
        if (area == ModbusArea.Coil)
            await modbus.WriteBitsAsync(channel, address, [Convert.ToBoolean(value, CultureInfo.InvariantCulture)], token).ConfigureAwait(false);
        else if (area == ModbusArea.HoldingRegister)
            await modbus.WriteRegistersAsync(channel, address, ModbusCodec.Encode(value, ModbusCodec.ParseType(a.Text("type", "UInt16")), a.Flag("lowFirst")), token).ConfigureAwait(false);
        else throw new CommunicationException("Modbus schreiben: nur Holding-Register (HR) oder Coils (C).");
        return null;

    }

    private static async Task<object?> ExecuteCanSendAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var id = (uint)a.Long("id");
        var frame = new CanFrame(id, CommText.ParseHex(a.Text("data", "")), a.Flag("extended") || id > 0x7FF, a.Flag("fd"), a.Flag("fd"));
        if (frame.Validate() is { } error) throw new CommunicationException(error);
        await CommunicationBlocks.As<ICanAdapter>(adapter, "CAN").SendAsync(channel, frame, token).ConfigureAwait(false);
        return null;

    }

    private static async Task<object?> ExecuteCanReceiveAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var can = CommunicationBlocks.As<ICanAdapter>(adapter, "CAN");
        var id = (uint)a.Long("id");
        bool? extended = a.Raw("extended") is null ? null : a.Flag("extended");
        if (a.Flag("discard")) can.DiscardInput(channel);
        var frame = await can.ReceiveAsync(channel, new CanFilter(id, (uint)(a.OptionalLong("mask") ?? 0x1FFFFFFF), extended), a.Timeout("timeout", 1000), token)
            .ConfigureAwait(false);
        return Frame(frame);

    }

    private static async Task<object?> ExecuteUdsAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        IDiagnosticTransport transport;
        if (a.Text("transport", "can").Equals("doip", StringComparison.OrdinalIgnoreCase))
            transport = new DoIpTransport(CommunicationBlocks.As<IByteStreamAdapter>(adapter, "Byte-Strom (TCP)"), channel,
                new DoIpSettings((ushort)a.Long("tester", 0x0E00), (ushort)a.Long("ecu", 0x1000)));
        else
        {
            var extended = a.Flag("extended");
            var settings = new IsoTpSettings((uint)a.Long("tx", 0x7E0), (uint)a.Long("rx", 0x7E8), extended || a.Long("tx", 0x7E0) > 0x7FF, a.Flag("fd"),
                (byte)a.Long("padding", 0xCC))
            {
                TxAddress = a.OptionalLong("txAddress") is { } ta ? (byte)ta : null,
                RxAddress = a.OptionalLong("rxAddress") is { } ra ? (byte)ra : null,
            };
            transport = new IsoTpTransport(CommunicationBlocks.As<ICanAdapter>(adapter, "CAN"), channel, settings);
        }
        var response = await Uds.RequestAsync(transport, CommText.ParseHex(a.Text("request")),
            a.Timeout("timeout", 1000), a.Timeout("timeoutPending", 5000), token).ConfigureAwait(false);
        return Convert.ToHexString(response);

    }

    private static async Task<object?> ExecuteJ1939SendAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        await J1939.SendAsync(CommunicationBlocks.As<ICanAdapter>(adapter, "CAN"), channel, (uint)a.Long("pgn"), CommText.ParseHex(a.Text("data")),
            (byte)a.Long("source", 0xF9), (byte)a.Long("destination", J1939.Global), (int)a.Long("priority", 6), token).ConfigureAwait(false);
        return null;

    }

    private static async Task<object?> ExecuteJ1939ReceiveAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var can = CommunicationBlocks.As<ICanAdapter>(adapter, "CAN");
        var pgn = (uint)a.Long("pgn");
        var own = (byte)a.Long("own", 0xF9);
        byte? source = a.OptionalLong("source") is { } s ? (byte)s : null;
        can.DiscardInput(channel);
        if (a.Flag("request")) await J1939.RequestAsync(can, channel, pgn, own, source ?? J1939.Global, token).ConfigureAwait(false);
        var m = await J1939.ReceiveAsync(can, channel, pgn, source, own, a.Timeout("timeout", 1000), token).ConfigureAwait(false);
        return new Dictionary<string, object?>
        {
            ["pgn"] = (long)m.Pgn,
            ["source"] = (long)m.Source,
            ["priority"] = (long)m.Priority,
            ["data"] = Convert.ToHexString(m.Data),
        };

    }

    private static async Task<object?> ExecuteLinReadAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var data = await CommunicationBlocks.As<ILinAdapter>(adapter, "LIN").RequestAsync(channel, (byte)a.Long("id"), (int)a.Long("length", 8),
            a.Flag("classic") ? LinChecksum.Classic : LinChecksum.Enhanced, a.Timeout("timeout", 100), token).ConfigureAwait(false);
        return Convert.ToHexString(data);

    }

    private static async Task<object?> ExecuteLinWriteAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        await CommunicationBlocks.As<ILinAdapter>(adapter, "LIN").SendAsync(channel, (byte)a.Long("id"), CommText.ParseHex(a.Text("data")),
            a.Flag("classic") ? LinChecksum.Classic : LinChecksum.Enhanced, token).ConfigureAwait(false);
        return null;

    }

    private static async Task<object?> ExecuteOpcuaReadAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var opc = Opc(adapter, channel);
        await opc.ConnectAsync(token).ConfigureAwait(false);
        return await opc.ReadAsync(a.Text("node"), token).ConfigureAwait(false);

    }

    private static async Task<object?> ExecuteOpcuaWriteAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var opc = Opc(adapter, channel);
        await opc.ConnectAsync(token).ConfigureAwait(false);
        await opc.WriteAsync(a.Text("node"), a.Raw("value"), token).ConfigureAwait(false);
        return null;

    }

    private static async Task<object?> ExecuteOpcuaCallAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args a, CancellationToken token)
    {
        var opc = Opc(adapter, channel);
        await opc.ConnectAsync(token).ConfigureAwait(false);
        var args = arguments.TryGetValue("args", out var raw) && raw is string s && s.Length > 0
            ? s.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Args.Scalar).ToList()
            : new List<object?>();
        var outputs = await opc.CallAsync(a.Text("object"), a.Text("method"), args, token).ConfigureAwait(false);
        return string.Join(";", outputs.Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)));

    }

    private static IReadOnlyDictionary<string, string> ParseOptions(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0) throw new CommunicationException("Option braucht Schlüssel=Wert.");
            result.Add(line[..equals].Trim(), line[(equals + 1)..]);
        }
        return result;
    }

    private static IOpcUaClient Opc(IHardwareAdapter adapter, string channel)
        => adapter is IOpcUaClient direct ? direct
            : adapter is IOpcUaDevice device ? device.OpcUa(channel)
            : throw new CommunicationException($"Gerät „{adapter.Name}“ ist kein OPC-UA-Gerät.");

    private static Dictionary<string, object?> Frame(CanFrame f) => new()
    {
        ["id"] = (long)f.Id,
        ["data"] = Convert.ToHexString(f.Data),
        ["extended"] = f.Extended,
        ["fd"] = f.Fd,
    };

    /// <summary>Über den Schrittkontext (Reservierung, Zugriffsprotokoll).</summary>
    public static Task<object?> ExecuteAsync(Domain.Definitions.IStepContext context, string operation, IReadOnlyDictionary<string, object?> arguments)
    {
        if (!IsDeviceOperation(operation)) throw new CommunicationException($"Unbekannte Kommunikationsoperation „{operation}“.");
        var resource = arguments.TryGetValue("resource", out var r) && r is string s && s.Length > 0
            ? s : throw new CommunicationException($"{operation}: Argument „resource“ (abstrakter Gerätename) fehlt.");
        return context.UseDeviceAsync(resource, "Code: " + operation, (adapter, channel, token) => ExecuteAsync(adapter, channel, operation, arguments, token));
    }
}
