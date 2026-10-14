/**
 * @file
 * @brief Ordnet Adapterkennungen den Gerätefabriken zu; Projektprofile ergänzen den eingebauten Katalog.
 */

using Testumgebung.Instruments.Communication;

namespace Testumgebung.Instruments;

/// <summary>
/// Unterstützte reale Geräte (Etappe 10) und ihre Erzeugung aus der Prüfplatzkonfiguration.
/// SCPI-Geräte verbinden sich erst beim ersten Zugriff; NI-DAQmx liest Kanäle und Grenzen aus dem Treiber.
/// </summary>
public static class InstrumentCatalog
{
    /// <summary>DAQmx-Nachbildung ohne NI-Treiber (Vorführung, Tests der Abläufe).</summary>
    public const string DaqmxReplica = "NI-DAQmx (Nachbildung ohne Treiber)";

    /// <summary>Gemeinsame Nachbildung für alle Geräte mit Adapter <see cref="DaqmxReplica"/>.</summary>
    public static SimulatedDaqmxApi Replica { get; } = new();

    public static IReadOnlyList<InstrumentFamily> Families { get; } =
    [
        new(Chroma61602Source.AdapterName, "AC-Quelle Chroma 61602", Chroma61602Source.Models, "COM3",
            "COM/RS232 oder VISA (GPIB/USB/LAN). Kanäle VOLT, FREQ, CURR, POW, OUT. Bereich HIGH/LOW; serielle Optionen Baud, Parität, Datenbits, Stoppbits, Handshake, Zeilenende."),
        new(EtEaq1000Source.AdapterName, "AC-Quelle ET Instrumente EAQ-1000", EtEaq1000Source.Models, "COM4",
            "RS232/COM, auch USB-RS232. Sollwertkanäle VOLT, CURR, FREQ, DC, PHAS, OUT. Serielle Optionen; Pruefbefehl und AbfrageVOLT/CURR/FREQ/DC/PHAS/OUT gemäß Gerätehandbuch."),
        new(Keysight3446xDmm.AdapterName, "Digitalmultimeter Keysight/Agilent", Keysight3446xDmm.Models, "TCPIP::192.168.1.21::5025::SOCKET",
            "Truevolt 34460A/61A/65A/70A über LAN (SOCKET/VXI-11/HiSLIP) oder USB; 34401A/34410A über GPIB/RS-232 (VISA). Kanäle VDC, VAC, IDC, IAC, RES, FRES, FREQ. Option NPLC."),
        new(KeysightE36300Supply.AdapterName, "DC-Netzgerät Keysight/Agilent", KeysightE36300Supply.Models, "TCPIP::192.168.1.22::5025::SOCKET",
            "E36311A/12A/13A (Kanalliste @n), E3631A (GPIB/RS-232). Kanäle CHn (V), CHn:I (A), CHn:OUT."),
        new(KeysightAc6800Source.AdapterName, "AC-Quelle Keysight AC6800B", KeysightAc6800Source.Models, "TCPIP::192.168.1.23::5025::SOCKET",
            "AC6801B…AC6804B (500–4000 VA, 155/310 V, 40–500 Hz). Kanäle VOLT, FREQ, CURR, POW, OUT. Option Bereich (155/310)."),
        new(ThreePhaseAcSource.AdapterName, "Dreiphasige AC-Versorgung aus drei AC6800B", KeysightAc6800Source.Models, "TCPIP::192.168.1.23::5025::SOCKET;TCPIP::192.168.1.24::5025::SOCKET;TCPIP::192.168.1.25::5025::SOCKET",
            "Drei einphasige AC6800B als L1/L2/L3 – drei VISA-Adressen mit „;“ getrennt. Kanäle L1:VOLT, L2:FREQ, … sowie ALLE:VOLT/FREQ/OUT für alle Phasen. Option Bereich (155/310)."),
        new(Keysight33500Generator.AdapterName, "Funktions-/Arbiträrgenerator Keysight/Agilent", Keysight33500Generator.Models, "TCPIP::192.168.1.24::5025::SOCKET",
            "33500B/33600A Trueform. Kanäle CHn:FREQ/AMPL/OFFS/DC/OUT, Kurvenausgabe ARB. Optionen Kurvenkanäle (L1=1;L2=2), Last (50/HighZ), Speicher."),
        new(KeysightInfiniiVisionScope.AdapterName, "Oszilloskop Keysight InfiniiVision", KeysightInfiniiVisionScope.Models, "TCPIP::192.168.1.25::5025::SOCKET",
            "1000X/2000X/3000T/4000X. Kurvenaufnahme CHANn (eine Erfassung, gemeinsame Zeitbasis), Messwerte CHANn:VRMS/VPP/VAVG/FREQ. Optionen Triggerquelle, Triggerpegel."),
        new(DaqmxDevice.AdapterName, "NI-DAQmx (USB, PCIe, CompactDAQ)", DaqmxDevice.Models, "Dev1",
            "Gerätename(n) aus NI MAX, z. B. Dev1 oder cDAQ1Mod1,cDAQ1Mod2. Ohne Hardware: in NI MAX simuliertes Gerät anlegen. Optionen Anschluss, Bereich, Starttrigger, Kurvenkanäle, Sichere Ausgänge."),
        new(DaqmxReplica, "NI-DAQmx-Nachbildung (ohne NI-Treiber)", ["USB-6211 + NI 9225"], "Dev1,cDAQ1Mod1",
            "Nachbildung von Dev1 (USB-6211) und cDAQ1Mod1 (NI 9225) zum Ausprobieren ohne NI-Software."),

        // Kommunikationsgeräte (Prüflingskommunikation)
        new(StreamDevice.TelnetAdapter, "Telnet", ["Telnet"], "192.168.1.10:23", "Byte-Strom mit Telnet-Aushandlung; Senden/Empfangen/Abfrage, Adresse zur Laufzeit änderbar."),
        new(RemoteDevice.SshAdapter, "SSH / SFTP", ["SSH / SFTP"], "192.168.1.10:22", "Optionen Benutzer, Passwort oder Schlüsseldatei, Schlüsselpasswort, HostKeySHA256 (Pflicht). SSH-Befehl und SFTP-Transfer."),
        new(RemoteDevice.HttpAdapter, "HTTP / HTTPS", ["HTTP / HTTPS"], "https://192.168.1.10", "HTTP-Anfragen mit GET, POST, PUT, PATCH, DELETE, HEAD und OPTIONS; normale TLS-Zertifikatsprüfung."),
        new(StreamDevice.SerialAdapter, "Seriell RS-232/RS-485 (COM-Port, USB-Seriell)", ["RS-232/RS-485"], "COM3",
            "Optionen Baud=115200, Datenbits=8, Parität=N, Stoppbits=1, Handshake=Kein|RTS-CTS|XON-XOFF, RS485=ja (RTS-Umschaltung), Prüfbefehl, Zeilenende, Sicherer Zustand. Kanal beliebig (z. B. Daten)."),
        new(StreamDevice.TcpAdapter, "TCP-Client (Text- oder Binärprotokoll)", ["TCP"], "192.168.1.50:5000",
            "Adresse host:port. Optionen Verbindungszeit [ms], Prüfbefehl, Zeilenende, Sicherer Zustand. Verbindet beim ersten Zugriff, nach Abbruch erneut."),
        new(StreamDevice.UdpAdapter, "UDP (Datagramme)", ["UDP"], "192.168.1.50:5000",
            "Adresse host:port, Option Lokaler Port. Jedes Senden ist ein Datagramm."),
        new(ModbusDevice.RtuAdapter, "Modbus RTU (RS-485/RS-232)", ["RTU"], "COM4",
            "Serielle Optionen wie oben (typisch Baud=19200, Parität=E), Einheit (Unit-ID), Wiederholungen, Rahmenpause [ms], Wortfolge=hoch|niedrig, Prüfadresse, Sicherer Zustand=HR10=0;C1=0. Kanal = Unit-ID oder Punkt 1/HR100:float32."),
        new(ModbusDevice.TcpAdapter, "Modbus TCP", ["TCP"], "192.168.1.60:502",
            "Optionen Einheit, Wiederholungen, Wortfolge, RTU über TCP=ja (Gateway), Prüfadresse, Sicherer Zustand. Kanal = Unit-ID oder Punkt 1/IR10:int16."),
        new(CanDevice.PcanAdapter, "CAN/CAN FD – PEAK PCAN-USB/PCIe/LAN", ["PCAN-Basic"], "PCAN_USBBUS1",
            "PCAN-Basic aus dem PEAK-Treiberpaket. Optionen Bitrate=500k, FD=ja, Datenbitrate=2M, FD-Parameter (eigene Zeitparameter), Sicherer Zustand=0x100 00 00."),
        new(CanDevice.KvaserAdapter, "CAN/CAN FD – Kvaser", ["CANlib"], "0",
            "Kvaser Drivers for Windows (canlib32.dll). Ressource = Kanalnummer. Optionen Bitrate, FD, Datenbitrate, Virtuell=ja (virtuelle Kanäle)."),
        new(CanDevice.VectorAdapter, "CAN – Vector (VN16xx, VN1530, CANcaseXL …)", ["XL API"], "Testumgebung:0",
            "Vector XL Driver Library (vxlapi64.dll). Ressource = Anwendungskanal aus „Vector Hardware Config“ (Anwendung:Kanal) oder virtuell:0. Optionen Bitrate, Sicherer Zustand. Klassisches CAN."),
        new(CanDevice.VirtualAdapter, "CAN – virtueller Bus (ohne Hardware)", ["virtuell"], "Bus1",
            "Alle Geräte mit derselben Ressource bilden einen Bus im Programm – zum Ausprobieren von Abläufen und Gegenstellen."),
        new(LinDevice.PlinAdapter, "LIN – PEAK PLIN-USB/PLIN-PCIe (Master)", ["PLIN-API"], "1",
            "PLinApi.dll aus dem PEAK-PLIN-Treiberpaket. Ressource = Hardware 1, 2, …; Optionen Baud=19200, Sicherer Zustand=0x10 00 00."),
        new(LinDevice.VirtualAdapter, "LIN – virtueller Bus (ohne Hardware)", ["virtuell"], "LIN1",
            "Master mit fest eingestellten Slave-Antworten: Option Antworten=0x21=01 02 03 04;0x22=@0x10 (@ = zuletzt gesendete Daten des Frames)."),
        new(OpcUaDevice.AdapterName, "OPC-UA-Server (Variablen, Methoden, Abonnements)", ["OPC UA"], "opc.tcp://192.168.1.70:4840",
            "Endpunkt opc.tcp://host:port. Optionen Sicherheit=ja, Benutzer, Passwort, Prüfknoten, Sicherer Zustand=ns=2;s=Ausgang=false. Bausteine OPC UA bzw. CodeBlock opcua_read/write/call."),
    ];

