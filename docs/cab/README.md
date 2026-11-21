# cab-JScript-Katalog – Prüfstand AP 0

Grundlage: cab JScript Programming Manual 07/2026, Firmware 5.48.

Alle 237 Einträge wurden gegen die jeweiligen Handbuchseiten geprüft.
Die im Ausgangskatalog fehlenden Abschnitte JS-1.4 und JS-4.2.1 sind ergänzt;
JS-5.83 existiert im Handbuch nicht. Das Schema verlangt `geprueft: true`.
Die Katalogprüfung begründet keine vollständige Befehlsunterstützung im
Etiketteneditor. Eine bestandene Strukturprüfung ersetzt keine Fachprüfung.

## Tatsächlich geprüfte Abschnitte

- JS-1.3, Seite 8: allgemeine Syntaxregeln abgeglichen.
- JS-3.1 bis JS-3.3, Seiten 39–46: Dump, Direktschnitt und drei
  Downloadvarianten abgeglichen; Formatgenerationen, Dateiname, Helligkeit und
  ASC-Regeln ergänzt. Widersprüche bei Methodenanzahl, Objektgrenze und
  ESC-Auswertung sind als Hinweise dokumentiert.
- JS-3.4 bis JS-3.10, Seiten 47–54: tatsächliche Löschtypen anstelle von
  Überschriftenwerten eingetragen und die zweite Tabellenspalte der Ländercodes
  ergänzt. Pausen-, Einheiten- und Auftragskennungsregeln abgeglichen.
- JS-3.11 bis JS-3.17, Seiten 55–70: Abfragetypen und Antwortfelder ergänzt,
  Datumskomponenten getrennt, optionale Selbsttest-Hardware gekennzeichnet und
  Nulldarstellung abgeglichen. Die falsche letzte Seite von JS-3.17 ist korrigiert.
- JS-2.1 bis JS-2.10, Seiten 11–20: ESC-Syntax, Generationen, Binärdaten und
  Antwortfelder abgeglichen; fehlende Syntax von ESCc sowie Antwortstufen von
  ESC? ergänzt. Antwortfelder sind ausdrücklich keine Eingabeparameter.
- JS-2.11 bis JS-2.18, Seiten 21–28: Codepage-Werteliste und fehlende
  Antwortfelder ergänzt; Gültigkeit der Codepage laut Handbuch vor dem nächsten
  Auftrag vermerkt. Pause und Prüferantworten abgeglichen.
- JS-2.19 bis JS-2.25, Seiten 29–37: Status- und I/O-Tabellen einschließlich
  Fortsetzungen abgeglichen. Fehlende Werte (`G`, Online- und Interpreterwerte)
  und Generationen ergänzt; die leere Ax-Zelle bei RFID-Fehler `r` bleibt
  unbekannt. ESCxin-Auswahl und ESCv-Prüferwerte vervollständigt.
- JS-1.5 und JS-1.6, Seite 9: Pfadgenerationen und Objektgrenzen anhand der
  gerenderten Tabelle geprüft. `card` ist in allen Generationen verfügbar;
  fehlende Ax-Grenzen bedeuten keine numerische Nullgrenze.
- JS-1.4, Seite 8: fehlende Befehlsarten ergänzt. Das Handbuch nennt vier Arten,
  zählt aber zusätzlich Sonstiges auf.
- JS-4.2.1, Seiten 76–80: fehlende Inhaltsverzeichnisposition ergänzt und auf
  die vorhandene gemeinsame B-Definition JS-4.2 verwiesen. Die pauschale
  Barcodegrenze auf Seite 76 widerspricht der Tabelle auf Seite 9; der Hinweis
  dokumentiert diese Abweichung. Zusammen mit JS-1.4 enthält der Katalog jetzt
  237 Abschnitte statt der 235 Abschnitte der Extraktion. Ein lokaler Abgleich
  der Abschnittsnummern mit dem Inhaltsverzeichnis ergab keine weitere Lücke.
- JS-5.97, Seiten 344–346: Seriennummer. Die Syntax nennt `start`, die Tabelle
  nennt `x`. Der Parameter heißt nach Tabelle `x`; die Originalschreibweise
  `start` bleibt in der Syntax und im Hinweis erhalten. Standardwerte für
  Schrittweite und Wiederholungszahl sind jeweils 1.
- JS-4.5.4, Seiten 179–181: RFID-Parameter für X2/X4 getrennt beschrieben,
  Werte, Generationen, Bereiche und Standardwerte ergänzt. Die E-Übersicht
  auf Seite 174 schließt Ax/X3 ausdrücklich aus. Widersprüche bei `P:pos` und beim Trennzeichen vor
  `E:power` stehen im Kataloghinweis.
- JS-4.9, Seiten 199–201: mehrzeilige GOODBAD-/EXTERN-Parameter rekonstruiert,
  Feldname und Bildname sowie Prüfpositionsparameter und Autoload getrennt;
  Vergrößerung, Drehungen und Zeitlimits ergänzt.
