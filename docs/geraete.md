# Reale Geräte einbinden (Etappe 10)

Eigene Geräte lassen sich im Projekt definieren: [Anleitung und Beispiel](eigene-geraete.md).

Neu ab 1.0.10.0: [Chroma 61602 und ET Instrumente EAQ-1000](chroma-und-eaq.md), einschließlich direkter COM-/RS232-Kommunikation, auch über einen USB-RS232-Adapter.

Die Testumgebung spricht Geräte über zwei Wege an:

* **SCPI** (Keysight/Agilent): direkt über LAN-Socket (Port 5025, ohne Zusatzsoftware) oder über **VISA** für USB-TMC,
  GPIB, VXI-11 und HiSLIP (Keysight IO Libraries Suite oder NI-VISA; geladen wird `visa64.dll`).
* **NI-DAQmx** (National Instruments): über die C-Bibliothek `nicaiu.dll` des NI-DAQmx-Treibers.

Ohne installierte Treiber startet die Anwendung normal; erst „Gerät testen“ bzw. der erste Zugriff meldet, was fehlt.

## Unterstützte Geräte

| Adapter | Modelle | Kanäle (Prüfplatzkonfiguration) | Ressource (Beispiel) |
|---|---|---|---|
| Keysight 3446xA/34401A DMM | 34460A, 34461A, 34465A, 34470A, 34401A, 34410A, 34411A | `VDC`, `VAC`, `IDC`, `IAC`, `RES`, `FRES` (nicht 34460A), `FREQ` – nur lesen | `TCPIP::192.168.1.21::5025::SOCKET`, `GPIB0::22::INSTR` (34401A) |
| Keysight E36300/E3631A Netzgerät | E36311A, E36312A, E36313A, E3631A | `CHn` (V: setzen = Sollwert, lesen = Messwert), `CHn:I` (A: Strombegrenzung / Messwert), `CHn:OUT` (0/1) | `USB0::0x2A8D::0x1102::MY…::INSTR` |
| Keysight AC6800B AC-Quelle | AC6801B (500 VA), AC6802B (1000 VA), AC6803B, AC6804B | `VOLT` (Effektivwert), `FREQ`, `CURR` (Begrenzung / Messwert), `POW` (lesen), `OUT` | `TCPIP::192.168.1.23::5025::SOCKET` |
| Dreiphasige AC-Quelle (3× AC6800B) | wie AC6800B | `L1:VOLT`, `L2:FREQ`, … je Phase; `ALLE:VOLT/FREQ/OUT` für alle | drei VISA-Adressen mit `;` getrennt |
| Keysight 33500B/33600A Generator | 33509B … 33522B, 33611A … 33622A | `CHn:FREQ`, `CHn:AMPL` (Vss), `CHn:OFFS`, `CHn:DC`, `CHn:OUT`; Kurvenausgabe `ARB` | `TCPIP::192.168.1.24::5025::SOCKET` |
| Keysight InfiniiVision Oszilloskop | DSOX1102G/1204G, DSOX2000A, DSOX3000T, DSOX4000A | Kurvenaufnahme `CHANn`; `CHANn:VRMS`, `:VPP`, `:VAVG`, `:FREQ` (lesen); `CHANn:SCALE`, `:OFFSET`, `TIMEBASE` | `TCPIP0::192.168.1.25::hislip0::INSTR` |
| NI-DAQmx | USB-6001…6003, USB-6211/6212, USB-6343/6363, PCIe-63xx, cDAQ-9174 mit NI 9215/9225/9227/9263/9401 | `Dev1/ai0` (lesen, Kurvenaufnahme), `Dev1/ao0` (setzen), `Dev1/port0/line0` (DO/DI), Kurvenausgabe `AO` | `Dev1` oder `cDAQ1Mod1,cDAQ1Mod2` |
| NI-DAQmx (Nachbildung ohne Treiber) | USB-6211 + NI 9225 | wie NI-DAQmx | `Dev1,cDAQ1Mod1` |

Für den Anwendungsfall 230 V AC / ~1000 W passen z. B. **AC6802B** (Versorgung), **NI 9225** (300 Vrms, 3 Kanäle
simultan) und **NI 9227** (5 Arms) im cDAQ-Chassis oder ein **34465A** für Effektivwerte. Ein Software-Exit ersetzt
keine unabhängige hardwareseitige Abschaltung (Not-Aus, Schütz).

## Optionen je Gerät (Seite „Prüfplatzkonfiguration“ → Geräte → Optionen, eine Zeile je Option)

