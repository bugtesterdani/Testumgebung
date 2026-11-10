/**
 * @file
 * @brief Übersetzt sprachübergreifende Geräteoperationen aus Codebausteinen in dieselben Protokollzugriffe wie grafische Bausteine.
 */

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
        public object? Raw(string name) => values.TryGetValue(name, out var value) ? value : null;

        public string Text(string name, string? fallback = null)
            => Raw(name) switch
            {
                null => fallback ?? throw new CommunicationException($"{operation}: Argument „{name}“ fehlt."),
                string text => text,
                var value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            };

        public long Long(string name, long? fallback = null)
            => Raw(name) switch
            {
                null => fallback ?? throw new CommunicationException($"{operation}: Argument „{name}“ fehlt."),
                long integerValue => integerValue,
                double number => (long)number,
                bool flag => flag ? 1 : 0,
                string text when text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                string text2 => long.Parse(text2, CultureInfo.InvariantCulture),
                var value => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            };

        public long? OptionalLong(string name) => Raw(name) is null ? null : Long(name);

        public bool Flag(string name) => Raw(name) switch
        {
            null => false,
            bool flag => flag,
            long integerValue => integerValue != 0,
            string text => text is "1" or "true" or "True" or "ja",
            _ => false,
        };

        public TimeSpan Timeout(string name, int fallbackMs) => TimeSpan.FromMilliseconds(Raw(name) is null ? fallbackMs : Long(name));

        /// <summary>Ein Skalar aus Text: true/false, Zahl oder Zeichenkette.</summary>
        public static object? Scalar(string text)
        {
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)) return length;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
            return text;
        }
    }

    /// <summary>Operation auf einem aufgelösten Gerät ausführen.</summary>
    public static async Task<object?> ExecuteAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments,
        CancellationToken token)
    {
        var args = new Args(arguments, operation);
        if (!OperationHandlers.TryGetValue(operation, out var handler))
            throw new CommunicationException($"Unbekannte Kommunikationsoperation: {operation}.");
        return await handler(adapter, channel, operation, arguments, args, token).ConfigureAwait(false);
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

    private static async Task<object?> ExecuteCommConfigureAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        var values = ParseOptions(args.Text("options", ""));
        await CommunicationBlocks.As<IConfigurableConnection>(adapter, "konfigurierbare Verbindung")
            .ConfigureAsync(args.Text("address"), values, token).ConfigureAwait(false);
        return null;

    }

    private static async Task<object?> ExecuteHttpRequestAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        var request = new RemoteRequest(operation == "http.request" ? "http" : operation == "ssh.exec" ? "ssh" : operation,
            args.Text(operation == "http.request" ? "path" : operation == "ssh.exec" ? "command" : "remote", operation == "http.request" ? "/" : ""),
            args.Text("method", "GET"), args.Text("body", ""), args.Text("headers", ""), args.Text("local", ""), args.Timeout("timeout", 30000), args.Flag("overwrite"));
        var response = await CommunicationBlocks.As<IRemoteConnection>(adapter, "SSH/SFTP/HTTP").ExecuteAsync(request, token).ConfigureAwait(false);
        return new Dictionary<string, object?> { ["text"] = response.Text, ["code"] = response.Code, ["error"] = response.Error, ["hostKeySHA256"] = response.HostKeySHA256, ["hostKeyBase64"] = response.HostKeyBase64 };

    }

    private static async Task<object?> ExecuteCommSendAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        var stream = CommunicationBlocks.As<IByteStreamAdapter>(adapter, "Byte-Strom");
        await stream.SendAsync(channel, CommText.Parse(args.Text("data"), args.Flag("hex")), token).ConfigureAwait(false);
        return null;

    }

    private static async Task<object?> ExecuteCommReceiveAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        var stream = CommunicationBlocks.As<IByteStreamAdapter>(adapter, "Byte-Strom");
        var hex = args.Flag("hex");
        var end = CommText.Parse(args.Text("end", "\r\n"), hex: false);
        if (operation == "comm.query")
        {
            stream.DiscardInput(channel);
            byte[] request = [.. CommText.Parse(args.Text("data"), hex), .. hex ? [] : end];
            await stream.SendAsync(channel, request, token).ConfigureAwait(false);
        }
        var length = args.OptionalLong("length");
        var rule = length is { } count ? new ReceiveRule(null, (int)count, args.Timeout("timeout", 1000))
            : end.Length > 0 && !hex ? new ReceiveRule(end, null, args.Timeout("timeout", 1000))
            : new ReceiveRule(null, null, args.Timeout("timeout", 1000));
        var response = await stream.ReceiveAsync(channel, rule, token).ConfigureAwait(false);
        return hex ? Convert.ToHexString(response) : CommText.Raw(response);

    }

    private static async Task<object?> ExecuteModbusReadAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        var modbus = CommunicationBlocks.As<IModbusAdapter>(adapter, "Modbus");
        var (area, address) = ModbusCodec.ParseAddress(args.Text("address"));
        if (area is ModbusArea.Coil or ModbusArea.DiscreteInput)
            return (await modbus.ReadBitsAsync(channel, area, address, 1, token).ConfigureAwait(false))[0];
        var type = ModbusCodec.ParseType(args.Text("type", "UInt16"));
        var regs = await modbus.ReadRegistersAsync(channel, area, address, ModbusCodec.Registers(type, (int)args.Long("length", 0)), token).ConfigureAwait(false);
        return ModbusCodec.Decode(regs, type, args.Flag("lowFirst"));

    }

    private static async Task<object?> ExecuteModbusWriteAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        var modbus = CommunicationBlocks.As<IModbusAdapter>(adapter, "Modbus");
        var (area, address) = ModbusCodec.ParseAddress(args.Text("address"));
        var value = args.Raw("value") ?? throw new CommunicationException("modbus.write: Argument „value“ fehlt.");
        if (area == ModbusArea.Coil)
            await modbus.WriteBitsAsync(channel, address, [Convert.ToBoolean(value, CultureInfo.InvariantCulture)], token).ConfigureAwait(false);
        else if (area == ModbusArea.HoldingRegister)
            await modbus.WriteRegistersAsync(channel, address, ModbusCodec.Encode(value, ModbusCodec.ParseType(args.Text("type", "UInt16")), args.Flag("lowFirst")), token).ConfigureAwait(false);
        else throw new CommunicationException("Modbus schreiben: nur Holding-Register (HR) oder Coils (C).");
        return null;

    }

    private static async Task<object?> ExecuteCanSendAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        var id = (uint)args.Long("id");
        var frame = new CanFrame(id, CommText.ParseHex(args.Text("data", "")), args.Flag("extended") || id > 0x7FF, args.Flag("fd"), args.Flag("fd"));
        if (frame.Validate() is { } error) throw new CommunicationException(error);
        await CommunicationBlocks.As<ICanAdapter>(adapter, "CAN").SendAsync(channel, frame, token).ConfigureAwait(false);
        return null;

    }

    private static async Task<object?> ExecuteCanReceiveAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        var can = CommunicationBlocks.As<ICanAdapter>(adapter, "CAN");
        var id = (uint)args.Long("id");
        bool? extended = args.Raw("extended") is null ? null : args.Flag("extended");
        if (args.Flag("discard")) can.DiscardInput(channel);
        var frame = await can.ReceiveAsync(channel, new CanFilter(id, (uint)(args.OptionalLong("mask") ?? 0x1FFFFFFF), extended), args.Timeout("timeout", 1000), token)
            .ConfigureAwait(false);
        return Frame(frame);

    }

    private static async Task<object?> ExecuteUdsAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        IDiagnosticTransport transport;
        if (args.Text("transport", "can").Equals("doip", StringComparison.OrdinalIgnoreCase))
            transport = new DoIpTransport(CommunicationBlocks.As<IByteStreamAdapter>(adapter, "Byte-Strom (TCP)"), channel,
                new DoIpSettings((ushort)args.Long("tester", 0x0E00), (ushort)args.Long("ecu", 0x1000)));
        else
        {
            var extended = args.Flag("extended");
            var settings = new IsoTpSettings((uint)args.Long("tx", 0x7E0), (uint)args.Long("rx", 0x7E8), extended || args.Long("tx", 0x7E0) > 0x7FF, args.Flag("fd"),
                (byte)args.Long("padding", 0xCC))
            {
                TxAddress = args.OptionalLong("txAddress") is { } ta ? (byte)ta : null,
                RxAddress = args.OptionalLong("rxAddress") is { } ra ? (byte)ra : null,
            };
            transport = new IsoTpTransport(CommunicationBlocks.As<ICanAdapter>(adapter, "CAN"), channel, settings);
        }
        var response = await Uds.RequestAsync(transport, CommText.ParseHex(args.Text("request")),
            args.Timeout("timeout", 1000), args.Timeout("timeoutPending", 5000), token).ConfigureAwait(false);
        return Convert.ToHexString(response);

    }

    private static async Task<object?> ExecuteJ1939SendAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        await J1939.SendAsync(CommunicationBlocks.As<ICanAdapter>(adapter, "CAN"), channel, (uint)args.Long("pgn"), CommText.ParseHex(args.Text("data")),
            (byte)args.Long("source", 0xF9), (byte)args.Long("destination", J1939.Global), (int)args.Long("priority", 6), token).ConfigureAwait(false);
        return null;

    }

    private static async Task<object?> ExecuteJ1939ReceiveAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        var can = CommunicationBlocks.As<ICanAdapter>(adapter, "CAN");
        var pgn = (uint)args.Long("pgn");
        var own = (byte)args.Long("own", 0xF9);
        byte? source = args.OptionalLong("source") is { } size ? (byte)size : null;
        can.DiscardInput(channel);
        if (args.Flag("request")) await J1939.RequestAsync(can, channel, pgn, own, source ?? J1939.Global, token).ConfigureAwait(false);
        var j1939Message = await J1939.ReceiveAsync(can, channel, pgn, source, own, args.Timeout("timeout", 1000), token).ConfigureAwait(false);
        return new Dictionary<string, object?>
        {
            ["pgn"] = (long)j1939Message.Pgn,
            ["source"] = (long)j1939Message.Source,
            ["priority"] = (long)j1939Message.Priority,
            ["data"] = Convert.ToHexString(j1939Message.Data),
        };

    }

    private static async Task<object?> ExecuteLinReadAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        var data = await CommunicationBlocks.As<ILinAdapter>(adapter, "LIN").RequestAsync(channel, (byte)args.Long("id"), (int)args.Long("length", 8),
            args.Flag("classic") ? LinChecksum.Classic : LinChecksum.Enhanced, args.Timeout("timeout", 100), token).ConfigureAwait(false);
        return Convert.ToHexString(data);

    }

    private static async Task<object?> ExecuteLinWriteAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        await CommunicationBlocks.As<ILinAdapter>(adapter, "LIN").SendAsync(channel, (byte)args.Long("id"), CommText.ParseHex(args.Text("data")),
            args.Flag("classic") ? LinChecksum.Classic : LinChecksum.Enhanced, token).ConfigureAwait(false);
        return null;

    }

    private static async Task<object?> ExecuteOpcuaReadAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        var opc = Opc(adapter, channel);
        await opc.ConnectAsync(token).ConfigureAwait(false);
        return await opc.ReadAsync(args.Text("node"), token).ConfigureAwait(false);

    }

    private static async Task<object?> ExecuteOpcuaWriteAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args, CancellationToken token)
    {
        var opc = Opc(adapter, channel);
        await opc.ConnectAsync(token).ConfigureAwait(false);
        await opc.WriteAsync(args.Text("node"), args.Raw("value"), token).ConfigureAwait(false);
        return null;

    }

    private static async Task<object?> ExecuteOpcuaCallAsync(IHardwareAdapter adapter, string channel, string operation, IReadOnlyDictionary<string, object?> arguments, Args args2, CancellationToken token)
    {
        var opc = Opc(adapter, channel);
        await opc.ConnectAsync(token).ConfigureAwait(false);
        var args = arguments.TryGetValue("args", out var raw) && raw is string text && text.Length > 0
            ? text.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Args.Scalar).ToList()
            : new List<object?>();
        var outputs = await opc.CallAsync(args2.Text("object"), args2.Text("method"), args, token).ConfigureAwait(false);
        return string.Join(";", outputs.Select(output => Convert.ToString(output, CultureInfo.InvariantCulture)));

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

    private static Dictionary<string, object?> Frame(CanFrame canFrame) => new()
    {
        ["id"] = (long)canFrame.Id,
        ["data"] = Convert.ToHexString(canFrame.Data),
        ["extended"] = canFrame.Extended,
        ["fd"] = canFrame.Fd,
    };

    /// <summary>Über den Schrittkontext (Reservierung, Zugriffsprotokoll).</summary>
    public static Task<object?> ExecuteAsync(Domain.Definitions.IStepContext context, string operation, IReadOnlyDictionary<string, object?> arguments)
    {
        if (!IsDeviceOperation(operation)) throw new CommunicationException($"Unbekannte Kommunikationsoperation „{operation}“.");
        var resource = arguments.TryGetValue("resource", out var result) && result is string text && text.Length > 0
            ? text : throw new CommunicationException($"{operation}: Argument „resource“ (abstrakter Gerätename) fehlt.");
        return context.UseDeviceAsync(resource, "Code: " + operation, (adapter, channel, token) => ExecuteAsync(adapter, channel, operation, arguments, token));
    }
}
