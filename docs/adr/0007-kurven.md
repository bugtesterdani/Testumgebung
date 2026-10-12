# ADR 0007 – Kurven: drei Wege, Versionen, Ausgabe in vier Phasen

Stand: 02.10.2026 · Status: angenommen für Etappe 8

## Modell

* `CurveDefinition` (Domain/Curves): Kennung, Name, **Version**, Beschreibung, gemeinsame Zeitbasis für alle Kanäle
  (feste Abtastrate **oder** explizite Zeitpunkte), Kanäle mit Einheit und Samples (float64), Herkunft
  (`Generator`, `FileImport`, `Measurement`) mit Quelltext (Generatorparameter bzw. Datei, SHA-256 und Zuordnung),
  Bearbeitungshistorie, Ersteller/Zeitpunkt, Qualitätshinweis. `ContentHash()` ist der SHA-256 über Zeitbasis,
  Kanäle, Einheiten und Samples.
* `CurveLibrary` je `TestDefinition`: Versionen sind **unveränderlich** – `Add` lehnt eine vorhandene Kennung/Version ab;
  Bearbeiten erzeugt immer eine neue Version (Vorschlag über `NextVersion`). „Kurve laden“ ohne Version = neueste.
* Speicherung im Projekt-JSON (`curves`, Samples Base64 float64-LE, Hash je Version). Ein abweichender Hash beim Laden
  ist ein Formatfehler. Im Release-Paket hat jede Kurvenversion einen eigenen Bereichshash („Kurven“); damit sind die
  Kurvenversionen Teil der Freigabe. Ändert sich eine Kurve, die außerhalb des Prüflings (Init/Finalization) geladen
  wird, ist der Versionswechsel nicht am Prüfling möglich (V01-Regel wie beim Ablauf).

## Drei Wege

1. **Dateiimport** (`Persistence/Curves`): `CurveImport.Read` wählt nach Endung.
   * CSV/Text: Trennzeichen (`;`, Tab, `,`, `|`) und Dezimalkomma automatisch, `#`-Kommentare, Kopfzeile, optionale
     Einheitenzeile oder Einheit im Spaltennamen (`U1 [V]`).
   * COMTRADE (IEEE C37.111, 1999/2013): `.cfg` + `.dat` in ASCII, BINARY, BINARY32, FLOAT32; Wert = a·x + b,
     fehlende Werte (0x8000 / 0x80000000) als Lücke; Zeitstempel × `timemult`; mehrere Abtastraten → Warnung.
   * Excel `.xlsx`: eigener Leser (ZIP + SpreadsheetML, gemeinsame Zeichenfolgen, Inline-Strings), Blattwahl.
   * Ergebnisdatei `.tres`: Kurvenaufnahme als Tabelle (feste Abtastrate der Aufnahme).
   Zuordnung: Quellspalte → Zielkanal mit Einheit, Zeitbasis (Zeitspalte mit Faktor → s, streng steigend, auf 0
   bezogen, **oder** feste Abtastrate aus Datei bzw. ausdrücklicher Angabe), Qualitätsspalte (Codes ≠ 0 werden gezählt
   und an der Kurve vermerkt). Fehlende Werte sind ein Fehler – es wird nichts still aufgefüllt.
2. **Messkurve übernehmen**: aus einer Ergebnisdatei (Ergebnisordner oder beliebige Datei) wird eine Aufnahme gewählt
   und als neue Ausgabekurve übernommen. Herkunft (Datei-Hash, Run, Prüfling, Schritt) wird mitgeführt; die
   Ergebnisdatei wird nur gelesen.
3. **Editor**: Sinus (Amplitude, Frequenz, Phasen je Kanal, Offset), Rampe, Sprung/Stufen (`Wert:Dauer;…`) und
   Formel f(t) je Kanal (eigener Parser: + − × ÷ ^, sin, cos, tan, asin, acos, atan, sqrt, abs, exp, ln, log10,
   floor, ceil, round, sign, step, min, max, pow, mod, clamp, pi, e). Ergebnis ist bitgleich reproduzierbar.

## Bearbeitung – nur ausdrücklich

`CurveOps`: Zuschnitt (Zeit wird auf 0 bezogen), Skalierung (alle oder ein Kanal), Zeitverschiebung (vorn mit dem
ersten Wert auffüllen bzw. abschneiden), Abtastrate ändern (linear interpoliert; auch explizite Zeit → feste Rate),
Kanäle umbenennen. Jeder Schritt steht in der Historie der Kurve. **Keine** Operation geschieht implizit – weder beim
Import noch bei der Ausgabe.

## Ausgabe

* `IWaveformOutputAdapter`: `LoadAsync`, `TransferAsync`, `StartAsync`, `StopAsync` mit Zustand
  Leerlauf → Geladen → Übertragen → Läuft → Gestoppt (erneut startbar). Falsche Reihenfolge →
  `OutputSequenceException` (Bewertung ERROR im Ablauf).
* Geräteeignung (`OutputSuitability.Check`) beim Laden: Kanäle vorhanden, Einheit gleich (keine Umrechnung),
  Wertebereich (keine Skalierung), Abtastrate unterstützt (keine Konvertierung), explizite Zeitpunkte nur wenn das
  Gerät sie kann, maximale Samplezahl. Jeder Befund nennt die ausdrückliche Abhilfe im Editor.
* Bausteine „Kurve laden / übertragen / starten / stoppen“ (Kategorie Kurvenausgabe). Simulation: die laufende Kurve
  wird periodisch auf die zugeordneten Messkanäle zurückgeführt (z. B. L1→u1), damit die Ausgabe messbar ist (W01).

## Oberfläche

* Seite **Kurven** (Recht Entwickeln): Bibliothek mit Herkunft, Historie, Hash und Verwendung; Reiter Editor,
  Dateiimport, Messkurve mit Datenvorschau, Kanalzuordnung, Zeitbasis und Kanalinformationen; Vorschau mit
  Kanalauswahl, Zoom (Aufziehen, Mausrad, Schaltflächen), Verschieben und Cursor; Bearbeitung mit Rückgängig;
  „Version speichern“; Geräteeignung je Ausgabegerät; Testausgabe in vier Schritten gegen die Simulation mit Rückmessung.
* Werkeransicht: Kurven der letzten Aufnahme (L1–L3) mit Cursor und Effektivwerten im sichtbaren Fenster.
* Die Darstellung reduziert große Kurven je Pixelspalte auf Minimum und Maximum, damit Spitzen sichtbar bleiben.

## Abnahme W01

`CurveTests.W01_*`: Kurve aus Editor, Datei und Messung; dreiphasige Ausgabe über Laden/Übertragen/Starten/Stoppen im
Ablauf; die gemessenen Kanäle stimmen mit der ausgegebenen Version phasen- und kanalrichtig überein; Zeitbasis,
Kanalzuordnung und Version bleiben über CSV-Export/-Import, Projekt und Release-Paket reproduzierbar.