| Option | Gerät | Bedeutung |
|---|---|---|
| `NPLC=10` | DMM | Integrationszeit in Netzperioden (Gleichgrößen, Widerstand) |
| `Bereich=310` | AC6800B | Spannungsbereich 155 oder 310 V (vor dem Einschalten gesetzt) |
| `Kurvenkanäle=L1=1;L2=2` | Generator | Kurvenkanal → Generatorkanal |
| `Last=HighZ` | Generator | Hochohmige Last: Ausgangsbereich ±10 V statt ±5 V |
| `Speicher=4000000` | Generator | Arbiträrspeicher (Option des Geräts) |
| `Triggerquelle=EXT`, `Triggerpegel=1.5` | Oszilloskop | Getriggerte Aufnahme (Synchronisation mit Generator/Quelle) statt Auto |
| `Anschluss=DIFF` | NI-DAQmx | RSE, NRSE, DIFF, PseudoDiff oder Standard |
| `Bereich=5` | NI-DAQmx | Eingangsbereich ±V (höchstens der Gerätebereich) |
| `Starttrigger=/Dev1/PFI0` | NI-DAQmx | Aufnahme startet mit digitaler Flanke (Synchronisation) |
| `Kurvenkanäle=L1=Dev1/ao0;L2=Dev1/ao1` | NI-DAQmx | Kurvenkanal → Analogausgang |
| `Sichere Ausgänge=Dev1/port1/line0=0` | NI-DAQmx | Digitalausgänge im sicheren Zustand |

## Einrichten

1. **Keysight-Geräte über LAN**: IP-Adresse am Gerät ablesen (Utility → I/O → LAN). Ressource
   `TCPIP::<Adresse>::5025::SOCKET` – keine Zusatzsoftware nötig. Für USB/GPIB/HiSLIP die
   *Keysight IO Libraries Suite* (oder NI-VISA) installieren und die Ressource aus *Connection Expert* übernehmen
   (oder „Geräte suchen“).
2. **NI-DAQmx**: Treiber *NI-DAQmx* installieren, Gerät in *NI MAX* benennen (z. B. `Dev1`). **Ohne Hardware**: in
   NI MAX „Geräte und Schnittstellen → Neu → Simuliertes NI-DAQmx-Gerät“ (z. B. USB-6211 oder cDAQ-9174 mit NI 9225) –
   der Treiber dieser Anwendung arbeitet damit unverändert („Gerät testen“ zeigt „simuliert“).
3. In der Anwendung: **Prüfplatzkonfiguration → + Gerät**, Adapter und Modell wählen, Ressource eintragen,
   **Gerät testen** (liest `*IDN?`, Fehlerwarteschlange und Fähigkeiten – Ausgänge bleiben unverändert).
4. In **Geräte und Signale** den abstrakten Namen des Ablaufs (z. B. `Spannung_Pruefling`) dem Gerät und Kanal
   zuordnen (Kanalauswahl aus dem Treiber). Geräte, die mehrere Prüfplätze nutzen, als **gemeinsam** markieren; der
   Zugriff erfordert dann eine Reservierung an einer Gruppe.
5. **Simulation prüfen** und **Zuordnung speichern**.

**Ohne Geräte ausprobieren**: „Laborsimulatoren starten“ startet Multimeter, Netzgerät, AC-Quelle, Generator und
Oszilloskop als SCPI-Simulatoren auf `localhost` (Ports 50251–50255) und trägt sie samt DAQmx-Nachbildung als
gemeinsame Geräte ein. Der Datenweg ist derselbe wie bei echten Geräten (LAN-Socket, SCPI, Fehlerwarteschlange).
Verdrahtung der Simulation: DMM VAC/IAC/FREQ ← AC-Quelle (Last 54,8 Ω), DMM VDC/IDC ← Netzgerät CH1 (Last 12 Ω),
Oszilloskop CHAN1 ← AC-Quelle, CHAN2/CHAN3 ← Generator CH1/CH2, CHAN4 ← Netzgerät CH1; DAQ ai0/ai1 ← ao0/ao1.

## Verhalten

* **Fehler**: Nach jedem Einstellbefehl wird `SYST:ERR?` gelesen; abgelehnte Befehle (z. B. Bereich überschritten)
  ergeben ERROR im Schritt mit der Gerätemeldung. Werte außerhalb des Modellbereichs werden schon vor dem Senden
  abgelehnt – es gibt keine automatische Begrenzung oder Skalierung.
