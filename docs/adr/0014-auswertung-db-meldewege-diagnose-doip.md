# ADR 0014 – Auswertung aus der Datenbank, Meldewege HTTP/OPC UA, Diagnoseausbau (DoIP, LIN, J1939-Adresse)

Status: angenommen (Oktober 2026). Bezug: offene Punkte aus ADR 0004, 0005 und 0013.

## Entscheidung

### Auswertung und Wiederherstellung (ADR 0004/0005)

* **Lesen aus der Datenbank:** `IRunQuerySource.QueryRunsAsync(RunQuery, …)` liefert Läufe mit Struktur, Schritten,
  Messwerten und Werkereingaben – ohne Kurven (diese nur je Prüfling über die Ergebnisdatei). SQL-Server lesen mit drei
  Abfragen (Läufe; Schritte mit Messwerten per LEFT JOIN; Werkereingaben), unabhängig von der Anzahl der Läufe; Zeitraum,
  Test, Prüfplatz, Kennung und „ohne Entwicklungsläufe“ filtert der Server. `FolderRunDatabase` (simuliert) und
  `SwitchableRunDatabase` erfüllen denselben Vertrag. Die Auswertungsseite bietet die Datenquelle „Ergebnisordner“ oder
  „Datenbank“.
* **Wiederherstellungsordner zurückkopieren:** `ResultFileStore.RestoreRecovered()` kopiert Dateien, die mangels Zugriff
  auf den Ergebnisordner im Wiederherstellungsordner landeten, atomar zurück und löscht sie dort erst nach Erfolg;
  beschädigte Dateien bleiben liegen und werden gemeldet. Die Ergebnisseite zeigt einen Hinweis mit Knopf.

### Meldewege (ADR 0005)

* **HTTP-Webhook** (`HttpNotifier`): je Meldung ein POST mit derselben JSON-Nachricht wie MQTT, Platzhalter in der URL,
  optionaler Kopf (z. B. `Authorization`/`X-Api-Key`, per DPAPI geschützt), Erfolg = Status 2xx.
* **OPC UA** (`OpcUaNotifier`, nur in der Windows-Anwendung über den OPC-Foundation-Stack): schreibt RunId, DutId, Event,
  Time, Attempt, Error und Station als Zeichenkette in Knoten `{Präfix}{Feld}` eines Servers; ohne Sicherheit oder
  Sign & Encrypt mit selbst erzeugtem Anwendungszertifikat. Die Umsetzung wird über `ResultServices.OpcUaFactory`
  eingehängt, damit Contracts/Presentation ohne OPC-Abhängigkeit bleiben.
* Beide sind strikt best effort wie MQTT (`QueuedNotifier`: ein begrenzter Versuch je Meldung, keine Wiederholung, kein
  persistenter Puffer). Mehrere Wege werden über `CompositeNotifier` bedient; ein Fehler eines Weges beeinflusst die
  anderen nicht.

### Diagnoseausbau (ADR 0013)

* **Diagnosetransport abstrahiert:** `IDiagnosticTransport` (senden, empfangen, verwerfen). UDS läuft darüber, der
  Baustein „UDS-Diagnose“ und die CodeBlock-Operation `uds` wählen den Transport.
  * `IsoTpTransport` (CAN) – jetzt mit erweiterter/gemischter Adressierung (Adressbyte in Anfrage und Antwort) und
    korrekter Einzelrahmengröße bei CAN FD.
  * `DoIpTransport` (Ethernet, Gerät „TCP-Client“, Port 13400): generischer Kopf, Routing-Aktivierung, Diagnosenachricht
    0x8001 mit Quittung 0x8002/0x8003, Alive-Check wird beantwortet.
  * `LinDiagnosticTransport` (LIN-TP): Master-Request 0x3C, Slave-Response 0x3D, NAD und PCI, klassische Prüfsumme.
* **J1939-Adressvergabe** (`J1939.ClaimAddressAsync`, `RequestClaimsAsync`): Address Claimed (PGN 60928) mit 64-Bit-NAME,
  250 ms auf Gegenansprüche warten; bei kleinerem NAME der Gegenstelle „Cannot Claim“ (Quelle 0xFE) und FAIL. Baustein
  „J1939 Adresse beanspruchen“.
* **LIN-Fahrplan** (Baustein „LIN-Fahrplan“): arbeitet eine Fahrplantabelle ab (Frames empfangen/senden mit Abstand,
  mehrere Durchläufe); fehlende Antworten sind FAIL oder nur Hinweis.
* **Simulation:** DoIP-Steuergerät als Byte-Strom (`CommKind.DoIp`), erweiterte Adressierung im CAN-Steuergerät
  (`uds … tx= rx=`).

## Nicht enthalten

* DoIP-Fahrzeugankündigung (UDP 13400, Vehicle Identification) und TLS für DoIP; die Verbindung nutzt das TCP-Client-Gerät
  (TLS dort nach Bedarf).
* OPC-UA-Methodenaufrufe und -Abonnements (nur Schreiben in Variablenknoten); Auswertung über OPC UA.
* Kurven in der Datenbankauswertung.
* Erprobung an echter Hardware (Vector, PLIN, reale Steuergeräte, DoIP-Gateways).

## Folgen

* `IDiagnosticTransport` entkoppelt UDS von CAN; neue Transporte (z. B. K-Line) lassen sich ergänzen, ohne UDS zu ändern.
* Der OPC-UA-Stack ist nur im Desktop-Projekt referenziert; in Umgebungen ohne diese Pakete fehlt der OPC-UA-Meldeweg,
  die Einstellung zeigt das an.
