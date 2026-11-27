# Release R10: 1.4.1

Release-Planung: [Issue #272](https://github.com/bugtesterdani/Testumgebung/issues/272),
Meilenstein **Release R10**. Gewählte Softwareversion: **1.4.1**.
Die vierte Paketversionsstelle vergibt der bestehende MSIX-Workflow fortlaufend.

## Abgeschlossener Lieferumfang

- #274: cab-Quelltext zeigt vollständige, horizontal scrollbar bleibende Zeilen.
- #276: Zeilennummern des Codeeditors behandeln fehlendes Textlayout und ungültige
  Zeilenindizes; der Clip wird auch bei frühem Rücksprung ausgeglichen.
- #278: Neue versionierte SQL-Releases speichern feste Quellreferenzen und
  Prüfsummen statt eingebetteter Projektdateien und Quellarchive. Das Laden
  prüft die exakte Revision oder einen lokalen Cache gegen das Release-Manifest.

Diese Änderungen sind in `main` gemergt. Vorhandene eingebettete Releases bleiben
lesbar. Bestehende Datenbankpakete werden nicht automatisch umgeschrieben.

## Betrieb und Kompatibilität

Für neue SQL-Quellreferenzen benötigen Produktivplätze beim ersten Laden einen
passenden Git-/SVN-Client und Repository-Lesezugriff, sofern kein gültiger lokaler
Cache vorhanden ist. Alle beteiligten Plätze müssen das neue Referenzformat
unterstützen. Details stehen unter [Versionierte Prüfprogramme](versionierte-pruefprogramme.md).

## Release-Nachweise

Der Abschluss-PR schließt #272 und bestätigt die Zielversion 1.4.1. Seine CI muss
vor dem normalen Merge erfolgreich sein. Der anschließende Main-Lauf setzt den
Tag **v1.4.1** am Release-Merge-Commit und baut die offiziellen Pakete sowie den
Release-Entwurf. Diese Vorbereitung behauptet keinen abgeschlossenen offiziellen
Paket-Build und veröffentlicht kein Release.

Paket, Dokumentation und Lizenznachweise werden nach dem erfolgreichen Build
geprüft. Maßgeblich sind der [Release-Ablauf](release-ablauf.md) und die
[Veröffentlichungsunterlagen](oeffentliche-veroeffentlichung.md).
