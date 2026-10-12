# ADR 0016 – Run-Patch Teil 2 (Bibliotheksbausteine/Struktur) und OPC UA (Lesen/Schreiben/Methoden/Abos)

Status: angenommen (Oktober 2026). Bezug: offene Punkte aus ADR 0015 (D06 zweiter Teil, OPC-UA-Methoden/Abonnements).

## Entscheidung

* **Run-Patch, zweiter Teil (D06)** – Erweiterung des Patches im pausierten Lauf (`RunPatcher`) über Parameter/Name/Bedingung
  hinaus:
  * **Bibliotheksbausteine** (Code und grafisch): `code` patcht den Quelltext eines Bibliotheks-CodeBlocks, `name` seine
    Bezeichnung. Die Änderung wirkt auf alle **folgenden** Aufrufe im selben Lauf – auch wenn der Baustein bereits einmal lief.
  * **Ablaufstruktur nicht aktiver Gruppen**: `delete` entfernt einen noch nicht ausgeführten Schritt/eine Gruppe, `move`
    verschiebt ein Element innerhalb seiner Elterngruppe (Wert = Versatz, z. B. `-1`). Nur zulässig, solange die Elterngruppe
    nicht gerade aktiv ist; aktive Scopes bleiben unverändert. Einfügeindex wird auf die Gruppengröße geklemmt.
  * Jeder Patch wird weiterhin validiert, nummeriert und als `DevelopmentEvent` protokolliert; `NameOf` löst auch
    Bibliotheks-/Gruppenbaustein-Ids für die Protokollzeile auf.
* **OPC UA als Gerät und Baustein** – über den Meldeweg (Schreiben) hinaus jetzt vollwertiger Feldzugriff:
  * Contract `IOpcUaClient` (Connect/Read/Write/Call/Subscribe), `IOpcUaSubscription`, `IOpcUaDevice`, `OpcUaException`.
    Werte sind `null`/`bool`/`long`/`double`/`string`; NodeIds im Format `ns=2;s=Name` bzw. `ns=2;i=42`.
  * Baustein **„OPC UA“** (`opcua.zugriff`): Aktionen Lesen, Schreiben, Methode aufrufen, Abonnieren (auf Änderung warten);
    gelesene/zurückgegebene Werte prüfbar gegen Sollwert oder Grenzwerte, Ziel­variable, Einheit.
  * CodeBlocks: `opcua_read`/`opcua_write`/`opcua_call` (Python) bzw. die Geräteoperationen `opcua.read|write|call`
    über die zentrale Vermittlung (`CodeDevices`), Präfix `opcua.` in `CodeRuntime.IsDeviceOperation`.
  * **Simulation/Tests**: `SimulatedOpcUaServer` (in-Prozess) mit Regeln je Zeile – `ns=2;s=Name = Wert` für Variablenknoten
    (am letzten `=` getrennt, da NodeIds selbst `=` enthalten) und `methode OBJ METHOD => summe|spiegeln|konstante:V` für
    Methoden; Abonnements melden Erstwert und jede serverseitige Änderung. Angebunden über `CommKind.OpcUa`.
  * **Realer Client**: `Testumgebung.Desktop.OpcUaClient` (OPC-Foundation-Stack) wird in `App` in `OpcUaDevice.ClientFactory`
    eingehängt – dieselbe Sitzungs-/PKI-Logik wie der OPC-UA-Meldeweg (%LOCALAPPDATA%\Testumgebung\pki, unbekannte
    Serverzertifikate werden angenommen; anonym oder Benutzer/Passwort; Option „Sicherheit“ = Sign & Encrypt). Nur in der
    Windows-Anwendung verfügbar; Engine/Instruments bleiben über die Factory stack-frei und damit testbar.

## Nicht enthalten / weiterhin offen

* **LIN-Slave-Betrieb** am realen Interface und **Quellcode-Debugger für Fremdsprachen** bleiben offen (siehe ADR 0015):
  hardware-/stackgebunden bzw. sehr umfangreich und headless kaum prüfbar.
* Erprobung des realen OPC-UA-Clients an einem echten Server (wie alle treibergebundenen Teile) steht noch aus.

## Folgen

* Der Run-Patch deckt nun Parameter, Namen, Bedingungen, Bibliotheksbausteine (Code/grafisch) und die Struktur nicht aktiver
  Gruppen ab – jeweils nur für den laufenden, pausierten Lauf (eigene Definitionskopie).
* OPC UA ist – wie die übrigen Kommunikationswege – aus Bausteinen und aus CodeBlocks nutzbar und gegen die Simulation
  vollständig getestet (Lesen/Schreiben/Methode/Abo, Fehlerfälle); der reale Client ist stack-gebunden und per Factory getrennt.
