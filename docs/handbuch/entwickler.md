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
  Optional spielt **Audiodatei** eine externe MP3-/WAV-Datei vom Laufwerk oder über HTTP/HTTPS während des Wartens wiederholt ab.
  Nur die Referenz wird gespeichert; Details: [Wartemusik](../werker-wartemusik.md).
* **Anzeige und Speicherung** je Schritt/Gruppe (erben oder setzen): Anzeigen am Prüfplatz, Ergebnisdatei (+Kurven),
  Datenbank (+Kurven). „Anzeigen“ aus blendet Schritt und Messwerte in der Werkeransicht aus.

## Neues Prüfprogramm und Hilfe

Über **Neu** im Ablaufeditor öffnest du den Projektassistenten. Wähle eine
mitgelieferte Vorlage oder einen leeren Ablauf und gib einen Projektnamen ein.
Die Kopie startet mit Version 0.1 und wird erstmals über **Speichern** abgelegt.
Die mitgelieferte Vorlage bleibt erhalten.

Mit **F1** öffnest du die Hilfe für die aktive Ansicht. Ist ein Schritt im
Ablaufeditor ausgewählt, siehst du zusätzlich die Beschreibung und Parameter
seines Bausteins.

## Etikett drucken und gestalten

Der Baustein **Etikett drucken** verwendet standardmäßig eine ZPL-Vorlage. Platzhalter wie
`{Kennung}` werden beim Ausführen ersetzt. Für den Druck kannst du einen
TCP-Drucker oder eine Windows-Druckwarteschlange wählen. Die Simulation zeigt
den erzeugten ZPL-Text als Schrittergebnis.

Über **Etikett gestalten …** lassen sich Elemente und Bilder visuell platzieren.
Die Druckauflösung muss zum Drucker passen. Der Designer bettet Bilder aus
PNG, JPEG, BMP, GIF oder TIFF als Schwarzweißgrafik in die Vorlage ein.

### cab-Drucker mit LBL-Datei

Der cab-Editor erschließt den JScript-Befehlskatalog **vollständig nach Handbuch
Ausgabe 07/2026**, Firmware 5.48: 237 geprüfte Einträge, 1.416 Parameter und
1.801 Tabellenwerte. Der [Abdeckungsbericht](../cab/abdeckung.md) beschreibt die
ausführbaren Nachweise und Grenzen. Antwortfelder bleiben lesbare Druckerausgaben;
allgemeine Kontextregeln erzeugen keine erfundenen zusätzlichen Befehle.

