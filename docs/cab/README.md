# cab-JScript-Katalog – Prüfstand AP 0

Grundlage: cab JScript Programming Manual 07/2026, Firmware 5.48.

Der Katalog wird schrittweise gegen das Handbuch geprüft. `geprueft: false`
bleibt bis zur tatsächlichen Prüfung der jeweiligen Handbuchseiten erhalten.
Diese Zwischenfassung begründet keine vollständige Unterstützung im
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
  Werte, Generationen, Bereiche und Standardwerte ergänzt. Ax/X3 bleiben
  ausdrücklich unbelegt. Widersprüche bei `P:pos` und beim Trennzeichen vor
  `E:power` stehen im Kataloghinweis.
- JS-4.9, Seiten 199–201: mehrzeilige GOODBAD-/EXTERN-Parameter rekonstruiert,
  Feldname und Bildname sowie Prüfpositionsparameter und Autoload getrennt;
  Vergrößerung, Drehungen und Zeitlimits ergänzt.
- JS-4.2.41, Seiten 158–160 und Übersicht S. 82: QR-Modell, Fehlerkorrektur und
  alle 40 Versionen samt Modulmaßen ergänzt. Fehlende Generationstabellen für
  einzelne Optionen und widersprüchliche Kurzangaben zu Daten sind vermerkt.
- JS-4.12, Seiten 212–217: Druckoptionen und Generationen abgeglichen,
  Syntax-Artefakte entfernt, Abreißversatz und optionale X4-Pufferzahl ergänzt;
  Reihenfolge, Hardwarevoraussetzungen und modellspezifische Angaben vermerkt.

## Strukturprüfung

`build/test-cab-catalog-schema.ps1` prüft das JSON-Schema und negative Fälle
(fehlende ID, ungültige Generation, Parameterquelle und Handbuchseite).
`CabJScriptCatalogSchemaTests` führt diese Prüfung in der Testsuite aus und
prüft zusätzlich eindeutige IDs, Seitenintervalle und berechnete Statistik.

Vor Abschluss von AP 0 sind alle Einträge fachlich zu prüfen, die bekannten
Extraktionsfehler zu korrigieren. Noch 185 Einträge sind fachlich ungeprüft;
die vollständige fachliche Prüfung steht weiterhin aus. Das lokale
Handbuch-PDF und daraus extrahierte Inhalte bleiben außerhalb der versionierten
Dateien. Vor Abschluss müssen alle Einträge geprüft und die Dokumentation
einschließlich des generierten Bausteinkatalogs abgeglichen sein.

Die folgenden AP beginnen erst nach erfolgreichem Build, Tests, CI, Review und
Merge von AP 0. Issue: #253; Release: R9, Milestone 18.
