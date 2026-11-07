# LabJack und RedLab/MCC: DIO und AIO

## Umfang und Anschlussauswahl

Die Geräte werden in **Prüfplatzkonfiguration → Geräte** angelegt. Als Adapter stehen **LabJack DIO / AIO**, **RedLab / MCC Universal Library** und **DAQ DIO / AIO (Nachbildung ohne Treiber)** zur Verfügung. Nach der Modellwahl zeigt **Verbindung** nur die zum Modell gehörenden Anschlüsse. Ein Modellwechsel entfernt eine unzulässige Auswahl. Auch die Ausführung prüft die gespeicherte Auswahl.

| Familie / Modelle | Verbindung | Herstellerschnittstelle |
|---|---|---|
| T4, T7, T8; aufgeführte OEM-Varianten | USB, Ethernet | LJM |
| T7-Pro, T7-Pro-OEM | USB, Ethernet, WLAN | LJM |
| U3-LV/HV, U6/Pro; OEM | USB | UD |
| UE9/Pro; OEM | USB, Ethernet | UD |
| U12 | USB | U12 Windows Library |
| RedLab 1008, 1024LS/HLS, 201/202/204/205, 1208LS/FS/FS-Plus, 1408FS/FS-Plus, 1608FS/FS-Plus/G/GX/GX-2AO, 1808/X, 2408/2AO, 2416/4AO, 3101–3106, TC, TEMP, TC-AI, TEMP-AI, ERB08/24, SSR08/24, PDISO8 | USB | Universal Library |
| RedLab E-1608, E-DIO24, E-TC | Ethernet | Universal Library |
| RedLab/MCC TC-32 | USB, Ethernet | Universal Library |
| UL-Gerät automatisch (USB/Ethernet) | jeweils der gewählte Anschluss | Laufzeit-Erkennung weiterer UL-Produkte |

Die Liste ist keine Behauptung, dass jedes Gerät physisch geprüft wurde. Die Anbindung deckt die genannten Treiberfamilien für skalare analoge/digitale Ein- und Ausgänge ab. Weitere von UL erkannte USB-/Ethernet-Produkte können ohne festes Modellprofil verwendet werden; tatsächlich verfügbare Kanäle und Bereiche werden abgefragt. Eigenständige WebDAQ-Logger, Bluetooth-/PCI-Geräte und gerätespezifische Stream-, Zähler-, PWM- oder Kalibrierfunktionen sind dadurch nicht automatisch implementiert. Nicht unterstützte Funktionen liefern einen Fehler statt erfundener Messwerte.

## Installation und eindeutige Geräteauswahl

Die Windows-x64-Laufzeit und der USB-Treiber des Herstellers müssen extern installiert sein. Das MSIX enthält **keine** LabJack- oder UL-Treiberdateien und kein Hersteller-SDK. Fehlende oder inkompatible Laufzeiten werden beim ersten Gerätezugriff verständlich gemeldet; der übrige Programmstart bleibt möglich. Lizenzprüfung: [LabJack/RedLab](licensing/labjack-redlab.md).

1. Passenden Hersteller-Treiber installieren: LJM für T-Serie, UD für U3/U6/UE9, U12-Laufzeit oder Universal Library/InstaCal für RedLab/MCC.
2. Adapter, Modell und Verbindung wählen.
3. Als Ressource eine **konkrete Seriennummer/Geräte-ID** eintragen; bei Ethernet die vom Treiber erkannte Kennung oder Geräteadresse. `ANY` und leere Angaben werden abgelehnt, damit kein beliebiges Gerät am Prüfplatz gewählt wird. U12 benötigt eine numerische lokale ID/Seriennummer.
4. Optionen setzen, **Gerät testen** wählen, Kanalnamen prüfen, anschließend die abstrakten Projektressourcen diesen Kanälen zuordnen.
5. Bei realer Hardware Signalpegel und sichere Ausgänge prüfen, bevor ein Prüfprogramm gestartet wird.

