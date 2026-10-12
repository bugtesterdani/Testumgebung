# Chroma 61602 und ET Instrumente EAQ-1000

Stand: 12.10.2026 · ab Software 1.0.10.0

Unter **Prüfplatzkonfiguration → Geräte** stehen **Chroma 61602 AC-Quelle** und **ET Instrumente EAQ-1000 AC-Quelle** zur Verfügung. Die Ressourcenzuordnung macht ihre Kanäle für Lesen/Schreiben-Bausteine und `Read`/`Write` in CodeBlocks verfügbar. Für beide Prüfseiten gemeinsam verwendete Geräte als **gemeinsam** kennzeichnen, **Sperre erforderlich** aktivieren und die Ressource an der Prüfgruppe reservieren.

## Verbindung

Für das vorhandene ET-Gerät über RS-232 beispielsweise **COM4** eintragen. Ein USB-RS232-Adapter wird über seinen Windows-COM-Port genauso verwendet. Direkte COM-Verbindungen verwenden den vorhandenen Windows-Serielltreiber, ohne VISA. Die Optionen müssen zu den Schnittstelleneinstellungen des Geräts passen:

```text
Baud=9600
Datenbits=8
Parität=N
Stoppbits=1
Handshake=Kein
Zeilenende=LF
```

Diese Werte sind konfigurierbare Ausgangswerte, kein Nachweis der Einstellungen eurer Geräte. Unterstützte Zeilenenden sind LF, CRLF und CR. Das Gerätestimeout begrenzt die Kommunikation. Abbruch, leere Antwort und Kommunikationsfehler werden gemeldet; eine beschädigte Antworttransaktion wird vor dem nächsten Zugriff zurückgesetzt.

Chroma kann außerdem über eine vorhandene VISA-Ressource für GPIB, USB oder LAN angebunden werden, soweit das Gerät die entsprechende Schnittstelle besitzt. Hierfür wird eine passende externe VISA-Laufzeit benötigt. Ein tatsächlich vorhandener roher SCPI-TCP-Socket verwendet `TCPIP::<Host>::<Port>::SOCKET` ohne VISA. Anschluss und Port müssen dem Gerät entsprechen.

## Chroma 61602

| Kanal | Schreiben | Lesen |
| --- | --- | --- |
| VOLT | AC-Spannung | RMS-Spannung einschließlich DC-Anteil |
| FREQ | Frequenz | Gemessene Frequenz |
| CURR | RMS-Stromgrenze | RMS-Strom |
| POW | — | Wirkleistung |
| OUT | 0 aus / 1 ein, mit Ausgangsrelais | Ausgangszustand |

Option **Bereich=HIGH** ist der Standard; **LOW** ist ebenfalls möglich. Der erste schreibende Zugriff schaltet Ausgang und Relais aus und stellt den gewählten Bereich sowie AC-Betrieb ein. Danach wird der angeforderte Wert geschrieben. Reine Messabfragen und **Gerät testen** initialisieren oder schalten den Ausgang nicht. OUT=1 schaltet Relais und Ausgang ein; OUT=0 und sicherer Zustand schalten beide aus. Auch bei einem Fehler des ersten Abschaltbefehls wird der zweite versucht.

Grenzen werden entsprechend dem Bereich geprüft; der Treiber begrenzt Werte nicht stillschweigend. Ein Einschaltwert außer 0/1 und nicht endliche Zahlen werden abgelehnt. Chroma-Fehlermeldungen nach Schreibbefehlen werden ausgewertet, einschließlich des textuellen leeren Fehlerpuffers.

## ET Instrumente EAQ-1000 / EAQ-M-1000

| Kanal | Standardfunktion |
| --- | --- |
| VOLT | AC-Sollspannung |
| CURR | Strombegrenzung |
| FREQ | Frequenz |
| DC | DC-Sollspannung |
| PHAS | Phasenwinkel |
| OUT | Standby und Ausgangsrelais ein / aus |

Der Standardumfang entspricht den veröffentlichten ET-Einstellbefehlen. OUT=1 hebt Standby auf und schaltet das Ausgangsrelais ein; OUT=0 öffnet das Relais und aktiviert Standby. Der zweite Abschaltbefehl wird auch bei einem Fehler des ersten versucht.

Die öffentliche ET-Unterlage spezifiziert keine vollständige Mess- oder Identifikationsabfrage und keine Fehlerwarteschlange. Deshalb werden keine Messwerte vorgetäuscht und keine Chroma-Abfragen an ET gesendet. Standardmäßig sind die ET-Kanäle **nur schreibbar**. Mit einem zum Gerätehandbuch passenden **AbfrageVOLT**, **AbfrageCURR**, **AbfrageFREQ**, **AbfrageDC**, **AbfragePHAS** oder **AbfrageOUT** wird der jeweilige Kanal zusätzlich lesbar. Eine Abfrage muss einen numerischen Wert zurückgeben. Angezeigte Messwerte werden ausschließlich aus dieser Geräteantwort gebildet, niemals aus dem zuletzt gesetzten Sollwert.

Für **Gerät testen** einen dokumentierten **Pruefbefehl** konfigurieren. Ohne diesen meldet die Prüfung „Identität nicht geprüft“. Die Software unterstellt nicht, dass dieses Modell `*IDN?` oder `SYST:ERR?` unterstützt. Ein reiner Einstellablauf benötigt keinen Prüfbefehl. Fehlerantworten bei ET-Einstellbefehlen werden mangels dokumentiertem Abfragevertrag nicht automatisch ausgewertet; Transportfehler und Zeitlimits werden weiterhin gemeldet.

## Verwendung im Prüfprogramm

Beispielsweise die abstrakte Ressource **Netzspannung** dem Gerätekanal **VOLT** und **NetzAusgang** dem Kanal **OUT** zuordnen. Eine Gruppe reserviert beide Namen, wenn das Netzteil gemeinsam verwendet wird. Im Ablauf Spannung setzen, Frequenz über ihren zugeordneten Kanal setzen, Ausgang einschalten und anschließend prüfen. Im End-Abschnitt den Ausgang ausschalten. Die beiden Namen werden auf dasselbe Gerät abgebildet und teilen dessen Sperre.

Nachgewiesen sind die Kommunikationsverträge mit Testtransporten, Bereichsprüfung, Terminierung, Fehlerbehandlung und Abschaltreihenfolgen. Eine Prüfung mit euren realen Netzteilen steht noch aus. Das Gerätehandbuch der konkreten ET-Ausführung wird benötigt, um zusätzliche Messabfragen und die Identifikationsprüfung abschließend vorzubelegen.

Quellen: [Chroma-Benutzerhandbuch 61601–61604, Kapitel 7](https://assets.testequity.com/te1/Documents/pdf/chroma/Chroma_61601-4_Programmable-AC-Source_User-Manual_1024.pdf), [ET Instrumente EAQ-M, Schnittstellenbeschreibung](https://et-instrumente.de/PDF-Files/KAT-Instrumente-SEITE8-13-EAQ.PDF).
