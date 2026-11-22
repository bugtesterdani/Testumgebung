# Eigenschaften und Aufträge – AP 5

Issue #265, Release R9, Meilenstein 18. Der Entwurfs-PR wurde vor der
Implementierung aus einem ersten Dokumentationscommit angelegt.

Im cab-Etiketteneditor öffnet **JScript-Eigenschaften** die Auftragsübersicht.
Sie enthält auch nichtgrafische Befehle, ESC-Steuerungen, Kommentare und Blöcke.
Eingebettete Steuerungen gehören beim Verschieben und Entfernen zu ihrem äußeren
Befehl. Downloads und ihr Dateiblock werden gemeinsam verschoben.

Die Formulare entstehen aus den geprüften Definitionen: Zahl mit Einheit und
Bereich, beschriebene Auswahl, Text, Name, Pfad sowie kombinierbare Optionen.
**Übernehmen** ändert die Arbeitskopie und aktualisiert die Grafik. Unveränderte
Objekte behalten ihre Identität. Speichern erfolgt über die vorhandenen
Vorlagen- und Dateifunktionen. Rückgängig und Wiederholen arbeiten mit Byte-Patches.

**Nicht gesetzte Parameter zeigen** blendet fehlende Parameter ein. Die Ergänzung
wird erst übernommen, wenn die echte Grammatik den neuen Parameter an dieser
Position bindet und vorhandene Eingaben erhalten bleiben. Manche Parameter
gehören ausschließlich zu einer bestimmten Schrift, Barcodeart oder Option.
Diese Variante muss zuerst gewählt werden. Beispiele sind eigene synthetische
Formularvorlagen, keine behaupteten Drucker-Standardwerte.

Alle 237 Definitionen und ihre 1.416 Parameterschemata sind über die Definitionen
erreichbar. Antwortfelder sind ausdrücklich lesbare Druckerausgaben. Allgemeine
Kontextregeln sind Dokumentation und erzeugen keine zusätzlichen Druckerbefehle.
Nicht verfügbare Wahlwerte sind deaktiviert und mit Generation und Beschreibung
erklärt. Eine unbekannte Verfügbarkeit wird nicht als bestätigte Unterstützung
ausgegeben. Die Eigenschaftenansicht überschreibt keine undurchsichtigen Daten
mit Text.

## Reihenfolge

Hinzufügen, Entfernen und Verschieben prüfen insbesondere `J` vor den grafischen
Etikettenbefehlen und `A` am Auftragsende. Ein geladenes LBL liefert ebenfalls
einen Auftragskontext. Bereits unvollständige Dateien bleiben bearbeitbar; eine
Operation darf keine zusätzlichen Reihenfolgefehler einführen. Fehlende
Zeilenabschlüsse beim Verschieben werden als notwendige Befehlsgrenzen ergänzt.
Hinter einem offenen Binär-, ABC- oder verschlüsselten Block ist keine äußere
Einfügeposition belegt. Dort werden Anhängen und Verschieben abgelehnt; vor dem
Block bleibt das Einfügen möglich. Ein explizit geschlossener Block darf von
einem neuen Auftrag gefolgt werden.

## Dateien

Die Ressourcenauswahl enthält alle katalogisierten Downloadtypen: BMP, GIF, IMG,
MAC, PCX, PNG, TIF, ASC, TTF, DBF, SQLITE3, db, TMP und RES. Ein Download wird mit
einer ausgewählten Datei angelegt. **Datei ersetzen** tauscht den Dateiblock;
Typ, Name und benachbarte Befehle bleiben erhalten. BMP, GIF, PNG und TIF erhalten
eine native WPF-Vorschau, andere Formate eine Typ- und Größenanzeige.

ESC-maskierte, rohe und Base64-Dateiblöcke sind unterstützt. Enthält eine neue
Datei den Abschlussmarker eines rohen Blocks, wechselt nur dieser Block auf
sichere ESC-Maskierung. Eigenständige Binärbefehle beginnen mit einem leeren
gültigen Rahmen und können ebenfalls Dateidaten aufnehmen. Unvollständige
Rahmen oder eingebettete Steuerungen werden nicht als eindeutige Dateivorschau
ausgegeben. Ihr Dateiaustausch wird ebenfalls abgelehnt, damit keine
Steuerbefehle oder unbekannten Restbytes verloren gehen. Ein eindeutig
abgeschlossener Base64-Rahmen mit ungültigem Inhalt kann durch eine neue Datei
repariert werden. Verschlüsselte Inhalte bleiben geschützt.

## Inhaltsfelder

An einem Inhaltsparameter öffnet **Inhaltsfeld einfügen** alle 107 Definitionen,
gruppiert nach Zeit, Datum, Jalali, Mathematik, Prüfsummen, RFID, Daten/Datenbanken,
Text/Daten und System. Parameter werden über ihre tatsächlichen Quellbereiche
bearbeitet. Die Offline-Vorschau aus AP 3 verwendet die gewählte Beispielzeit
und eigene Beispieldaten; Hinweise auf Hardwareabhängigkeit bleiben sichtbar.
Bekannte vorherige Feldnamen des Auftrags sind als Referenzen zulässig.
Für Prüfsummen MOD10, MOD36 und MOD43 gelten dabei dieselben Referenzregeln wie
für die Inhaltsfeldgrammatik; solche Verweise bleiben direkt bearbeitbar.
Der Dialog fügt an der ausgewählten Textposition ein; anschließend übernimmt der
Parameterknopf den geänderten Inhalt. Bereits vorhandene Inhaltsfelder besitzen
auch direkt im Eigenschaftenformular ihre eigenen Parameterzeilen.

## Nachweise

Die Tests prüfen alle Parameterschemata für vier Generationen, tatsächliche
Befehlsbindungen aller 237 Protokollvorlagen und Argumentbindungen aller 107
Inhaltsfelder. Weitere Fälle prüfen gezielte Änderungen, Nachbarbytes, Codepages,
eingebettete ESC-Steuerungen, optionale Ergänzungen, Reihenfolge und Dateidaten.
WPF-Tests bedienen die Formulare, Historie, Definitionsansicht und den
Inhaltsfelddialog. Der generierte [Abdeckungsbericht](abdeckung.md) beschreibt die
Bedeutung der Spalten und die verbleibenden Rückstellungen für AP 6 und AP 7.

Vor Merge bleiben vollständiger Build, vollständige Tests, unveränderte
Qualitätsgrenzen, erfolgreiche finale GitHub-CI und erledigte Review-Threads
verbindlich. AP 6 beginnt erst nach dem normalen Merge von AP 5.
