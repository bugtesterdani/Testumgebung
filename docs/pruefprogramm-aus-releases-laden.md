# Prüfprogramm aus Releases in den Ablaufeditor laden

## Vorgehen

1. Mit einem Konto mit dem Recht **Entwickeln** anmelden.
2. **Releases** öffnen und unter **Testprogramme** das gewünschte Programm auswählen.
3. In der Versionsliste die gewünschte Version auswählen. Entwürfe, geprüfte Versionen, Freigaben und archivierte Versionen lassen sich zur Bearbeitung öffnen.
4. **In Ablaufeditor laden** wählen. Das Paket wird vor dem Öffnen auf Unversehrtheit geprüft.
5. Bei ungespeicherter Editorarbeit **Ja** zum Speichern, **Nein** zum ausdrücklichen Verwerfen oder **Abbrechen** zum Beibehalten des bisherigen Projekts wählen. Wird die Speicherdateiauswahl abgebrochen oder schlägt Speichern fehl, bleibt das bisherige Editorprojekt erhalten.
6. Nach erfolgreichem Laden öffnet sich der **Ablaufeditor** mit der gewählten Programmfassung. Ablauf, Variablen, Bausteine, Kurven und weitere Projektbestandteile stammen aus dieser Version.

Die geladene Fassung ist eine **lokale Bearbeitungskopie** ohne festgelegten Dateipfad. Beim Speichern einen lokalen Projektdateipfad wählen. Änderungen und neue Releases werden erst durch die üblichen nachfolgenden Schritte gespeichert beziehungsweise freigegeben. Das ursprüngliche Release-Paket wird durch Laden oder Bearbeiten nicht verändert.

## Programme ohne gespeicherte Version

Ist noch keine Paketversion vorhanden, aber ein lokaler Editor-/Beispielentwurf für das Programm verfügbar, öffnet dieselbe Aktion eine Kopie dieses Entwurfs. Fehlen sowohl Paket als auch lokaler Entwurf, ist das Laden deaktiviert; dann zunächst eine Projektquelle öffnen oder einen Entwurf bereitstellen.

## Rechte und laufender Betrieb

Das Recht **Freigeben** allein erlaubt keine Editorbearbeitung. Die Option **Entwurf testen** wird beim Laden nicht automatisch aktiviert. Laufende Prüfungen behalten ihre gestartete Projektkopie. Gespeicherte Freigaben und zentrale Prüfplatzzuteilungen werden nicht geändert.

Wechselt während des Ladens die Anmeldung, das gewählte Programm/die Version oder das bereits geöffnete Editorprojekt, wird das Ergebnis nicht unerwartet darübergeladen. Die Statusmeldung erklärt, dass erneut geladen werden muss. Beschädigte oder nicht lesbare Pakete lassen das bisherige Editorprojekt bestehen.

Auch ältere Pakete dürfen zur Überarbeitung geöffnet werden, wenn ihr Inhalt unversehrt und ihr Projektformat noch lesbar ist. Die Bearbeitung ist keine Bestätigung für die Ausführbarkeit mit der heutigen Laufzeit: Vor einer neuen Freigabe die Entwicklungsprüfung und die üblichen Tests durchführen.

Bei Git-/SVN-Projekten die Bearbeitungskopie im vorgesehenen Arbeitsordner speichern und über den üblichen Commit-/Remote-Freigabeablauf weiterführen.
