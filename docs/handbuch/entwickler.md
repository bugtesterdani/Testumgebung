# Handbuch für Entwickler

Anmelden (oben rechts) mit dem Recht **Entwickeln**. Die Entwickleransicht öffnet sich über die Navigation links.

## Prüfablauf (Testschritte)

* Aufbau: **Test Start / Init** → **Test Loop / Testing** → **Test End / Finalization**. Der Prüfling ist eine Gruppe mit
  „Prüflingslauf“ (eigene Kennung, eigenes Ergebnis, eigener Datensatz).
* Bausteine aus der **Palette** einfügen; Eigenschaften rechts. Gruppen haben Eintrittsbedingung, Wiederholung
  (einmal, Anzahl, Zeitspanne, solange, bis, endlos), Ergebnisregel bei Wiederholung, Ressourcenreservierung mit Timeout
  und eine **Exit-Gruppe** (läuft immer, auch bei Abbruch).
* **Variablen** gehören zu Gruppen; Untergruppen sehen äußere Variablen, `../Name` sucht ab der Elterngruppe. Gemeinsame
  Auftragsdaten stehen im Hauptablauf (Projekt-Eigenschaften).
* **Werkerinteraktion**: Anweisung, Bild mit Bestätigung, Eingabe oder Auswahl; Datentyp, Pflicht, Standardwert, erlaubte
  Werte/Bereich, auf Antwort warten oder fortsetzen, Inhalt ersetzt vorherigen oder bleibt bis Gruppenende.
* **Anzeige und Speicherung** je Schritt/Gruppe (erben oder setzen): Anzeigen am Prüfplatz, Ergebnisdatei (+Kurven),
  Datenbank (+Kurven). „Anzeigen“ aus blendet Schritt und Messwerte in der Werkeransicht aus.

## Werkeransicht gestalten

Projekt-Eigenschaften (Editor ohne Auswahl bzw. „Projekt“) → **Werkeransicht: Layout und Darstellung**:
Position „Aktueller Schritt und Fortschritt“ (oben, links, unten), Ablaufübersicht und Protokoll ein/aus,
„Alle Messwerte standardmäßig anzeigen“ (aus = nur FAIL/ERROR, der Werker kann alle einblenden). Das Layout ist Teil des
Projekts und des Release-Pakets; ruhende Prüfplätze zeigen Änderungen sofort.

## CodeBlocks und Bausteine

C#, Python, C++ (und LabVIEW über C++-Glue), je Aufruf in einem isolierten Worker. **Build prüfen** zeigt Compilerdiagnosen,
**Test** führt den Baustein mit Testwerten aus. Wiederverwendbare Bausteine liegen unter **CodeBlocks**; „Aus Gruppe
erstellen“ macht grafische Bausteine. Details: `docs/adr/0002…`, `0003…`.

## Auswahl- und Auftragsablauf

**Auswahl & Auftrag** (je Prüfplatz): Methoden einzeln aktivierbar – manuell, Kennungsregeln (Muster mit `*`, `?`, `#`
oder `re:` regulärer Ausdruck; kleinere Priorität gewinnt; Version leer = aktuell freigegeben; Prüfparameter
`Name=Wert;…`), eigener Auswahlablauf. Meldungstext und Text der Alternative sind einstellbar. **Regel-Simulation** prüft
eine Beispielkennung gegen den Entwurf. Speichern schreibt die Prüfplatzkonfiguration (Administratorrecht auf ProgramData).

Eigener Auswahlablauf: ein Projekt (`.tproj.json`) mit dem Baustein **Test auswählen** (Kategorie „Auswahl & Auftrag“).
Es erhält `Kennung` und `Auftrag` als Vorbelegung, darf Dateien, Datenbanken oder CodeBlocks abfragen und liefert
Testkennung, optional Version, Auftrag und Prüfparameter. Der zugeordnete Test startet mit diesen Vorbelegungen
(Variablen gleichen Namens im Prüfling); die Eingabe „Kennung erfassen“ übernimmt die Kennung ohne Rückfrage.

Eine Python-Umgebung kann mit `SelectTest(..., pythonVenv: pfad)`, `select_test(..., python_venv=pfad)` oder dem optionalen Feld im Baustein „Test auswählen“ übergeben werden. Reihenfolge: Auswahlprogramm → sichtbare String-Variable `PythonVenv` → allgemeine Vorgabe → gebündeltes Python. Details und Init-Beispiele stehen in der [Python-Anleitung](../portable-python.md).

## Bearbeiten, Suchen, Vergleichen