UL sucht die exakte Kennung im Inventar des gewählten Anschlusses. Bei Ethernet kann anschließend eine Adresse direkt abgefragt werden. Bereits durch InstaCal belegte Boardnummern werden nicht überschrieben.

## Kanäle und vorhandene Ablaufpunkte

| Kanal | Bedeutung | Ablaufpunkt |
|---|---|---|
| LabJack `AIN0`, … | Spannung lesen | Analog messen / Messverlauf aufnehmen |
| LabJack `DAC0`, `DAC1` | Spannung setzen | Analog ausgeben |
| LabJack `DIO0`, … | digitalen Zustand lesen/setzen | Digital lesen / Digital setzen |
| UL `AI0`, … / `AO0`, … | Spannung lesen / setzen | dieselben Analogpunkte |
| UL `TI0`, … | Temperatur in °C | Analog messen mit passender Einheit |
| UL `DIO0`, … | fortlaufende Bits über die erkannten Ports | dieselben Digitalpunkte |

Es sind keine zusätzlichen doppelten Ablaufpunkte erforderlich. Ein Projekt bindet abstrakte Ressourcen an diese Kanäle. Kurvenaufnahmen verwenden softwaregesteuerte Einzelabfragen; es wird keine gemeinsame Hardware-Zeitbasis zugesagt.

T8 hat Eingänge bis ±11 V und DAC-Ausgänge ungefähr −0,1 bis 10 V. T4 hat DIO4–19; beim U3-HV sind AIN0–3 keine Digitalleitungen. Flexible Leitungen wechseln beim Lesen/Setzen ihre Funktion. Dieselbe Leitung daher nicht gleichzeitig als Analog- und Digitalressource verwenden. Ein U12-Analogaufruf setzt beide DACs: Vor dem ersten Schreiben sind die bekannten Startwerte des anderen DAC über `AO-Paar-Startwerte` festzulegen. Es wird kein unbekannter Ausgang still auf null gesetzt.

## Optionen

Jede Option wird als eigene Zeile `Schlüssel=Wert` eingetragen. `Verbindung` wird ausschließlich über das Auswahlfeld bedient.

| Option | Bedeutung |
|---|---|
| `Sichere Ausgänge=DAC0=0;DIO4=0` | Nur ausdrücklich genannte Ausgänge werden beim sicheren Zustand verändert. Bei einem Fehler werden die übrigen weiterhin versucht und Fehler gemeinsam gemeldet. |
| `Bereich=1` | T7: gewünschter Eingangsbereich bis 10 V, T8 bis 11 V (zum Beispiel 9.6); U6: 10, 1, 0.1 oder 0.01; UE9: 5. T4/U3 haben hier feste Bereiche. |
| `AO-Paar-Startwerte=DAC0=0;DAC1=0` | U12: bekannte Ausgangswerte vor dem ersten gepaarten Schreibzugriff; die Konfiguration allein schreibt nichts. |
| `AI-Bereich=1` / `AO-Bereich=100` | ULRange-Zahl gemäß Gerätehandbuch: beispielsweise 1 = ±10 V, 100 = 0–10 V. Für programmierbare/unbekannte Bereiche ausdrücklich wählen. Der Hersteller prüft die Unterstützung des Bereichs. |
| `Port-Richtungen=PORT12=Ausgang;PORT1=Eingang` | UL-Porttyp gemäß Geräteprüfung. Gemeinsam umschaltbare Ports werden niemals still wegen eines einzelnen Bits umgeschaltet. Die Richtung wird erst beim Zugriff gesetzt. |
| `Kanalarten=AI0=Temperatur;AI4=Spannung` | UL-Multifunktionsgerät: Kanaltyp zur vorherigen Gerätekonfiguration passend wählen. Konfiguriert keinen Sensortyp und ersetzt keine Thermoelement-/RTD-Konfiguration in InstaCal. |
| `Port=54211` | UL-Ethernet-Erkennung: Port, ganze Zahl 1–65535. |
| `Erkennungszeit [ms]=5000` | UL-Ethernet-Erkennung: 1–120000 ms. |

