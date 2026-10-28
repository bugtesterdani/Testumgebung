/**
 * @file
 * @brief Definiert transportübergreifende Kommunikationsverträge und Empfangsregeln für die Gerätevermittlung.
 */

namespace Testumgebung.Contracts;

public interface IConfigurableConnection
{
    Task ConfigureAsync(string endpoint, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken);
}

public sealed record RemoteRequest(string Operation, string Argument, string Method = "GET", string Body = "", string Headers = "",
    string LocalPath = "", TimeSpan? Timeout = null, bool Overwrite = false);
public sealed record RemoteResponse(string Text, int Code, string Error = "")
{
    /// <summary>Empfangener SSH-Hostschlüssel als SHA256:…; leer bei HTTP oder Simulation ohne Schlüsseldaten.</summary>
    public string HostKeySHA256 { get; init; } = "";
    /// <summary>Öffentlicher SSH-Schlüsselblob als Base64, kein privater Schlüssel.</summary>
    public string HostKeyBase64 { get; init; } = "";
}
public interface IRemoteConnection
{
    Task<RemoteResponse> ExecuteAsync(RemoteRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Ende einer Empfangsoperation auf einer Byte-Strom-Schnittstelle (Seriell, TCP, UDP): Abschlusszeichen, feste Länge
/// oder Ruhe/Zeitlimit. Ohne Abschluss und Länge endet der Empfang nach <see cref="Idle"/> ohne neue Bytes (mindestens ein Byte).
/// </summary>
/// <param name="Terminator">Abschlussfolge (z. B. CR LF); wird nicht mitgeliefert.</param>
/// <param name="Length">Genau so viele Bytes.</param>
/// <param name="Timeout">Gesamtzeitlimit; danach <see cref="CommunicationTimeoutException"/> mit den bis dahin empfangenen Bytes.</param>
/// <param name="Idle">Ruhezeit, die eine Antwort ohne Abschluss/Länge beendet.</param>
public sealed record ReceiveRule(byte[]? Terminator, int? Length, TimeSpan Timeout, TimeSpan? Idle = null, int MaxBytes = 65536)
{
    public TimeSpan EffectiveIdle => Idle ?? TimeSpan.FromMilliseconds(50);
}

/// <summary>Fehler einer Kommunikationsschnittstelle (Verbindung, Protokoll, Gegenstelle meldet Fehler).</summary>
public class CommunicationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Keine (vollständige) Antwort innerhalb des Zeitlimits.</summary>
public sealed class CommunicationTimeoutException(string message, byte[] partial) : CommunicationException(message)
{
    public byte[] Partial { get; } = partial;
}

/// <summary>Byte-Strom zum Prüfling: RS-232/RS-485, TCP-Client, UDP (je Datagramm).</summary>
public interface IByteStreamAdapter
{
    Task SendAsync(string channel, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    Task<byte[]> ReceiveAsync(string channel, ReceiveRule rule, CancellationToken cancellationToken);

    /// <summary>Noch nicht gelesene Eingangsdaten verwerfen (vor einer Abfrage).</summary>
    void DiscardInput(string channel);
}

/// <summary>Modbus-Datenbereiche (Adressen 0-basiert im Protokoll).</summary>
public enum ModbusArea
{
    Coil,
    DiscreteInput,
    InputRegister,
    HoldingRegister,
}

/// <summary>Modbus RTU/TCP. Kanal = Geräteadresse (Unit-ID) „1“…„247“.</summary>
public interface IModbusAdapter
{
    Task<ushort[]> ReadRegistersAsync(string channel, ModbusArea area, int address, int count, CancellationToken cancellationToken);

    Task<bool[]> ReadBitsAsync(string channel, ModbusArea area, int address, int count, CancellationToken cancellationToken);

    Task WriteRegistersAsync(string channel, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken);

    Task WriteBitsAsync(string channel, int address, IReadOnlyList<bool> values, CancellationToken cancellationToken);
}

/// <summary>Modbus-Ausnahmeantwort der Gegenstelle (Funktionscode | 0x80).</summary>
public sealed class ModbusException(byte function, byte code)
    : CommunicationException($"Modbus-Ausnahme {code:X2} ({Describe(code)}) für Funktion {function:X2}")
{
    public byte Function { get; } = function;
    public byte Code { get; } = code;

    public static string Describe(byte code) => code switch
    {
        1 => "unzulässige Funktion",
        2 => "unzulässige Datenadresse",
        3 => "unzulässiger Datenwert",
        4 => "Gerätefehler",
        5 => "bestätigt, Bearbeitung dauert",
        6 => "Gerät beschäftigt",
        10 => "Gateway-Pfad nicht verfügbar",
        11 => "Gateway-Ziel antwortet nicht",
        _ => "unbekannt",
    };
}

/// <summary>CAN-/CAN-FD-Rahmen. <see cref="Data"/> 0–8 Bytes (klassisch) bzw. 0–64 (FD, gültige DLC-Längen).</summary>
public sealed record CanFrame(uint Id, byte[] Data, bool Extended = false, bool Fd = false, bool BitRateSwitch = false, bool Remote = false)
{
    public DateTimeOffset? Time { get; init; }

    public static readonly int[] FdLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    public string DataHex => Convert.ToHexString(Data);

    public override string ToString() => $"{(Extended ? Id.ToString("X8") : Id.ToString("X3"))}{(Fd ? " FD" : "")}{(Remote ? " RTR" : "")} [{Data.Length}] {string.Join(" ", Data.Select(b => b.ToString("X2")))}";

    /// <summary>Gültigkeit prüfen (Länge, ID-Bereich).</summary>
    public string? Validate()
    {
        if (Extended ? Id > 0x1FFFFFFF : Id > 0x7FF) return $"CAN-ID 0x{Id:X} außerhalb des {(Extended ? "29" : "11")}-Bit-Bereichs.";
        if (!Fd && Data.Length > 8) return "Klassischer CAN-Rahmen: höchstens 8 Datenbytes.";
        if (Fd && !FdLengths.Contains(Data.Length)) return $"CAN FD: {Data.Length} Bytes ist keine gültige Länge ({string.Join(", ", FdLengths)}).";
        if (Fd && Remote) return "CAN FD kennt keine Remote-Rahmen.";
        return null;
    }
}

/// <summary>Filter für den Empfang: (ID &amp; Maske) == (Soll &amp; Maske).</summary>
public sealed record CanFilter(uint Id, uint Mask = 0x1FFFFFFF, bool? Extended = null)
{
    public bool Matches(CanFrame frame) => (frame.Id & Mask) == (Id & Mask) && (Extended is null || Extended == frame.Extended);
}

/// <summary>CAN/CAN FD. Kanal ist frei (z. B. „Bus“).</summary>
public interface ICanAdapter
{
    Task SendAsync(string channel, CanFrame frame, CancellationToken cancellationToken);

    /// <summary>Nächsten passenden Rahmen seit dem letzten <see cref="DiscardInput"/> liefern (gepuffert).</summary>
    Task<CanFrame> ReceiveAsync(string channel, CanFilter filter, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Nächsten Rahmen, der zu einem der Filter passt (J1939-Transportprotokoll, ISO-TP mit Flusssteuerung).</summary>
    Task<CanFrame> ReceiveAnyAsync(string channel, IReadOnlyList<CanFilter> filters, TimeSpan timeout, CancellationToken cancellationToken)
        => filters.Count == 1
            ? ReceiveAsync(channel, filters[0], timeout, cancellationToken)
            : throw new NotSupportedException("Diese CAN-Schnittstelle unterstützt nur einen Empfangsfilter je Aufruf.");

    void DiscardInput(string channel);
}

/// <summary>Prüfsummenart eines LIN-Frames (LIN 2.x: erweitert, Diagnoseframes 0x3C/0x3D immer klassisch).</summary>
public enum LinChecksum
{
    Enhanced,
    Classic,
}

/// <summary>LIN-Master. Kanal ist frei (z. B. „Bus“).</summary>
public interface ILinAdapter
{
    /// <summary>Header senden und die Antwort des Slaves lesen (Subscriber-Frame).</summary>
    Task<byte[]> RequestAsync(string channel, byte frameId, int length, LinChecksum checksum, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Frame mit Daten des Masters senden (Publisher-Frame).</summary>
    Task SendAsync(string channel, byte frameId, byte[] data, LinChecksum checksum, CancellationToken cancellationToken);
}
