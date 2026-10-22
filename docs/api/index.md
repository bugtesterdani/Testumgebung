# Quellcode und Schnittstellen

Diese Referenz ergänzt das Benutzerhandbuch. Doxygen zeigt auch undokumentierte
Mitglieder; ihre Aufnahme bedeutet nicht, dass ihr Vertrag vollständig beschrieben ist.
Die separate Datei `Dokumentationsstatus.html` weist fehlende Beschreibungen aus.

## Aufbau

- **Contracts:** Werte, Hardware- und Kommunikationsverträge sowie Kurven.
- **Domain:** Prüfprogramm, Variablenscopes, Ausdrücke und Codebaustein-Verträge.
- **Engine:** Start/Loop/End, Bewertung, Abbruch, Reservierungen und Live-Patches.
- **Hardware / Instruments:** Simulationen, reale Geräte und Protokolltransporte.
- **Workers:** isolierte C#-, Python- und native Ausführung mit Host-Rückrufen.
- **Persistence / DatabaseAdapters:** Projekte, Releases, Ergebnisse, Outbox und SQL.
- **Identity:** Anmeldung und Rechteprüfung.
- **Presentation / Desktop:** Zustandsmodelle und WPF-Oberfläche.
- **Cli:** Projekte prüfen, Code übersetzen und Szenarien simulieren; [Anleitung](../kommandozeile.md).
- **Notifications:** externe Ergebnismeldungen.

## Wichtige Verträge

Gerätezugriffe aus Codebausteinen erfolgen über `ICodeHost`, damit die zentralen
Reservierungen gelten. Ein Abbruch ist zunächst kooperativ; nach dem festgelegten
Zeitlimit beendet die Laufzeit den isolierten Worker. Auswahlcode und Prüfcode haben
unterschiedliche Möglichkeiten: `SelectTest` ist für den Auswahlablauf vorgesehen.

`IRunDatabase` speichert einen vollständigen Prüflingslauf idempotent in einer
Transaktion. Eine verlorene Antwort nach erfolgreichem Commit darf beim erneuten
Übertragen keine Duplikate erzeugen. Die Outbox übernimmt spätere Wiederholungen.

## Grenzen der Dokumentation

Die Referenz beschreibt Quellcode und Verträge, keine Bestätigung realer Hardware.
Kommentare sollen Zweck, Einheiten, Seiteneffekte, Abbruch, Fehler und Randbedingungen
erklären. Eine hohe Anzahl von Kommentaren ist kein Nachweis ihrer fachlichen Qualität.

## Lizenz

Eigener Projektcode und eigene Dokumentation stehen unter MIT. Doxygen wird separat
als Entwicklungswerkzeug verwendet und nicht in das MSIX aufgenommen. Die automatisch
erzeugten HTML-Dateien behalten ihre von Doxygen mitgelieferten Hinweise und Assets.