* **Rückgängig/Wiederholen:** Strg+Z / Strg+Y (außerhalb von Eingabefeldern) oder Schaltflächen oben rechts.
* **Kopieren/Ausschneiden/Einfügen/Duplizieren** in der Kartenliste: Strg+C / Strg+X / Strg+V / Strg+D – Gruppen samt
  Inhalt und Exit-Gruppe, mit neuen IDs, eingefügt an der Einfügeposition (wie aus der Palette).
* **Suche:** Strg+F; Treffer in Namen, Bausteinen, Parametern (z. B. Ressourcen, Grenzwerte), Variablen, Bedingungen und
  Reservierungen; Enter/F3 = nächster, Umschalt+F3 = vorheriger Treffer.
* **Versionsvergleich:** Releases → Release wählen → „Versionsvergleich“ gegen eine andere Version oder den Editorstand.

## Kommunikation mit dem Prüfling

Palette „Kommunikation“: Senden, Empfangen, Abfrage (Text/Hex, Antwortmuster, Grenzwerte), Modbus lesen/schreiben, CAN
senden/empfangen (Signal, erwartete Daten), CAN-Signal lesen und CAN-Botschaft senden nach DBC, UDS-Diagnose (ISO-TP),
J1939 empfangen/senden (Anforderung, BAM, RTS/CTS), LIN lesen/senden (LDF). DBC/LDF importieren Sie unter
Projekt-Eigenschaften → „Busdatenbanken“; die Übersicht dort zeigt Botschaften und Signalnamen zum Eintragen.
Simulation: Projekt-Eigenschaften → „Kommunikation: Simulation des Prüflings“. Einzelheiten: `docs/geraete.md`.

Aus CodeBlocks (Ressource = abstrakter Name; Reservierung und Zugriffsprotokoll wie bei den Bausteinen):

| Python | C# | C++ | Ergebnis |
|---|---|---|---|
| `comm_send(res, text_oder_bytes)` | `CommSend(res, text \| byte[])` | `CommSend(res, text)` | – |
| `comm_query(res, text, timeout_ms=1000, end="\r\n")` | `CommQuery(res, text)` | `CommQuery(res, text)` | Antworttext (ohne Zeilenende) |
| `comm_receive(res, timeout_ms, end, length)` | `CommReceive(res, …)` | `CommReceive(res, …)` | Text |
| `modbus_read(res, "IR10", "Float32")` | `ModbusRead(res, adr, typ)` / `ModbusReadText` | `ModbusRead(res, adr, typ)` | Zahl, Bool, Text |
| `modbus_write(res, "HR20", 77)` | `ModbusWrite(res, adr, wert, typ)` | `ModbusWrite(…)` | – |
| `can_send(res, 0x7E0, bytes)` / `can_receive(res, 0x7E8, mask, timeout_ms)` | `CanSend` / `CanReceive` → `CanMessage` | `CanSend(res, id, "0102")` / `CanReceive` → Hex | `{"id", "data", "bytes", …}` |
| `uds(res, "27 01", tx=0x7E0, rx=0x7E8)` | `Uds(res, byte[])` | `Uds(res, "2701")` | positive Antwort (bytes/Hex); negative = Ausnahme mit „NRC 0x..“ |
| `j1939_receive(res, pgn, source, request)` / `j1939_send(res, pgn, data, …)` | `J1939Receive` / `J1939Send` | `J1939Receive` / `J1939Send` | Daten |
| `lin_read(res, 0x21, 4)` / `lin_write(res, 0x10, bytes)` | `LinRead` / `LinWrite` | `LinRead` / `LinWrite` | bytes / Hex |

Beispiel Sicherheitszugang (Python):

```python
seed = uds("Pruefling_CAN", "27 01")[2:]
key = bytes(b ^ 0x5A for b in seed)          # herstellerspezifischer Algorithmus
uds("Pruefling_CAN", bytes([0x27, 0x02]) + key)
return Pass()
```

## Debuggen

Haltepunkt mit **F9**, dann **Simulation starten**. Im Debugger: **F5** fortsetzen, **F11** Einzelschritt (nächstes fertiges
Element, auch in Gruppen), **F10** Prozedurschritt (nächstes Element derselben oder einer äußeren Ebene), **Umschalt+F11** bis
Gruppenende. Abmelden während einer Pause setzt den Lauf nicht fort. Pause vor der Ergebnisrückgabe: Variablen ansehen/ändern, Schritt
über/hinein, gezielt einzelne Schritte ausführen, Code-/Parameter-Patch nur ausdrücklich. Entwicklungsläufe werden im
Ergebnis gekennzeichnet.

## Kurven