- JS-4.2.41, Seiten 158–160 und Übersicht S. 82: QR-Modell, Fehlerkorrektur und
  alle 40 Versionen samt Modulmaßen ergänzt. Fehlende Generationstabellen für
  einzelne Optionen und widersprüchliche Kurzangaben zu Daten sind vermerkt.
- JS-4.2.2 bis JS-4.2.6, Seiten 81–90: Übersicht als Information statt eigene
  Symbologie erfasst; 2-of-5, Add-on 2/5 und Aztec abgeglichen. Fehlende große
  Schreibweise von 2-of-5 ergänzt, Größen und Fehlerkorrektur vervollständigt.
- JS-4.2.7 bis JS-4.2.11, Seiten 91–100: Codabar, Codablock F und Code
  39/93/128 abgeglichen. Mehrzeilige Zeichenvorräte, die fehlende große
  Codablock-Schreibweise und Code-128-Subset-/Funktionswerte ergänzt.
- JS-4.2.12 bis JS-4.2.14, Seiten 101–108: alle DataMatrix-Kapazitätstabellen
  samt ROWS-/COLS-Auswahl übernommen, DBP-Mindesthöhe und Dotcode-Zeichenvorrat
  vervollständigt; fragliche Tabellenüberschriften und Prüfzifferangaben vermerkt.
- JS-4.2.15 bis JS-4.2.19, Seiten 109–117: EAN/JAN-Eingabelängen von den
  Symbolstellen unterschieden, GS1-Aliasse und FIM-Werte vervollständigt und
  SSCC-18 als Verweis ohne erfundenen eigenen Befehl abgeglichen.
- JS-4.2.20 bis JS-4.2.26, Seiten 118–131: mehrzeilige RSS-Typnamen korrekt
  zusammengesetzt, Composite-Parameter ergänzt sowie GTIN-Eingabe und lineare
  Symbolstellen von den 2D-Zusatzdaten unterschieden.
- JS-4.2.27 bis JS-4.2.30, Seiten 132–139: GS1-Matrix-/QR-Varianten und HIBC
  abgeglichen. Modell-/Versionswerte ergänzt; fehlende GS1-rMQR-Generationen
  sowie die in der GS1-QR-Syntax nicht vorkommenden Tabellenfelder vermerkt.
- JS-4.2.31 bis JS-4.2.36, Seiten 140–150: ISBT-/Maxicode-Modi, ITF-Verweis,
  Micro-PDF-Spalten und Micro-QR-Kapazitäten ergänzt. Die fehlerhafte M3-
  Tabellenzuordnung und widersprüchliche Kurzkapazitäten ausdrücklich vermerkt.
- JS-4.2.37 bis JS-4.2.40, Seiten 151–157: PDF417-Fehlerlevel, Plessey,
  Postnet und PZN-Verweis abgeglichen; den Widerspruch EL0/EL1…8 dokumentiert.
- JS-4.2.42 bis JS-4.2.45, Seiten 161–169: beide rMQR-Versionstabellen mit
  64 Kapazitätszeilen visuell abgeglichen; UPC-Eingabelängen, Schreibweisen und
  Nullunterdrückung ergänzt. Fragliche rMQR-Kapazität der Version 13/M vermerkt.
- JS-4.1 und JS-4.2, Seiten 72–80: Mengen-/Vorschausteuerung und allgemeine
  Barcodeparameter abgeglichen. Zerlegte Prüfoptionen zusammengeführt,
  besondere Barcodefunktionen und benannte Steuerzeichen ergänzt.
- JS-4.3 bis JS-4.5, Seiten 170–174: Schnittmodi und -grenzen, globale
  Versätze und Erweiterungsauswahl abgeglichen. RFID-Generationen durch die
  übergeordnete E-Tabelle geklärt.
- JS-4.5.1–3, JS-4.5.5–7 und JS-4.6, Seiten 175–178/182–186: weitere
  Erweiterungen und Schriftaliasse abgeglichen. Kopierte/falsch benannte
  OPC-UA-Felder, doppelte SQLite-Endung und abweichende Downloadbezeichnung
  geklärt beziehungsweise als Handbuchwiderspruch vermerkt.
- JS-4.7 und JS-4.7.1–3, Seiten 187–195: alle Grafik- und Fülloptionen
  ergänzt. Unbeschriebene vierte Kreiszahl, gebrochene Beispieldrehungen und
  die Abweichung Linienstärken/Eckenradien zum Arbeitsauftrag dokumentiert.
- JS-4.8 und JS-4.10–4.11.7, Seiten 196–198/202–211: Druckkopf-/Farbband-
  Optionen, Auftragsstart und alle Speicherbefehle abgeglichen; fehlende
  Medientabelle, Ladeformate und Aufzeichnungsabschluss ergänzt.
- JS-4.13 bis JS-4.15, Seiten 218–223: Spenden, gepufferter Feldersatz und
  alle Etikettensensoren abgeglichen. Mehrkopfbedingungen und den Widerspruch
  der O-Reihenfolge zwischen Vorgabe und Beispielen dokumentiert.