Unter **Vorlagenformat** den Wert **cab LBL** wählen. Über **LBL-Datei einbetten …**
lässt sich eine JScript-Druckdatei ins Prüfprojekt übernehmen. Ein bearbeitbares
cablabel-Layout vorher als Druckdatei exportieren; beim Ziel **Datei** enthält es
auch eingebettete Bilder und Schriften. [cablabel-S3-Handbuch](https://www.cab.de/media/pushfile.cfm?file=2238)

**cab-Etikett gestalten …** öffnet eine Arbeitskopie. **LBL öffnen …** und das
Ablegen einer einzelnen `.lbl`-Datei laden denselben verlustfreien Dokumentpfad;
**LBL speichern …** exportiert ihn. Unverändertes Öffnen und Speichern erhält die
Bytes einschließlich Zeilenenden, ESC-Befehlen, Codepagewechseln, mehreren
Aufträgen, Binärdownloads und unbekannten Inhalten. Vor dem Dateiimport die
Druckauflösung und die anfängliche Codepage einstellen (Vorgaben: 203 dpi, UTF-8).
`ESCo` kann die aktive Kodierung innerhalb der Datei ändern; nicht dekodierbare
Bytes bleiben erhalten. Abbrechen verändert das Projekt nicht. Erst
**Vorlage übernehmen** speichert Quelle, Einstellungen und eingebettete Druckdatei.
Ein älteres Designer-Layout wird dafür einmalig in eine private JScript-Quelle
überführt; vorhandene Projekte und bereits freigegebene Pakete werden nicht umgeschrieben.

Die drei ergänzenden Ansichten erschließen den gesamten Umfang:

- **Quelltext …** bearbeitet den vollständigen Quelltext mit Kataloghilfe,
  Vervollständigung und Diagnosen. Geschützte Binärbereiche bleiben unverändert.
- **JScript-Eigenschaften …** zeigt typisierte Parameter, Einheiten,
  Generationshinweise und Wahlwerte; hier lassen sich Aufträge, Kommentare,
  Ressourcen und Inhaltsfelder bearbeiten. Abhängige Varianten benötigen die
  zugehörige Schrift, Barcodeart oder übergeordnete Option.
- **Vollständige cab-Grafik …** zeigt den gewählten Auftrag mit Etikettgröße,
  Abstand, Spalten, Versatz, Spiegelung und Drehung. Alle 50 grafischen Definitionen
  einschließlich aller 43 Barcodeprofile sind einfügbar und bearbeitbar.
  Schriften, Klarschrift und SC-Standardmaße sind erklärte Näherungen. Unterstützte
  Barcodevarianten werden mit ZXing.Net 0.16.11 erzeugt, sonst erscheinen
  beschriftete Flächen berechneter Näherungsgröße. Es werden keine Ersatzbalken
  als echte Symbologie ausgegeben. [Grafik und Grenzen](../cab/grafik.md)

Die ursprüngliche einfache Zeichenfläche bleibt als ergänzende Ansicht erhalten.
Für weitere Schriften, Rich Text, Formen, gedrehte Objekte und native Bildformate
die vollständige Grafikansicht verwenden. Änderungen betreffen gebundene
Quellbereiche; fremde Befehle werden nicht pauschal neu formatiert.

**Generation und Prüfung …** wählt Ax, X2, X3 oder X4 je Auftrag (Vorgabe X4).
Die Auswahl bleibt im Projekt und verändert keine Druckbytes. Diagnosen nennen
Zeile, Spalte, Katalog-ID und Handbuchseite. Sie erscheinen auch in den
Projektprüfungen der Kommandozeile und in der Entwicklungsprüfung einer Freigabe.
Fehler verhindern eine neue Freigabe; Warnungen bleiben sichtbar und verhindern
sie nicht. Prüfungen führen keine Drucker-, Datei-, RFID- oder Gerätebefehle aus.

Platzhalter wie `{Kennung}` werden beim Drucken nur in tatsächlichen Text- und
Datenparametern eingesetzt, auch in Barcode, Rich Text und Feldersetzungen.
Binärdaten und ihre Klammern bleiben unverändert. Neue Vorlagen benötigen keinen
`CabLblTextOffset`; ältere Projekte mit diesem Feld bleiben lesbar. Die Datei
bleibt in `CabLblDaten`, Quelle und Einstellungen in `CabLblLayout`. TCP und
Windows-RAW senden die resultierenden Bytes; die Simulation zeigt deren Anzahl.

Neben Platzhaltern stehen alle 107 Inhaltsfelder zur Verfügung.
**Inhaltsfeld-Vorschau …** zeigt Quelle, Offlinewert und Handbuchhinweise;
Beispielzeit und regionale Regeln werden gespeichert. Das vorhandene Feld
**Beispielwerte für Platzhalter** bleibt nutzbar. Hardwareabhängige Werte sind
gekennzeichnete Beispiele. Die Vorschau emuliert keine Druckerhardware und
behauptet keine druckergleiche Pixelwiedergabe. Widersprüche und unklare
Herstellerangaben stehen ausdrücklich als Hinweise im geprüften Katalog.
[Validierung](../cab/validierung.md) · [Inhaltsfelder](../cab/inhaltsfelder.md) ·
[Datei- und Projektintegration](../cab/integration.md) ·
[Herstellerhandbuch](https://www.cab.de/media/pushfile.cfm?file=3047)
Für cab zusätzlich den Abstand zwischen den Etiketten eingeben. Ein Abstand von 0 mm steht für Endlosmaterial.
DataMatrix, GS1 DataMatrix und GS1 QR sind für ZPL und cab LBL verfügbar. Bei GS1 zeigt die Kennungsauswahl Bedeutung und Länge der häufigsten Application Identifiers. Über **Kennung einfügen** eine Kennung vor dem jeweiligen Wert oder Platzhalter einsetzen, z. B. `(01){GTIN}(10){Charge}(21){Kennung}`. Der ZPL-Druckpfad setzt die erforderlichen GS1-Steuerzeichen ein; bei cab bleiben die nativen JScript-Befehle und Datenstrukturen erhalten. GS1 QR benötigt einen Drucker mit entsprechender Firmware. [Zebra-Anleitung zu GS1](https://support.zebra.com/article/000015447) · [GS1-Kennungsverzeichnis](https://ref.gs1.org/ai/)

Der Designer erzeugt eine druckfertige JScript-Datei mit eingebetteten Bilddaten und speichert das bearbeitbare Layout im Prüfprojekt.
In Text- und Barcodefeldern kannst du Platzhalter wie `{Kennung}` einsetzen. Unter **Beispielwerte für Platzhalter**
gibst du je Zeile einen Wert im Format `Kennung=SN-123` ein. Der Designer setzt diese Werte in die Vorschau ein und lässt
den übrigen Text stehen. Beim Drucken werden die tatsächlichen Prüfvariablen verwendet. Externe LBL-Druckdateien werden weiterhin bytegenau gesendet.

Extern importierte LBL-Dateien bleiben mit ihren Originalbytes im Projekt. Abbrechen verändert die
eingebettete Datei nicht. Bei anderen Codepages, Zeilenenden, Inhaltsfeldern oder späteren Binärblöcken
bleibt der bisherige UTF-8-Druckplatzhaltermechanismus deaktiviert; die Datei wird bytegenau gesendet.
[Programmierhandbuch cab JScript](https://www.cab.de/media/pushfile.cfm?file=3047)

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

Bei der Grenzauswertung **Steigung** lassen sich im vorhandenen Aufnahmebaustein zusätzliche Regeln einstellen.
**Anlaufausnahmen: Anzahl je Kanal** zählt je Kanal ausschließlich Grenzverletzungen,
deren Steigung größer als **Anlaufausnahmen: nur ab Steigung** ist. X = 0 deaktiviert diese Ausnahme.
**Richtung der Anlaufausnahmen** wählt Steigend (> Y), Fallend (< −Y) oder Beide (Betrag > Y).
Bei Fallend und Beide ist Y ein nichtnegativer Betrag. Alte Projekte erhalten beim Laden Steigend als Vorgabe.
Anlaufausnahmen gelten nur bis zur ersten regulär bewerteten Steigung je Kanal und höchstens bis zur **Anlaufdauer** nach dem ersten Messpunkt. Unter „Erweitert“ erscheint **Anlaufausnahmen: nur bis [s] nach Aufnahmebeginn**, sobald die Anzahl positiv ist. Neue Bausteine verwenden 1 s; ein endlicher Wert > 0 ist erforderlich. Alte Projekte und gespeicherte Aufnahmen ohne dieses Feld behalten die unbegrenzte Dauer und zeigen „Anlaufphase unbegrenzt“ als Diagnose bzw. Ergebnishinweis.

Beispiele: Nach 4 s konstant 8,5 V wird ein Sprung um 0,6 V in 10 ms (60 V/s) bei 1 s Anlaufdauer regulär bewertet und verletzt Grenzen von −1 bis 1 V/s: FAIL. Beginnt die Aufnahme dagegen 0,5 s vor dem Einschalten bei 0 V und entsteht der gewollte Überschwinger bei 0,52 s, kann er bei 1 s Anlaufdauer noch ausgenommen werden. Fensterenden genau auf der Zeitgrenze sind eingeschlossen. Gesammelte flache Fenster beenden die Anlaufphase nicht vorzeitig.
Ein späterer Sprung wird daher auch bei unverbrauchter Ausnahmeanzahl bewertet.
Ausgenommene Werte lösen die Steigungsbewertung und deren zusätzliche Abbruchabweichung nicht aus.

**Kleine Steigungen sammeln bis + / −** sind direkt in der Kanaleinheit pro Sekunde
angegeben, zum Beispiel V/s. Die nächste Steigung wird bewertet, sobald sie größer als die positive
Schwelle oder kleiner als die negative Schwelle ist. Werte zwischen beiden Schwellen einschließlich
der Grenzen werden protokolliert. Der Ausgangspunkt und die gesamte verstrichene Zeit bleiben
für den nächsten Vergleich erhalten. Beide Schwellen 0 deaktivieren diesen Filter.
Der gesamte ausgenommene Bereich muss innerhalb der normalen Steigungsgrenzen liegen.
Wenn Berühren nicht zulässig ist, müssen die Schwellen streng innerhalb liegen.
Ein aktivierter Mindeststeigungsfilter ist bei einer geforderten minimalen Steigung über 0 nicht verwendbar.
Ungültige Kombinationen bleiben beim Laden erhalten und werden als Konfigurationsdiagnose angezeigt.
Getrennte Fenster prüfen beim regulären Aufnahmeende, Zielband-Ende und manuellen Bewerten zusätzlich ein verkürztes Restfenster. Bei einem Restpunkt zählt der letzte Punkt des vorherigen Fensters als Ausgangspunkt; bei mehreren Restpunkten zählt der erste Restpunkt. Die Bewertung erfolgt genau einmal und nicht bei Live-Vorschauen. Sicherheitsabbruch, bereits ausgelöste Abbruchgrenzen und ungültige Aufnahmen erhalten keine zusätzliche Restbewertung. `PartialWindowTimes` enthält die Endzeit, die Kurvenansicht nennt den verkürzten Vergleich. Gespeicherte Bewertungen bleiben maßgeblich; alte Läufe erhalten wegen neuer Restfenster nur bei zusätzlichen Grenzverletzungen einen Hinweis.

Die Live-Anzeige wartet nicht auf abgeschlossene Steigungsfenster: Die ersten beiden Rohmesspunkte, die erste Grenzverletzung und das Aufnahmeende erscheinen sofort; weitere Vorschauen werden auf etwa vier pro Sekunde begrenzt. Steigungspunkte werden weiterhin erst nach abgeschlossenem Fenster berechnet. Die Aktualisierungshäufigkeit ändert weder Grenzbewertung noch Schrittergebnis oder `StabilesZiel`.

Die Regeln gelten auch für getrennte und gleitende Fenster. Dort bestimmt das gewählte Fenster weiterhin,
wann ein neuer Vergleich erfolgt. Bei ausgenommenen Vergleichen bleibt dessen erster Ausgangspunkt bestehen.

Beispielsweise ergeben 0 V bei 0 ms, 0,05 V bei 10 ms und 0,1 V bei 20 ms jeweils 5 V/s.
Bei einer positiven Schwelle von 10 V/s bleiben diese Vergleiche ausgenommen. 0,6 V bei 30 ms
ergeben im aktuellen 10-ms-Fenster 50 V/s. Dieser schnelle Sprung wird direkt bewertet.
Eine vorherige lange flache Phase kann ihn dadurch nicht verdünnen. Nur solange auch das aktuelle
Fenster zwischen den Mindeststeigungen liegt, wird der gesammelte Vergleich verwendet.
Nach einem direkt bewerteten Fenster beginnt die nächste
Bewertung wieder gemäß dem gewählten Fenstermodus.

Im Prüfergebnis stehen Anzahl und Details unter **SuppressedSlopes** in den gespeicherten
Kurvenmetadaten und der JSON-Ergebnisvariable. Graue Rauten in der Kurvenansicht kennzeichnen ausgeschlossene Steigungen; der Hinweistext zeigt Kanal, Zeit, Steigung, Ausgangszeit und beide Messwerte mit Ausschlussgrund. In gespeicherten Ergebnissen listet „Ausgeschlossene Steigungen (je Kanal)“ diese Angaben auf. Alte Ergebnisse ohne Ausschlüsse bleiben darstellbar. Die gespeicherten Gründe „Mindeststeigung nicht erreicht“ und „Anlaufausnahme X von Y“ bleiben lesbar. Jeder Eintrag enthält Zeit, Steigung, Ausgangszeit,
Ausgangswert, Endwert und den Ausnahmegrund. Die normale Steigungsdarstellung enthält die
berechneten Werte, ausgenommene Vergleiche erhalten keine Grenzverletzungsmarkierung.
Je Kanal muss mindestens ein regulär bewertetes oder wegen Mindeststeigung gesammeltes Fenster vorliegen. Vollständig erfasste Haltephasen ergeben damit PASS. Ohne Fenster (zu wenige Punkte) oder bei ausschließlich Anlaufausnahmen ergibt sich jeweils eine eigene ERROR-Meldung. Ein erreichtes stabiles Zielband darf weiterhin vor dem ersten vollständigen Steigungsfenster erfolgreich enden.
Harte Messwert-Sicherheitsgrenzen bleiben bei jedem Rohmesswert wirksam.

**Neustart einer Aufnahme:** Nach einem automatischen Ende (zusätzliche FAIL-Abweichung, Sicherheitsgrenze, Dauer oder stabiles Ziel) darf **Messwertverlauf beginnen** dieselbe Kennung desselben Prüflings erneut verwenden. Das startet eine frische Aufnahme; bereits ausgegebene FAIL-Ergebnisse bleiben gespeichert. Bis dahin ist die alte Aufnahme weiter abschließend bewertbar. Laufende und pausierte Aufnahmen bleiben vor Überschreiben geschützt. Scheitert die Validierung des Neustarts, bleibt auch die alte Aufnahme erhalten.

Bei **Messwertverlauf aufnehmen**, **Messpunkt ergänzen** und **Verlauf beenden und bewerten**
kann **Bei Aufnahmeende immer zur Prüflings-Exit-Gruppe (PASS / FAIL / ERROR)** aktiviert werden.
Die Option überspringt nach Abschluss der Aufnahme die regulären Folgeschritte und führt die
aktiven Exit-Gruppen des Prüflings von innen nach außen aus. Bei manueller Aufnahme die Option
am Messpunkt oder am abschließenden Bewertungsbaustein setzen. Während einer noch laufenden
Aufnahme werden weitere Messpunkte auch nach einer normalen Grenzverletzung aufgenommen.
Ein ERROR des so konfigurierten Bausteins führt ebenfalls direkt zur Exit-Gruppe.

Für die PSU-Prüfung an fehlerrelevanten Standard-Schritten unter **Erweitert** die Option
**Bei ERROR zur Prüflings-Exit-Gruppe** aktivieren. Bei der festen Bewertung kann die Option
auch beim expliziten FAIL auslösen. Die Engine überspringt weitere reguläre Schritte des
Prüflings und führt dessen aktive Exit-Gruppen von innen nach außen aus. In der Exit-Gruppe
die für den Prüfplatz konfigurierten Standard-Ausgabeschritte zum Abschalten von Quelle,
Lade- und Entladeansteuerung hinterlegen. Die Ergebnisbewertung des Prüflings wird nach
diesem Abschluss veröffentlicht.

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

Die [Knotenansicht](../knotenansicht.md) ergänzt manuelle Verbindungen, Zoom,
Verschieben und Debugwerte am bisherigen Haltepunkt vor der Ergebnisweitergabe.

### Einstieg in die Verwaltung

„Verwaltung“ öffnet den ersten erlaubten und verfügbaren Unterpunkt in der sichtbaren Reihenfolge: Datenbank, Meldewege, Zugang, Python, Sicherung. Dadurch führt der Einstieg bei vorhandener Datenbankkonfiguration zuerst dorthin. Das Auftragssymbol (Liste) unterscheidet sich vom Releases-Symbol.
### Sprache der Werkeransicht

Die Auswahl „Sprache Werkeransicht“ betrifft die Werkeroberfläche. Entwicklung, Verwaltung und selbst erstellte Prüfprogramminhalte behalten ihre bisherigen Texte. Der Hinweis direkt an der Auswahl beschreibt diesen Geltungsbereich.
### Sichtbare Bausteinparameter

`VisibleWhen` im Bausteinkatalog beschreibt erlaubte Auswahlwerte (`AllowedValues`), Ja/Nein-Schalter (`Boolean`) und positive endliche Zahlen (`GreaterThanZero`). Bedingungen unter `All` gelten gemeinsam. Fehlende Projektwerte verwenden den Vorgabewert des jeweiligen Parameters. Der Editor wertet diese Metadaten einheitlich aus; bei Diagnosen bleiben Eingabefelder zur Korrektur erreichbar. Nur die als Tabelle bearbeiteten Kanal-Einheiten und Grenzkurven bleiben als doppelte Texteingaben verborgen. Die Bedingungen sind ebenfalls im KI-Bausteinkatalog enthalten.

### Steigungsfilter im Editor

Die Sammelschwellen und Anlaufausnahmen stehen unter „Erweitert“. Die Anzahl 0 schaltet Anlaufausnahmen aus; erst bei positiver Anzahl werden Schwelle und Richtung angeboten. Minimale und maximale Steigung sowie die bei Fensterauswertung benötigte Fenstergröße bleiben im Grundbereich. Die gespeicherten Parameternamen ändern sich nicht.

### Parameterabschnitte und Zielerkennung

Große Bausteine gliedern ihre Eingaben in aufklappbare Abschnitte. Messwertaufnahmen verwenden Kanäle und Quelle, Grenzauswertung, Sicherheit, Zeit und Vollständigkeit, Zielerkennung sowie Ende und Exit. Gerätequelle und Wertausdrücke zeigen jeweils ihre eigenen Eingaben. Punktlimit, Messlücke, Mindestpunkte, Mindestdauer, Grenzberührung, Messversuche und JSON-Auswertungsziel stehen unter Erweitert. UDS-Diagnose und Prozessstart verwenden dieselbe Abschnittsmetadatenstruktur.

„Stabiles Ziel erkennen“ schaltet die Zielbandauswertung ausdrücklich ein. Beim Laden alter Projekte aktivieren vorhandene Zielbandgrenzen die Erkennung automatisch; unvollständige Altangaben bleiben als Fehler erkennbar. Ausschalten bewahrt vorhandene Grenzwerte, wertet sie aber nicht aus. Bei neuen Aufnahmen ist die Erkennung zunächst aus.

### Nach Aufnahmeende

Bei Aufnahme, Messpunkt und abschließender Auswertung ersetzt „Nach Aufnahmeende“ die drei bisherigen Exit-Schalter. Zur Auswahl stehen Weiter mit nächstem Schritt, Exit bei vorzeitigem Ende, Exit bei ERROR, Exit bei vorzeitigem Ende oder ERROR und Immer Exit (PASS / FAIL / ERROR). Die drei bisherigen Rohwerte bleiben lesbar; beim Laden werden sie auf die passende Auswahl abgebildet. „Immer“ hat Vorrang, andernfalls werden vorzeitiges Ende und ERROR kombiniert. Eine ausdrücklich gesetzte neue Auswahl steuert die Ausführung. Andere Bausteine behalten ihren allgemeinen ERROR-Schalter. Exit-Gruppen laufen weiterhin auch bei vorzeitigem Ende; reguläre Folgeschritte werden entsprechend der Auswahl übersprungen.

### Rohdaten und rekonstruierte Messwertverläufe

`EnvelopeRecording` liefert die bisherigen Auswertekurven für Bewertung/Statistik und hängt ein transientes `TraceRecordingData` an. `ResultRecorder` übernimmt es einmal je Ereignis. `TraceResultStorage.Collapse` reduziert erst an der Speichergrenze (TRES und SQL) auf Rohwerte, gemeinsame Achse und eingefrorene `TraceRecordingSnapshot`-Einstellungen. `Expand` rekonstruiert über dieselbe Domain-Auswertung; gespeicherte Metadaten werden anschließend wieder eingesetzt. Eine Abweichung überschreibt keine Bewertung. Auswerteänderungen, die dieselben Rohdaten anders bewerten, müssen `CurrentAlgorithmVersion` erhöhen.

Format 2 verwendet vorhandene WAVE-/SQL-Felder: `SyncGroup=TraceRaw:1:<event>`, Zeitdaten nur am ersten Rohkanal, Snapshot/gespeicherte Bewertungen nur in dessen erstem Chunk. Fremde und ältere Kurven durchlaufen Collapse/Expand unverändert. Einzelheiten und eine numerische SQL-Abfrage: [Messwertverläufe](../messwertverlaeufe.md#rohdatenformat-und-anzeige-ergebnisformat-2).

`WaveformPlotViewModel` bildet je Einheit einen Unterbereich mit synchronisiertem Zeitbereich/Cursor. Nur die Anzeige interpoliert auf ein gemeinsames Raster; diese Werte werden nie zurückgeschrieben. Ergebnisse und Prüfplatzansichten verwenden dieselbe WPF-Komponente und Inhaltsfilter.