**Kurven**: Import (CSV, COMTRADE, Excel, `.tres`), Messkurve übernehmen, Editor (Sinus, Rampe, Sprung, Formel).
Änderungen erzeugen neue, unveränderliche Versionen; Ausgabe über Laden → Übertragen → Starten → Stoppen mit
Eignungsprüfung des Geräts. Details: `docs/adr/0007-kurven.md`.

## Freigabe

**Releases**: Entwurf aus dem Editorstand → Entwicklungsprüfung (Integrität, Validierung, Build) → Freigabe durch ein
Konto mit Recht **Freigeben**. Pakete sind unveränderlich (Hashes), alles steht im Audit-Log. Prüfplätze übernehmen
neue Freigaben ab dem nächsten Prüfling. „Entwurf testen“ (Kopfzeile) prüft den Editorstand als Entwicklungslauf.

## Ergebnisse und Auswertung

**Ergebnisse**: Ergebnisdateien mit Reader (Ereignisse, Kurven), **Prüfbericht (PDF)** und CSV je Prüfling, Outbox und
Meldungen, unvollständige Prüfläufe nach Absturz (als abgebrochen speichern oder verwerfen). War der Ergebnisordner beim
Speichern nicht erreichbar, bietet ein Hinweis das Zurückkopieren aus dem Wiederherstellungsordner an.

**Auswertung** (auch mit Recht Freigeben): Datenquelle **Ergebnisordner** oder **Datenbank** (SQL bzw. simuliert) – bei der
Datenbank filtert der Server nach Zeitraum, Test und Prüfplatz (ohne Kurven). Danach filtern (Zeitraum, Test, Version,
Prüfplatz, Kennung), Ausbeute, First Pass Yield, Endausbeute, Fehler-Pareto, Messwerte mit Cpk und Histogramm,
Tagesverlauf; Export als PDF und CSV (Excel).

## Gruppen: Zeitspanne

Wiederholung „Zeitspanne“ prüft die Frist standardmäßig zwischen den Iterationen. Mit „Frist auch innerhalb der Iteration
prüfen“ werden nach Ablauf die restlichen Schritte der laufenden Iteration übersprungen (ein laufender Schritt wird nicht
unterbrochen, die Exit-Gruppe läuft).

## Externe Programme

Die Palette **Prozesse** startet EXE, Python-Skripte und .NET-DLL-Einstiegspunkte. Gebundene Lebensdauer, getrennte Argumente, spätere Warte-/Statusabfragen und Exitcode-Bewertung sind in [Externe Prozesse](../externe-prozesse.md) beschrieben.


## Parallele Gruppenschritte

In den Gruppeneigenschaften **Schritte und Untergruppen parallel starten** wählen.
Die Gruppe wartet vor Wiederholung und Exit auf alle Zweige. Abhängige Schritte
in einer intern sequenziellen Untergruppe bündeln und eigene Zielvariablen verwenden.
[Anleitung, Geräteverriegelung und Beispiel](../parallele-gruppen.md).

## Live-Ausgabe und Fortschritt

Prozessausgabe, laufend geschriebene Logdateien und eigene Python-/C#-Meldungen können
mit Status und Prozentwert angezeigt werden. [Anleitung und Regeln](../live-log-und-fortschritt.md).

## Aktivierung und bedingte Haltepunkte

Schritte und Gruppen können mit `disabled: true` übersprungen werden. Haltefilter
für Ablaufkarten und Codezeilen sind konfigurierbar; Standard ist immer.
[Bedienung, Ausdruckssprache und Trefferzahlen](../aktivierung-und-haltepunkte.md).

## Kommentare an Schritten und Gruppen

Im Ablaufeditor einen Schritt oder eine Gruppe auswählen. In den Eigenschaften
steht **Kommentar / Verlauf** als mehrzeiliges Textfeld bereit. Mit **Enter** wird
eine neue Zeile eingefügt. Längere Texte sind im Feld scrollbar.

Beispiel für manuelle Verlaufsnotizen:

```text
2026-11-01 – D. Weber – Wartezeit nach Geräteinitialisierung begründet.
2026-11-02 – D. Weber – Nach Firmwareprüfung wieder auf 2 s reduziert.
```

Die Ablaufkarte zeigt bei vorhandenem Text ein Kommentarsymbol. Beim Darüberfahren
erscheint der Kommentar mit seinen Zeilenumbrüchen. Kommentare werden als bisheriges
Beschreibungsfeld (`description`) im Projekt gespeichert; vorhandene Beschreibungen
erscheinen dort weiterhin. Speichern, Rückgängig/Wiederholen und der Versionsvergleich
verwenden die vorhandenen Projektfunktionen. Der Text verändert weder die Ausführung
noch die Bewertung. Datum und Autor werden bei Bedarf von Hand eingetragen; das Feld
ist kein automatisch geführtes oder unveränderliches Änderungsprotokoll.

