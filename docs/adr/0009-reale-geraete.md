# ADR 0009 – Reale Geräte: SCPI (Keysight/Agilent) und NI-DAQmx

Stand: 03.10.2026 · Status: angenommen für Etappe 10 (Treiberprototypen ohne verfügbare Hardware)

## Ausgangslage

Es steht keine reale Hardware zur Verfügung. Vorgabe: NI-DAQ-Module sowie Agilent/Keysight-Multimeter, -Quelle,
-Generator und -Oszilloskop einbinden. Ausgewählt wurden aktuelle, programmierbare Familien mit dokumentiertem SCPI bzw.
NI-DAQmx-C-API (Geräteliste und Einrichtung: `docs/geraete.md`).

## Entscheidungen

* **Eigenes Projekt `Testumgebung.Instruments`** ohne NuGet-Abhängigkeiten: SCPI-Transport, -Sitzung, Treiber,
  DAQmx-Anbindung, Simulatoren. Die Treiber erfüllen die bestehenden Verträge (`IHardwareAdapter`, `IWaveformAdapter`,
  `IWaveformOutputAdapter`) und neu `ISafeStateAdapter` – Engine, Bausteine, Sperren und Ergebnisse bleiben unverändert.
* **Transport**: LAN-Socket (Port 5025) in reinem .NET; alles andere über VISA per P/Invoke auf `visa64.dll`
  (Keysight IO Libraries oder NI-VISA – herstellerneutral, zur Laufzeit geladen). Lesen in Zeitscheiben (250 ms), damit
  ein Abbruch auch bei blockierendem `viRead` schnell wirkt.
* **SCPI-Sitzung**: eine Transaktion zur Zeit; nach Einstellbefehlen `SYST:ERR?` bis `+0`; IEEE-488.2-Blöcke für
  Binärdaten (Arbiträrkurven float32, Oszilloskop WORD); nach Abbruch/Zeitlimit Device Clear bzw. neue Verbindung, damit
  keine verspätete Antwort dem nächsten Befehl zugeordnet wird; Treiberprotokoll der letzten Befehle.
* **NI-DAQmx** über die C-API (`nicaiu.dll`) hinter `IDaqmxApi`: Einzelwerte mit kurzlebigen Tasks (immer
  freigegeben), Mehrkanal-Aufnahme mit einem Abtasttakt (endlich, Blöcke à 0,2 s, Abbruch zwischen Blöcken, optional
  Starttrigger), Kurvenausgabe auf AO mit Regeneration (Laden → Übertragen → Starten → Stoppen wie in Etappe 8).
  Kanäle, Bereiche und Raten liest der Treiber vom Gerät. Ohne Hardware: simulierte Geräte in NI MAX.
* **Keine stille Anpassung**: Werte außerhalb des Modellbereichs werden vor dem Senden abgelehnt; Geräteeignung der
  Kurven wie in Etappe 8 (Generator/AO: frei wählbare Abtastrate im Gerätebereich, neu `MinSampleRate/MaxSampleRate`);
  das Oszilloskop liefert seine tatsächliche Abtastrate/Punktzahl und vermerkt Abweichungen in der Qualität.
* **Prüfplatzkonfiguration**: Geräte erhalten Adapter, Modell, Ressource, Zeitlimit und Optionen. Reale Geräte gibt es
  physisch einmal: ein nicht gemeinsames reales Gerät darf nur ein Prüfplatz verwenden; gesperrt wird standardmäßig das
  ganze Gerät (Option „Sperre je Kanal“). Die Konfigurationsprüfung prüft Kanäle gegen den Treiber und Einheiten gegen
  den Ablauf.
* **Sicherer Zustand**: zusätzlich zu den Exit-Gruppen nach jedem Lauf und beim Beenden der Anwendung (gemeinsame Geräte
  nur, wenn kein anderer Prüfplatz prüft). Fehlschläge werden gemeldet, die übrigen Geräte trotzdem geschaltet.
* **Verifikation ohne Hardware**: SCPI-Gerätesimulatoren als TCP-Server (echter Socket-/SCPI-Pfad, Fehlerwarteschlange,
  Bereichsprüfung, Verzögerung für Abbruchtests) mit gemeinsamem „Laboraufbau“ (Quellen speisen Messgeräte) und eine
  DAQmx-Nachbildung mit Echtzeit-Takt, Triggern und DAQmx-Fehlercodes. Beide auch in der Anwendung nutzbar
  („Laborsimulatoren starten“).

## Grenzen / offen für die Pilotstation (Etappe 11)

* Treiber gegen echte Geräte verifizieren (Befehlssätze einzelner Firmwarestände, z. B. AC6800B-Messbefehle,
  Oszilloskop-Messsyntax je Serie). Abweichungen zeigt „Gerät testen“ und die Fehlerwarteschlange.
* Dreiphasige AC-Versorgung: AC6800B ist einphasig; für L1–L3 z. B. drei Quellen oder eine dreiphasige Quelle
  (eigener Treiber nach Geräteauswahl).
* Hardwaresynchronisation über Gerätegrenzen nur über Trigger; gemeinsame Referenztakte (10 MHz) sind Gerätekonfiguration.