* **Abbruch**: Werkerabbruch bricht laufende Gerätezugriffe ab (Socket: neue Verbindung, VISA: Device Clear; DAQmx:
  Task stoppen). Eine verspätete Antwort wird nie dem nächsten Befehl zugeordnet.
* **Sicherer Zustand**: Nach jedem Lauf (auch Abbruch/Fehler) und beim Beenden der Anwendung werden alle Geräte des
  Prüfplatzes in den sicheren Zustand gebracht (Netzgerät/AC-Quelle/Generator: Ausgänge aus; DAQ: Kurvenausgabe
  gestoppt, AO 0 V, konfigurierte Digitalausgänge). Gemeinsame Geräte nur, wenn kein anderer Prüfplatz prüft. Nicht
  bestätigte Geräte erscheinen im Protokoll und als Fehlermeldung des Prüfplatzes.
* **Synchronisation**: Mehrkanal-Aufnahmen laufen mit einem Takt (DAQmx-Task, Oszilloskop-`:DIGitize`) und bekommen
  eine gemeinsame Synchronisationsgruppe. Gerätegrenzen (Abtastrate, Punktzahl) werden übernommen und in der Qualität
  vermerkt, nicht still umgerechnet. Zwischen Geräten: Starttrigger (DAQmx `Starttrigger`, Oszilloskop
  `Triggerquelle=EXT`), Generator-Kanäle phasengleich (`FUNC:ARB:SYNC`).
* **Sperren**: Reale Geräte werden als ganzes Gerät gesperrt (Funktionsumschaltung, Ausgänge); mit „Sperre je Kanal“
  einzeln je Kanal (z. B. unabhängige Netzgeräteausgänge).

## Kommunikation mit dem Prüfling

Adapterfamilie „Kommunikationsgeräte“ (Abschnitte 1 und 7). Zuordnung wie bei Messgeräten: abstrakter Name im Ablauf
(z. B. `Pruefling_Seriell`) → Gerät und Kanal in der Prüfplatzkonfiguration. Reservierung, Sperren und
Zugriffsprotokoll gelten unverändert; ein gemeinsamer RS-485-Bus wird als gemeinsames Gerät mit Sperre konfiguriert.

| Adapter | Ressource | Kanal | Treiber |
|---|---|---|---|
| Seriell (RS-232/RS-485) | `COM3` | frei (z. B. `Daten`) | Windows (Win32), auch USB-Seriell-Adapter |
| TCP-Client | `192.168.1.50:5000` | frei | – |
| UDP | `192.168.1.50:5000` | frei | – |
| Modbus RTU | `COM4` | Unit-ID `1` oder Punkt `1/HR100:float32` | Windows (Win32) |
| Modbus TCP | `192.168.1.60:502` | wie RTU | – |
| CAN – PEAK PCAN-Basic | `PCAN_USBBUS1` (…16, `PCAN_PCIBUS1`, `PCAN_LANBUS1`) | frei (`Bus`) | PEAK-Treiberpaket (PCANBasic.dll) |
| CAN – Kvaser CANlib | Kanalnummer `0` | frei | Kvaser Drivers for Windows (canlib32.dll) |
| CAN – Vector XL Driver Library | Anwendungskanal `Testumgebung:0` (aus „Vector Hardware Config“) oder `virtuell:0` | frei | Vector-Treiberpaket (vxlapi64.dll), CAN und CAN FD |
| CAN – virtueller Bus | beliebiger Busname | frei | – (im Programm, zum Ausprobieren) |
| LIN – PEAK PLIN-API | Hardware `1`, `2` … | frei (`Bus`) | PEAK-PLIN-Treiberpaket (PLinApi.dll), Master-Betrieb |
| LIN – virtueller Bus | beliebiger Name | frei | – (feste Slave-Antworten über Option `Antworten`) |

Optionen (eine Zeile je Option):

* **Seriell:** `Baud=115200`, `Datenbits=8`, `Parität=N|E|O|M|S`, `Stoppbits=1|1.5|2`, `Handshake=Kein|RTS-CTS|XON-XOFF`,
  `RS485=ja` (RTS-Richtungsumschaltung; bei Adaptern mit automatischer Umschaltung weglassen).
* **TCP/UDP:** `Verbindungszeit=3000` (ms), `Lokaler Port=5001` (UDP). TCP zusätzlich `TLS=ja`, `TLS-Servername=…`,
  `Firmen-CA=pfad.crt` (Selbst-/Firmenzertifikate), `TLS-Prüfung=aus` (Laborzertifikate ohne Kette; nur im Testaufbau).