    /// <summary>Kommunikationsgeräte (Kanalnamen frei: Modbus-Adressen, beliebiger Kanal bei Byte-Strom/CAN).</summary>
    private static readonly HashSet<string> CommunicationAdapters = new(StringComparer.Ordinal)
    {
        StreamDevice.SerialAdapter,
        StreamDevice.TcpAdapter,
        StreamDevice.UdpAdapter,
        ModbusDevice.RtuAdapter,
        ModbusDevice.TcpAdapter,
        CanDevice.PcanAdapter,
        CanDevice.KvaserAdapter,
        CanDevice.VirtualAdapter,
        CanDevice.VectorAdapter,
        LinDevice.PlinAdapter,
        LinDevice.VirtualAdapter,
        OpcUaDevice.AdapterName,
    };

    public static bool IsCommunication(string adapter) => CommunicationAdapters.Contains(adapter);

    public static InstrumentFamily? Family(string adapter) => Families.FirstOrDefault(f => f.Adapter == adapter);

    private enum AdapterKind { Unknown, Dmm, Supply, AcSource, ThreePhase, Generator, Scope, Daqmx, DaqmxReplica, Stream, Remote, Modbus, Can, Lin, OpcUa, Chroma, Eaq }

    private static readonly IReadOnlyDictionary<string, AdapterKind> AdapterKinds = new Dictionary<string, AdapterKind>(StringComparer.Ordinal)
    {
        [Chroma61602Source.AdapterName] = AdapterKind.Chroma,
        [EtEaq1000Source.AdapterName] = AdapterKind.Eaq,
        [Keysight3446xDmm.AdapterName] = AdapterKind.Dmm,
        [KeysightE36300Supply.AdapterName] = AdapterKind.Supply,
        [KeysightAc6800Source.AdapterName] = AdapterKind.AcSource,
        [ThreePhaseAcSource.AdapterName] = AdapterKind.ThreePhase,
        [Keysight33500Generator.AdapterName] = AdapterKind.Generator,
        [KeysightInfiniiVisionScope.AdapterName] = AdapterKind.Scope,
        [DaqmxDevice.AdapterName] = AdapterKind.Daqmx,
        [DaqmxReplica] = AdapterKind.DaqmxReplica,
        [StreamDevice.TelnetAdapter] = AdapterKind.Stream,
        [StreamDevice.UdpAdapter] = AdapterKind.Stream,
        [StreamDevice.TcpAdapter] = AdapterKind.Stream,
        [StreamDevice.SerialAdapter] = AdapterKind.Stream,
        [RemoteDevice.HttpAdapter] = AdapterKind.Remote,
        [RemoteDevice.SshAdapter] = AdapterKind.Remote,
        [ModbusDevice.TcpAdapter] = AdapterKind.Modbus,
        [ModbusDevice.RtuAdapter] = AdapterKind.Modbus,
        [CanDevice.VectorAdapter] = AdapterKind.Can,
        [CanDevice.VirtualAdapter] = AdapterKind.Can,
        [CanDevice.KvaserAdapter] = AdapterKind.Can,
        [CanDevice.PcanAdapter] = AdapterKind.Can,
        [LinDevice.VirtualAdapter] = AdapterKind.Lin,
        [LinDevice.PlinAdapter] = AdapterKind.Lin,
        [OpcUaDevice.AdapterName] = AdapterKind.OpcUa,
    };