## Änderungshistorie einer Ablaufkarte

Einen Schritt oder eine Gruppe auswählen und in den Eigenschaften auf
**Änderungshistorie** klicken. Ein eigenes Fenster zeigt die freigegebenen
Änderungen genau dieses Elements. Weitere Auswahlen im Editor verändern ein
bereits geöffnetes Historienfenster nicht.

Die Tabelle enthält das Feld, die Art der Änderung, den alten und neuen Wert,
die Freigabeversion mit Freigabezeitpunkt sowie die nächste Freigabe, in der
sich dieses Feld erneut geändert hat. Die Zeitangaben sind lokale Zeiten.
Unveränderte Werte behalten ihre ursprüngliche Freigabe als Beginn ihres
Zeitraums. Die Reihenfolge richtet sich nach dem Freigabezeitpunkt, nicht nach
der Schreibweise der Versionsnummer. Eine Zeile auswählen, um beide Werte
unten vollständig lesen und kopieren zu können, einschließlich mehrzeiliger
Kommentare und Quelltexte. Die Suche berücksichtigt Felder, Werte und Versionen;
**Nur zuletzt belegte Werte** blendet überholte und entfernte Werte aus.

Die Zuordnung erfolgt innerhalb desselben Programmnamens über die Element-ID.
Umbenennen oder Verschieben einer Karte beginnt deshalb keine neue Wertehistorie.
Kopien mit neuer ID haben eine eigene Historie. Bei Gruppen werden die eigenen
Einstellungen und Variablendeklarationen verglichen; für die Änderungen eines
Kindes dessen Karte öffnen. Bei Bibliotheksaufrufen ist auch die gespeicherte
Definition des aufgerufenen Bibliotheksbausteins enthalten. Die Reihenfolge
benannter Listen wird als eigenes Feld ausgewiesen.

Grundlage sind die vorhandenen freigegebenen und archivierten Release-Pakete.
Entwürfe, ungespeicherte Editoränderungen, gemessene Laufzeitwerte sowie globale
Prüfplatz- und DLL-Konfigurationen gehören nicht zu dieser Kartenhistorie.
Ein fehlendes optionales Feld bedeutet Standard oder Vererbung; ein leerer
String wird als `""` angezeigt. Der erste vorhandene Stand ist als **Erster
belegter Stand** gekennzeichnet: Was vor der frühesten vorhandenen Freigabe galt,
lässt sich daraus nicht ableiten. Die Historie kann vollständig aus der Ablage
entfernte Releases nicht rekonstruieren. Bei einem fehlenden oder beschädigten
Paket, fehlendem Freigabezeitpunkt oder nicht eindeutig sortierbaren Freigaben
werden keine vermeintlich gesicherten Teilzeiträume angezeigt.

Eine Freigabe ist nicht der Übernahmezeitpunkt an jedem Prüfplatz. Ein laufender
Prüfling kann noch die vorherige Version verwenden; der tatsächlich verwendete
Programmstand ist dem jeweiligen Prüfergebnis zu entnehmen.


## Live-Gruppenergebnis

Die schreibgeschützte Systemvariable `GruppenErgebnis` liefert das bisher
übergebene Ergebnis der eigenen Gruppe (`NRESULT`, `PASS`, `FAIL`, `ERROR`).
`../GruppenErgebnis` liest die Elterngruppe. Sie ist in Bedingungen,
Wiederholungsausdrücken und freien CodeBlocks ohne eigene Deklaration verfügbar.
[Zeitpunkt, Bausteingrenzen und Beispiele für C#, Python und C++](../live-gruppenergebnis.md).


## Visuelle Blockprogrammierung

**Blockansicht** im Ablaufeditor einschalten. **+ Schritt** am Ablaufblock fügt
Bausteine hinzu. Werte, Variablen, Auswahlfelder und Bedingungen werden direkt
in der Karte bearbeitet; **◈ Wert aus Blöcken** öffnet dort den eingebetteten
Baukasten für Berechnungen, Vergleiche und Logik. Seitliche Einstellungen und
Palette entfallen. Spezialoptionen bleiben innerhalb der Karte aufklappbar.
Beide Ansichten bearbeiten denselben Projektstand.
[Bedienung mit Bildern und Beispielprogramm](../visuelle-blockprogrammierung.md).