* **Gerät testen:** `Prüfbefehl=*IDN?`, `Zeilenende=\r\n`; Modbus: `Prüfadresse=1/HR0`.
* **Modbus:** `Einheit=1` (Standard-Unit-ID), `Wiederholungen=1`, `Rahmenpause=5` (ms, RTU; Standard 3,5 Zeichen),
  `Wortfolge=niedrig` (32/64-Bit-Werte mit niederwertigem Register zuerst), `RTU über TCP=ja` (Gateway).
  Zeitlimit je Anfrage = Feld „Zeitlimit“ des Geräts.
* **CAN:** `Bitrate=500k` (10k…1M), `FD=ja`, `Datenbitrate=2M` (1M/2M/4M/5M/8M), `FD-Parameter=f_clock_mhz=80,…` (PCAN,
  eigene Zeitparameter), `Virtuell=ja` (Kvaser, virtuelle Kanäle). Vector: Bitrate; CAN FD über Vector ist angebunden
  (xlCanTransmitEx/xlCanReceive), aber nicht an Hardware erprobt.
* **LIN:** `Baud=19200` (1000–20000); virtueller Bus: `Antworten=0x21=01 02 03 04;0x22=@0x10`.
* **Sicherer Zustand** (nach jedem Lauf): Seriell/TCP Befehle getrennt durch `|` (z. B. `OUTP OFF\n`), Modbus
  `HR10=0;C1=0`, CAN Rahmen `0x100 00 00|0x101 00`, LIN Frames `0x10 00 00`.

Bausteine (Palette „Kommunikation“): **Senden**, **Empfangen**, **Abfrage** (alte Eingangsdaten verwerfen, senden,
Antwort bis Zeilenende/Länge/Ruhe, Antwortmuster mit Gruppe 1 als Wert, Grenzwerte), **Modbus lesen** (Datentypen
UInt16 … Float64, Bool, Text, Wortfolge, Faktor/Offset, Sollwert oder Grenzwerte), **Modbus schreiben** (optional
zurücklesen), **CAN senden** (Standard/29 Bit, FD, BRS), **CAN empfangen** (ID/Maske, optional Anfrage vorher,
erwartete Daten mit `**`, Signal mit Startbit/Länge/Intel-Motorola/Vorzeichen/Faktor/Offset). In Texten fügt
`{Ausdruck}` Werte ein (z. B. `SET:VOLT {Sollspannung}`, in Hexdaten ein Byte). Zeitüberschreitung ergibt ERROR
(wählbar FAIL), Modbus-Ausnahmen und Verbindungsfehler ERROR mit Klartext.

### Busdatenbanken, Diagnose, J1939, LIN

Projekt-Eigenschaften → **Busdatenbanken (DBC, LDF)** → „Importieren …“. Der Dateiinhalt wird im Projekt gespeichert
(damit auch im Release-Paket); ein erneuter Import mit gleichem Namen ersetzt ihn, Umbenennen zieht die Verweise in den
Bausteinen mit. Unterstützt werden aus DBC: Botschaften (11/29 Bit, CAN FD), Signale (Intel/Motorola, Vorzeichen,
Faktor/Offset, Bereich, Einheit), Multiplex (`M`/`m1`), Wertetabellen (`VAL_`), Gleitkommasignale (`SIG_VALTYPE_`),
Zykluszeit (`GenMsgCycleTime`), Startwerte (`GenSigStartValue`), Kommentare, `ProtocolType "J1939"`. Aus LDF: Bitrate,
Master/Slaves, Signale mit Startwert, Frames, Kodierung (`physical_value`, `logical_value`). Der Editor meldet fehlende
Datenbanken, Botschaften und Signale an den Schritten.