    /// <summary>Treiber anlegen (verbindet noch nicht).</summary>
    public static IInstrumentDriver Create(string adapter, string model, string resource, IReadOnlyDictionary<string, string>? options = null, int? timeoutMs = null)
    {
        var opts = new InstrumentOptions(options);
        ScpiSession Session() => new(resource, TimeSpan.FromMilliseconds(timeoutMs ?? 5000));
        ScpiSession AcSession() => resource.Trim().StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            ? new ScpiSession(resource.Trim(), () => new SerialInstrumentIo(resource.Trim(), opts), TimeSpan.FromMilliseconds(timeoutMs ?? 5000))
            : Session();
        return AdapterKinds.GetValueOrDefault(adapter, AdapterKind.Unknown) switch
        {
            AdapterKind.Chroma => new Chroma61602Source(AcSession(), opts),
            AdapterKind.Eaq => new EtEaq1000Source(Model(model, EtEaq1000Source.Models), AcSession(), opts),
            AdapterKind.Dmm => new Keysight3446xDmm(Model(model, Keysight3446xDmm.Models), Session(), opts),
            AdapterKind.Supply => new KeysightE36300Supply(Model(model, KeysightE36300Supply.Models), Session()),
            AdapterKind.AcSource => new KeysightAc6800Source(Model(model, KeysightAc6800Source.Models), Session(), opts),
            AdapterKind.ThreePhase => CreateThreePhase(model, resource, options, timeoutMs),
            AdapterKind.Generator => new Keysight33500Generator(Model(model, Keysight33500Generator.Models), Session(), opts),
            AdapterKind.Scope => new KeysightInfiniiVisionScope(Model(model, KeysightInfiniiVisionScope.Models), Session(), opts),
            AdapterKind.Daqmx => new DaqmxDevice(resource, NativeDaqmxApi.Instance, opts),
            AdapterKind.DaqmxReplica => new DaqmxDevice(resource, Replica, opts),
            AdapterKind.Stream => new StreamDevice(adapter, resource, opts),
            AdapterKind.Remote => new RemoteDevice(adapter, resource, opts),
            AdapterKind.Modbus => new ModbusDevice(adapter, resource, opts, timeoutMs),
            AdapterKind.Can => new CanDevice(adapter, resource, opts),
            AdapterKind.Lin => new LinDevice(adapter, resource, opts),
            AdapterKind.OpcUa => new OpcUaDevice(resource, opts),
            _ => throw new InstrumentException($"Unbekannter Geräteadapter „{adapter}“."),
        };
    }

