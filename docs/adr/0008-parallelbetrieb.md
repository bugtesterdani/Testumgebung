# ADR 0008 – Parallelbetrieb: Prüfplatzkonfiguration, Sperre je Gerät, Zyklusrisiko

Stand: 03.10.2026 · Status: angenommen für Etappe 9

## Prüfplatzkonfiguration (Entscheidung: je Rechner)

* `%ProgramData%\Testumgebung\pruefplaetze.json` (`StationSetupStore`), für alle Windows-Benutzer gleich, Schreibrecht
  typischerweise nur für Administratoren. Projekte bleiben geräteunabhängig und verwenden nur abstrakte Namen.
* Inhalt: **Geräte** (Id, Name, Adapter, *gemeinsam* ja/nein) und **Prüfplätze** (Entscheidung: konfigurierbar, Standard
  zwei) mit der Zuordnung *Name → Gerät/Kanal* und *Sperre erforderlich*.
* Ohne Datei gilt ein **Vorschlag aus dem Projekt**: Ressourcen mit Sperre liegen auf einem gemeinsamen Gerät
  (`sim-gemeinsam`, z. B. AC-Leistungsquelle), alles andere auf einem lokalen Gerät je Prüfplatz (`sim-lokal`).
  Der Vorschlag folgt dem geöffneten Projekt, bis gespeichert wird.
* Beschädigte Datei: kein stiller Rückfall – Anzeige auf der Konfigurationsseite, **kein Prüfplatz startet**, bis eine
  korrigierte Konfiguration gespeichert ist.
* Übernahme nur ohne laufende Prüfung (Geräteinstanzen werden neu angelegt); Prüfplätze erscheinen/verschwinden sofort.

## Gerätepool und Sperrschlüssel

* `DevicePool`: ein gemeinsames Gerät existiert **einmal** im Prozess, lokale Geräte einmal je Prüfplatz. Instanzen
  leben über Läufe hinweg (wie reale Hardware); ein Kanal wird nur beim ersten Mal eingerichtet, damit ein Prüfplatz
  laufende Ausgaben eines anderen nicht überschreibt.
* **Gesperrt wird das Gerät/der Kanal, nicht der Name**: `ResourceBinding.Key` = `gerät:kanal` (gemeinsam) bzw.
  `prüfplatz/gerät:kanal` (lokal). Nennt Prüfplatz 02 die AC-Quelle `Netz_AC`, sperrt er trotzdem dieselbe Sperre wie
  Prüfplatz 01 mit `AC_Quelle`. Die Engine bildet die Gruppenressourcen über die Registry auf Schlüssel ab; die
  Zugriffsprüfung (Exit darf unreservierte Hardware nicht verwenden) prüft ebenfalls den Schlüssel.
* Konfigurationsprüfung: ein gemeinsames Gerät **ohne Sperre** ist ein Fehler (widersprüchlicher Zugriff möglich),
  ebenso fehlende Zuordnungen von im Ablauf verwendeten Namen, unbekannte Geräte und doppelte Namen. Gesperrte Hardware,
  die an keiner Gruppe reserviert wird, ergibt einen Hinweis.

## Zyklusrisiko (Entscheidung: Start verweigern)

* Atomarer Erwerb verhindert Teilbesitz, aber keine Verklemmung verschachtelter Erweiterungen (A hält X und will Y,
  B hält Y und will X). `LockOrderAnalysis` sammelt alle Erweiterungen *gehalten → zusätzlich* (auch in Exit-Gruppen und
  grafischen Bausteinen) mit den Sperrschlüsseln des jeweiligen Prüfplatzes; ein Zyklus über alle beteiligten Abläufe
  ist ein Zyklusrisiko.
* Prüfung in der Konfigurationsprüfung (alle Prüfplätze mit dem Projekt), beim **Start** gegen die laufenden Abläufe der
  anderen Prüfplätze (`StationCoordinator.TryBeginRun` – Start verweigert mit Begründung und Vorschlag) und beim
  Versionswechsel am Prüfling (neue Version wird dann nicht übernommen).
* Sicherheitsnetz zur Laufzeit: Der `ResourceManager` führt einen Wartegraphen (Besitzer und vorrangige Wartende).
  Würde eine Anforderung einen Zyklus schließen, wird sie nicht eingereiht (`ResourceDeadlockException`) – die Gruppe
  ergibt ERROR wie bei einem Zeitlimit, reguläre Schritte werden übersprungen, nichts hängt.

## Nachweis „kein widersprüchlicher Gerätezugriff“

* `HardwareAccessJournal`: jeder Zugriff (lesen, schreiben, Kurve, Kurvenaufnahme) mit Lauf, Prüfplatz, Name,
  Sperrschlüssel, Beginn/Ende. Widersprüchlich ist ein Zugriff gleichzeitig mit einem anderen Lauf auf denselben
  Schlüssel, ohne eigene Reservierung auf gesperrter Hardware oder bei Verlust der Reservierung während des Zugriffs.
* Anzeige auf der Konfigurationsseite (Zugriffsprotokoll mit Befund), Test `ParallelTests.Etappe9_*`: zwei Prüfplätze
  mit je sechs Prüflingen, gemeinsame AC-Quelle unter verschiedenen Namen, nachweislich Wartezeiten – kein Konflikt,
  Zugriffe zeitlich getrennt, das Gerät sieht immer vollständige Ein/Aus-Folgen eines Prüfplatzes. Gegenprobe ohne
  Sperre: Konflikte werden erkannt.

## Oberfläche

* Seite **Prüfplatzkonfiguration** (Recht Entwickeln): Prüfplatz-Reiter (anlegen/entfernen), Geräte und Signale mit
  Live-Status (Bereit, Reserviert – Prüfplatz 02, wartend), Ressourcen-Details (Adapter, Gerät, Kanal, Bereiche,
  synchrone Zeitbasis, Waveform-Speicher, Aktionen, Sperrschlüssel, gemeinsam mit), Ressourcenreservierungen je Gruppe
  (Timeout, RessourcenErhalten, Wartestatus je Prüfplatz, Gültigkeitsbereich Run/Gruppe), Geräte, Konfigurationsprüfung,
  Zugriffsprotokoll; „Simulation prüfen“ und „Zuordnung speichern“.
* Werkeransicht: „Warten auf AC_Quelle (belegt von Prüfplatz 02)“; Zeitlimit-Meldung nennt den Besitzer.