| Baustein | Zweck |
|---|---|
| **CAN-Signal lesen (DBC)** | Signal `Botschaft.Signal` frisch empfangen (alte Rahmen verworfen; Multiplex beachtet), Sollwert (Zahl oder Text der Wertetabelle) oder Grenzwerte; Einheit aus der Datenbank. Zeitlimit leer = 3 × Zykluszeit. J1939-Datenbanken: Empfang über die PGN (Priorität und Quelladresse egal). |
| **CAN-Botschaft senden (DBC)** | Botschaft aus `Signal = Ausdruck; …` (Wertetabellen-Text erlaubt), übrige Signale mit Startwert, Multiplexer automatisch; mehrfach im Zyklus. |
| **UDS-Diagnose** | Anfrage (Hex, `{Ausdruck}` = Byte) über einen von drei Transporten: **CAN (ISO-TP)** mit Segmentierung, Flusssteuerung, CAN FD und optionaler erweiterter/gemischter Adressierung (Adressbyte Anfrage/Antwort); **DoIP (Ethernet)** über ein TCP-Client-Gerät (Routing-Aktivierung, Quittung, Alive-Check); **LIN (Transportprotokoll)** mit NAD. P2/P2*, „Antwort folgt“ (NRC 0x78). Auswertung ab Byte als Hex, Text, Zahl (mit Vorzeichen); negative Antwort = FAIL mit Klartext, außer als „Erwartete negative Antwort“ angegeben. Positive Antwort ohne weitere Prüfung = PASS. |
| **J1939 empfangen** | PGN (optional Quelle), optional mit Anforderung (PGN 59904); BAM und RTS/CTS werden zusammengesetzt (CTS und Abschlussbestätigung an die eigene Adresse). Signal aus der Datenbank oder Startbit/Länge; „nicht verfügbar“ (alle Bits 1) = FAIL. |
| **J1939 senden** | PGN, Priorität, Quelle, Ziel; über 8 Bytes global als BAM (50 ms Abstand), an eine Adresse per RTS/CTS. |
| **J1939 Adresse beanspruchen** | Address Claimed (PGN 60928) mit 64-Bit-NAME, 250 ms auf Gegenansprüche prüfen; verliert die Station (kleinerer NAME der Gegenstelle), ist der Schritt FAIL. Optional vorher alle Teilnehmer abfragen. |
| **UDS-Sicherheitszugang** | Seed anfordern (0x27, ungerade Stufe), Schlüssel berechnen (XOR/Addition/Invertieren/Add-Rotate-XOR oder Ausdruck je Byte) und senden; Seed aus Nullen = bereits frei; falscher Schlüssel = FAIL. Herstellerspezifisch sonst als CodeBlock. |
| **DoIP Fahrzeugsuche** | Vehicle Identification Request (UDP 13400) senden und antwortende Steuergeräte/Gateways (VIN, Adresse) sammeln; optional auf VIN/Adresse/Mindestanzahl prüfen. |
| **LIN lesen** / **LIN senden** | Als Master Header senden und Slave-Antwort prüfen bzw. Frame mit Daten senden; Frame per ID oder Name aus der LDF, Signale wie bei CAN, Prüfsumme erweitert (LIN 2.x) oder klassisch; Diagnoseframes 0x3C/0x3D immer klassisch. |
| **LIN-Fahrplan** | Fahrplantabelle abarbeiten: je Zeile ein Frame (`ID [:Länge] [Abstand]` empfangen bzw. `ID=DATEN [Abstand]` senden), mehrere Durchläufe; fehlende Antworten FAIL oder nur Hinweis. |

Sicherheitszugang (Seed/Key) und andere herstellerspezifische Abläufe laufen als CodeBlock: `uds(…)`, `can_send(…)` usw.
(siehe Entwicklerhandbuch) – über dieselbe Vermittlung mit Reservierung und Zugriffsprotokoll. Der CodeBlock-`uds` kennt
ebenfalls `transport="doip"` bzw. erweiterte Adressierung (`tx_address`, `rx_address`).

Simulation: Projekt-Eigenschaften → „Kommunikation: Simulation des Prüflings“ – Antwortregeln je Schnittstelle
(Byte-Strom `MUSTER => ANTWORT`, Modbus-Registerabbild, CAN-Antworten – auch mehrere Rahmen mit `;` – und zyklische
Rahmen, die sich bei gleicher ID abwechseln; UDS-Steuergerät mit `uds 0x7E0 0x7E8` und `uds: 22 F1 90 => 62 F1 90 "TEXT"`,
Segmentierung automatisch, erweiterte Adressierung mit `uds 0x7E0 0x7E8 tx=0x10 rx=0xF1`; DoIP-Steuergerät mit
`doip 0x1000` und `uds:`-Regeln; LIN `0x21 => 01 02 03 04`, `0x22 => @0x10`). Antworten kommen sofort;
keine passende Regel = der Prüfling schweigt. Zum Prüfen der echten Treiber ohne Prüfling: `CAN – virtueller Bus`
(zwei Geräte am selben Bus) sowie die Gegenstellen `ModbusTcpSimulatorServer`, `ModbusRtuResponder` und `LineServer`
(Tests). Die Treiber für COM-Ports, PCAN, Kvaser, Vector XL und PLIN sind ohne Hardware nur übersetzt, nicht am Gerät
erprobt. Beispieldateien: `docs/beispiele/antrieb.dbc`, `docs/beispiele/fensterheber.ldf`.