    private static ThreePhaseAcSource CreateThreePhase(string model, string resource, IReadOnlyDictionary<string, string>? options, int? timeoutMs)
    {
        var addresses = resource.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (addresses.Length != 3) throw new InstrumentException("Dreiphasige AC-Quelle: drei VISA-Adressen mit „;“ getrennt angeben (L1;L2;L3).");
        var opts = new InstrumentOptions(options);
        var phases = addresses.Select(a => (IInstrumentDriver)new KeysightAc6800Source(Model(model, KeysightAc6800Source.Models),
            new ScpiSession(a, TimeSpan.FromMilliseconds(timeoutMs ?? 5000)), opts)).ToList();
        return new ThreePhaseAcSource(resource, phases);
    }

    private static string Model(string model, string[] models) => string.IsNullOrWhiteSpace(model) ? models[0] : model.Trim();

    /// <summary>Angeschlossene Geräte: VISA-Ressourcen (USB, GPIB, LAN) und NI-DAQmx-Geräte, soweit Treiber installiert.</summary>
    public static IReadOnlyList<string> Discover()
        => Discover(() => VisaIo.Find(), VisaIo.IsAvailable, NativeDaqmxApi.Instance.IsAvailable,
            NativeDaqmxApi.Instance.DeviceNames, NativeDaqmxApi.Instance.DeviceInfo, SerialTransport.Ports);

