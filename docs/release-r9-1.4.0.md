# Release R9: 1.4.0

Release-Planung: [Issue #240](https://github.com/bugtesterdani/Testumgebung/issues/240),
Meilenstein **Release R9**. Gewählte Softwareversion: **1.4.0**.
Die vierte Paketversionsstelle vergibt der bestehende MSIX-Workflow fortlaufend.

## Abgeschlossener Lieferumfang

Die cab-JScript-Arbeitspakete wurden sequenziell über normale Merge-Commits
abgeschlossen: AP 0 (#254), AP 1 (#256), AP 2 (#258), AP 3 (#260), AP 8 (#262),
AP 4 (#264), AP 5 (#266), AP 6 (#268), AP 7 (#270).
Der Katalog nach Handbuch 07/2026 umfasst 237 Einträge, 1.416 Parameter und
1.801 Tabellenwerte. Die abschließende Rückstellungsliste ist leer.

Quelle, Eigenschaften, Grafik, native Inhaltsfelder, verlustfreie Dateioperationen
und Druckintegration sind im [Entwicklerhandbuch](handbuch/entwickler.md) und im
[ausführbar erzeugten Abdeckungsbericht](cab/abdeckung.md) beschrieben.
Hardwareabhängige Werte werden offline nicht ausgeführt; dokumentierte
Vorschau-Näherungen bleiben gekennzeichnet.

Der abschließende Feature-Stand wurde vollständig gebaut und getestet; die
[CI des letzten Korrekturcommits](https://github.com/bugtesterdani/Testumgebung/actions/runs/45139261333)
ist erfolgreich. Beim lokalen PDF-Nachweis wurden 287 Beispielstellen geprüft:
262 gültige und 25 dokumentierte negative Beispiele, keine unerwarteten Fehler.

## Release-Nachweise

Der Release-PR schließt #240 und bestätigt die Zielversion 1.4.0. Seine CI muss
vor dem normalen Merge erfolgreich sein. Der anschließende Main-Lauf setzt den
Tag **v1.4.0** am Release-Merge-Commit und baut die offiziellen Pakete.

Nach erfolgreichem Build werden MSIX, CLI, Fernverwaltung, Entra-Anmeldeanwendung,
KI-Paket, Dokumentation, Prüfsummen sowie passende Lizenztexte und Quellen als
Anlagen des Release-Entwurfs geprüft. Maßgeblich sind
[Release-Ablauf](release-ablauf.md) und
[Veröffentlichungsunterlagen](oeffentliche-veroeffentlichung.md).
Eine öffentliche Veröffentlichung ist ein eigener bewusster Schritt nach dieser
Prüfung; dieser Abschluss verändert keine früher veröffentlichten Pakete.
