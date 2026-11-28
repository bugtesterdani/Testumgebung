# Optionale Wartemusik bei Werkerinteraktionen

Im Baustein **Werkerinteraktion** kann unter **Audiodatei (optional)** eine externe MP3-/WAV-Datei angegeben werden. **Auf Antwort warten** muss eingeschaltet sein. Ohne Audiodatei bleibt das bisherige Verhalten unverändert.

Beispiele für das Feld `Audio`:

- Laufwerk: `C:\Pruefplatz\Audio\warten.mp3`
- Netzfreigabe: `\\server\pruefung\audio\warten.wav`
- Webadresse: `https://example.org/audio/warten.mp3`
- Download-Endpunkt: `https://example.org/audio/download?id=123`

Platzhalter wie `{AudioDatei}` werden mit den aktuellen Variablen des Schritts ersetzt. Die Datei beziehungsweise Webadresse muss für den angemeldeten Windows-Benutzer erreichbar sein. Webadressen müssen direkt auf abspielbare Audiodaten verweisen; eine Anmeldeseite ist keine Audiodatei. Es gibt keinen separaten Dialog für Zugangsdaten oder eigene HTTP-Header.

## Dauer und Fehlerbehandlung

Die Desktop-Anwendung lädt die externe Quelle beim Beginn des Wartens asynchron und spielt sie wiederholt ab. Das gilt für Anweisungen, Bildbestätigungen, Eingaben und Auswahlfelder. Die Wiedergabe bleibt auch beim Wechsel der Ansicht oder des ausgewählten Prüfplatzes an die wartende Aufforderung gebunden.

Eine gültige Bestätigung, Eingabe oder Auswahl beendet die Musik, bevor der Ablauf fortgesetzt wird. Bei einer ungültigen Eingabe läuft sie weiter. Abbruch, Gruppenende und Aufräumen des Ablaufs beenden auch einen noch ausstehenden Ladevorgang. Mehrere Prüfplätze besitzen voneinander unabhängige Wiedergaben.

Bei **Auf Antwort warten = aus** oder bei einer bereits durch Auftragsparameter beantworteten Abfrage wird keine Wartemusik gestartet. Eine nicht erreichbare oder nicht abspielbare Datei erzeugt einen Audiohinweis an der Aufforderung; die Eingabe bleibt bedienbar. Nach einem Wiedergabefehler erfolgt kein automatischer Wiederholungsversuch. Die Audioausgabe verwendet die Windows-Medienunterstützung und die aktuell eingestellte Systemlautstärke.

## Speicherung und Übertragung

**Projektdatei, Datenbank und Freigabepaket enthalten ausschließlich die externe Referenz, niemals die Audiodatei.** Die Anwendung bietet für diesen Parameter keinen Import eingebetteter Medien an. `data:`-Adressen und relative Dateipfade werden nicht abgespielt.

Die Musikdatei muss separat auf den Prüfplätzen, der Netzfreigabe oder dem Webserver bereitgestellt werden. Sie gehört nicht in ein versioniertes Git-/SVN-Programmquellarchiv, da solche Quellarchive unabhängig vom Audio-Parameter als Ganzes zur Freigabe gespeichert werden. Beim Start wird die Quelle direkt von dort geöffnet; es gibt keine eigene dauerhafte Medienablage der Testumgebung. Eine Änderung der Audiodatei außerhalb der Anwendung ändert das vorhandene Freigabepaket nicht. Der dort gespeicherte Pfad beziehungsweise die Webadresse bleibt Teil der Programmdefinition.