    internal static IReadOnlyList<string> Discover(Func<IReadOnlyList<string>> visaResources, bool visaAvailable, bool daqAvailable,
        Func<IReadOnlyList<string>> daqDevices, Func<string, DaqDeviceInfo> deviceInfo, Func<IReadOnlyList<string>> serialPorts)
    {
        var list = new List<string>();
        try
        {
            list.AddRange(visaResources().Select(r => "VISA: " + r));
        }
        catch (InstrumentException ex)
        {
            list.Add("VISA: " + ex.Message);
        }
        if (daqAvailable)
        {
            try
            {
                foreach (var d in daqDevices())
                {
                    var info = deviceInfo(d);
                    list.Add($"NI-DAQmx: {d} – {info.ProductType}{(info.IsSimulated ? " (simuliert)" : "")}");
                }
            }
            catch (InstrumentException ex)
            {
                list.Add("NI-DAQmx: " + ex.Message);
            }
        }
        foreach (var port in serialPorts()) list.Add("Seriell: " + port);
        if (!visaAvailable) list.Add("Keine VISA-Bibliothek: LAN-Geräte direkt über TCPIP::<Adresse>::5025::SOCKET.");
        if (!daqAvailable) list.Add("NI-DAQmx nicht installiert.");
        return list;
    }
}