- JS-4.16, Seiten 224–232: Fonts, Größenvarianten und alle Effekte
  abgeglichen; Basislinie und vertikale Zeichenanordnung anhand der Bilder
  geprüft. Unklare Generationen und widersprüchliches OCR-Beispiel vermerkt.
- JS-4.17–4.18, Seiten 233–240: Rich-Text-Größen, HTML-Tags und in Beispielen
  verwendete Attribute ergänzt; mehrzeilige Inhalte, Wrapper-/Einheitenkonflikt
  und getrennte AND-/OR-Masken des Positionssignals dokumentiert.
- JS-5.108 und JS-6.1–4, Seiten 358–362: User-Speichergrenze, Kommentare,
  abc und verschlüsselte Rahmen abgeglichen. Mainboardnummer vom falschen
  Extraktionsnamen key getrennt und unbekannte Payload-Grenze vermerkt.
- JS-5.1 bis JS-5.10, Seiten 241–253: Zeit-/Datumsversätze und Referenznamen
  abgeglichen. Ausgabebereiche von Eingabeversätzen getrennt, feste 30-Tage-
  Monate und unklare Prosa-Aliasse ausdrücklich dokumentiert.
- JS-5.11 bis JS-5.33, Seiten 254–276: Datum, Wochen, Namen und erste
  Jalali-Felder abgeglichen; ISO-Datumsformat anhand der Abbildung geprüft.
  Ausgabewerte sind von Eingaben getrennt. Die Monatsberechnung im Beispielbild
  widerspricht der pauschalen 30-Tage-Regel; beide Belege sind vermerkt.
- JS-5.34 bis JS-5.45, Seiten 277–288: weitere Kalenderfelder, Rechen-
  Operanden und variadische Werte abgeglichen; zwei Nachkommastellen als
  Ausgabestandard erfasst. Kopierter Divisionstext und AND/Gleichheit-Konflikt
  sind ausdrücklich gekennzeichnet.
- JS-5.46 bis JS-5.57, Seiten 289–300: Vergleiche, Prüfalphabete, Preis-
  und Rundungsparameter sowie erste RFID-Felder abgeglichen. Zusammengesetzte
  Preiszeichen statt falscher Kommaargumente und kopierte Beschreibungen korrigiert.
- JS-5.58 bis JS-5.66, Seiten 301–312: RFID-Zugriffe, vollständige Sperrwerte
  und interaktive Masken abgeglichen. WTAG-Tippfehler, ausgelassene Dialog-
  Positionen und mehrstellige Masken aus den Beispielen erfasst.
- JS-5.67 bis JS-5.77, Seiten 313–324: abc-/Basisumwandlung, alle Binärbreiten,
  Bitfeldlisten und Füllzeichen abgeglichen. Benutzeralphabet, Titelartefakt und
  doppelte Syntaxkommas korrigiert; rechte Bitauffüllung visuell bestätigt.
- JS-5.78 bis JS-5.108, Seiten 325–358 (ohne 5.83): Formatierung, Referenzen,
  Teilstrings, OPC-UA, Datenbankzugriffe, Speicher und Zeichenoperationen
  abgeglichen. HEX-Bytekonvertierung, GS-Trenner, Unicode-Steuerzeichen und
  Druckzeitverarbeitung erfasst. Bedingte Unsichtbarkeit widerspricht dem
  Beispiel; der Konflikt bleibt ausdrücklich dokumentiert. E OPCUA anhand der
  späteren Verbindungsbeispiele ergänzt, kopierte Nichtparameter entfernt.
- JS-4.12, Seiten 212–217: Druckoptionen und Generationen abgeglichen,
  Syntax-Artefakte entfernt, Abreißversatz und optionale X4-Pufferzahl ergänzt;
  Reihenfolge, Hardwarevoraussetzungen und modellspezifische Angaben vermerkt.

## Strukturprüfung

`build/test-cab-catalog-schema.ps1` prüft das JSON-Schema und negative Fälle
(fehlende ID, ungültige Generation, Parameterquelle und Handbuchseite,
leere Parameterbeschreibung und ungeprüfter Eintrag).
`CabJScriptCatalogSchemaTests` führt diese Prüfung in der Testsuite aus und
prüft zusätzlich eindeutige IDs, Seitenintervalle und berechnete Statistik.

Die fachliche Prüfung aller Einträge ist abgeschlossen; bekannte
Extraktionsfehler sind korrigiert. Nicht eindeutig belegte Regeln und
Widersprüche sind als Hinweise gekennzeichnet. Das lokale Handbuch-PDF und
daraus extrahierte Inhalte bleiben außerhalb der versionierten Dateien.
Der generierte Bausteinkatalog beschreibt die tatsächlich vorhandenen
Anwendungsbausteine; AP 0 ergänzt noch keine neue Editorfunktion.

`statistik.werte` zählt die Parameter-Wertelisten. Getrennte Ausgabe-,
Kapazitäts- und Medientabellen werden darin nicht als Eingabewerte mitgezählt.

Die folgenden AP beginnen erst nach erfolgreichem Build, Tests, CI, Review und
Merge von AP 0. Issue: #253; Release: R9, Milestone 18.
