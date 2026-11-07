# Lizenzvorprüfung: LabJack und RedLab/MCC

Stand: 06.11.2026. Gegenstand ist die neue eigene C#-Anbindung der Hersteller-APIs, nicht eine Freigabe unbekannter zukünftiger Installationspakete.

| Komponente | Verwendung | Mitgeliefert | Lizenzbewertung |
|---|---|---|---|
| LabJack LJM / `LabJackM.dll` | T4/T7/T8, `Daq/LjmIoSession.cs` | Nein | Hersteller nennt MIT mit zusätzlichen Boost-/gRPC-Lizenzen unter Windows. Paket kann weitere Bedingungen enthalten. |
| LabJack UD / `LabJackUD.dll` | U3/U6/UE9, `Daq/UdIoSession.cs` | Nein | Allgemeine LabJack-MIT-Regel, sofern das konkrete Paket nichts anderes bestimmt. |
| LabJack U12 / `ljackuw.dll` | U12, `Daq/U12IoSession.cs` | Nein | Dieselbe allgemeine Regel; konkrete Windows-Laufzeit separat installieren. |
| MCC Universal Library / `cbw64.dll` und Hardwaretreiber | RedLab/MCC, `Daq/NativeUlApi.cs` | Nein | Notwendige Laufzeit-/Hardwaretreiber dürfen laut Hersteller gebührenfrei weitergegeben werden; Entwicklungsdateien ausdrücklich nicht. |

Die eigenen Deklarationen verwenden Funktionssignaturen, Konstanten und Gerätefakten zur Interoperabilität. Es werden keine Herstellerquelltexte, SDK-Archive, Header oder Binärdateien ins Repository beziehungsweise MSIX übernommen. Für diese Integration sind keine zusätzlichen NuGet-Pakete nötig. Die eigene Software bleibt unter MIT; daraus folgt keine MIT-Lizenz für proprietäre Treiber.

## Entscheidung für diese Änderung

Die Laufzeiten werden **extern durch den Anwender installiert**, genau wie andere vorhandene Gerätetreiber. Das Lizenzinventar führt sie als optionale externe Abhängigkeiten, deren Version nicht festgelegt und deren Binärdatei nicht verteilt wird. Daher gibt es für diese Dateien keine erfundene Versions-/Hash-Bestätigung. Die Anleitung und dieser Nachweis werden mit der Dokumentation ausgeliefert. Das Lizenzverzeichnis enthält einen Hinweis auf die separat zu installierenden Laufzeiten.

## Falls später gebündelt werden soll

Zuerst das konkrete Herstellerpaket einschließlich Version, Hash und aller Lizenztexte prüfen. Bei LJM die mitgelieferten Fremdlizenzen und Copyright-Hinweise erhalten; USB-Treiber und benötigte Konfigurationsdateien berücksichtigen. Bei UL nur erlaubte Laufzeit-/Treiberdateien gemäß **ULRedistribution.pdf** aus der verwendeten Installation übernehmen, keine Entwicklungsdateien. InstaCal muss mitgegeben werden, wenn es für die Konfiguration benötigt wird. Ein beliebiger SDK-Download darf nicht pauschal in die öffentliche Distribution kopiert werden.

## Primärquellen

- [LabJack: Softwarelizenz und LJM-Zusatzlizenzen](https://support.labjack.com/docs/what-is-labjack-software-licensed-under)
- [LabJack: Beispiel für eigenen Windows-Installer](https://github.com/labjack/windows_example_installer)
- [MCC/Digilent: Redistributing a Custom UL Application](https://files.digilent.com/manuals/Mcculw_WebHelp/Users_Guide/Overview/Redistribute.htm)

Die Vorprüfung betrifft die benannten Integrationswege und ersetzt keine Prüfung eines später geänderten Herstellerpakets.