Bei TEMP/TC-Modellen werden Temperatureingänge verwendet; bei TEMP-AI/TC-AI sind die ersten vier Temperatur-, die weiteren Spannungskanäle. Über `Kanalarten` kann die tatsächlich konfigurierte Betriebsart ausgewählt werden. Temperatureinheiten und Sensoreinstellung müssen zur Hardware passen. Analoge Grenzwerte im Ablauf bleiben davon unabhängig.

**Gerät testen** liest Identität und Fähigkeiten, setzt keine Ausgänge und führt keine automatische Ausgangsprobe aus. Digitale Ausgangsrücklesung schaltet einen Port nicht auf Eingang. Zugriffe auf dieselbe Adapter-/Gerätekennung werden serialisiert; ein physisches Gerät deshalb nicht mehrfach über verschiedene Kennungen/Schnittstellen konfigurieren. Für zwei Prüfplätze dasselbe gemeinsame Gerät verwenden und vorhandene Ressourcensperren nutzen.

Ein laufender synchroner Herstelleraufruf lässt sich nicht verlässlich unterbrechen. Ein Werkerabbruch wird vor/nach dem Zugriff berücksichtigt; die native Treiberwartezeit bleibt maßgeblich. Keine pauschale Kommunikationsfrist wird angeboten.

## Beispiel ohne Hardware

Adapter **DAQ DIO / AIO (Nachbildung ohne Treiber)**, Modell **T7**, Ressource `DAQ-DEMO`, Optionen:

```text
Rueckkopplung=AIN0=DAC0
Sichere Ausgänge=DAC0=0;DIO4=0
```

`DAC0` auf 2,5 V setzen, `AIN0` messen: 2,5 V. `DIO4` auf 1 setzen und wieder lesen: 1. Anschließend beide Ausgänge auf 0 setzen. Ein vollständiges Beispiel liegt unter [DAQ-Demo](beispiele/README-daq-demo.md).

Für die UL-Nachbildung zusätzlich `AI-Kanaele=2`, `AO-Kanaele=1`, `DIO-Kanaele=8` angeben; ohne explizite Zahlen werden keine realen Fähigkeiten erfunden. Mit `Eingaenge=AIN0=1.2;DIO4=1` sind feste Eingänge und mit `Rueckkopplung=AIN0=DAC0` gleichartige Rückkopplungen möglich.

## Nachweise und Grenzen der Prüfung

Automatisierte Tests prüfen Modell-/Anschlussmatrix, echte WPF-Auswahl, Geräteidentifikation, API-Aufrufe mit nachgebildeten Herstellerschnittstellen, Wertebereiche, Port-/Bitzuordnung, U12-DAC-Paar und Fehler-/Abbruchbehandlung. Es wurde kein physisches LabJack/RedLab am Entwicklungsrechner geprüft. Vor Produktion ist ein Abnahmetest mit dem konkreten Modell, dessen Treiberversion und Verdrahtung erforderlich.

Herstellerreferenzen: [Treiberfamilien](https://support.labjack.com/docs/what-driver-library-should-i-use-with-my-labjack), [T-Serie](https://support.labjack.com/docs/4-0-hardware-overview-t-series-datasheet), [U12-Analogausgang](https://support.labjack.com/docs/4-2-eanalogout-u12-datasheet), [Universal Library](https://digilent.com/shop/universal-library/).

T8-Bereichsreferenzen: [Analogeingänge](https://support.labjack.com/docs/14-3-2-analog-inputs-t8-t-series-datasheet), [DAC](https://support.labjack.com/docs/15-0-dac-t-series-datasheet).

TC-32: [Herstellerdaten mit USB/Ethernet](https://digilent.com/shop/mcc-tc-32-high-precision-thermocouple-measurement-usb-ethernet-system/). RedLab-Modellfamilien: [Meilhaus-Kompatibilitätsliste](https://www.meilhaus.de/daqami.htm).
