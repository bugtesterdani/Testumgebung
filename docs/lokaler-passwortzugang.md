# Optionaler lokaler Benutzerlogin mit Passwort

Der lokale Passwortzugang ist ein freiwilliger Übergang, bis Microsoft Entra ID eingerichtet ist. Er verwendet dieselbe Benutzerliste und Rechteverwaltung wie der bisherige Zugang. Werker können die Prüfung weiterhin ohne Anmeldung bedienen; eine freiwillige Anmeldung ohne Entwickler-/Freigaberecht gewährt keine zusätzlichen Rechte.

## Erstmalig einrichten

1. Oben **Anmelden** öffnen. Ohne eingerichtetes Entra zunächst den gewünschten Namen eingeben und bewusst **Lokal anmelden (Entwicklungsmodus)** wählen.
2. Unter **Einstellungen → Zugang** die Benutzerliste bearbeiten. Das aktuell angemeldete Entwicklerkonto beibehalten, damit der Zugang zu den Einstellungen erhalten bleibt.
3. Für jedes Konto **Setzen / ändern** in der Passwortspalte wählen. Das Passwort zweimal eingeben und übernehmen. Die Liste zeigt nur „gesetzt“ beziehungsweise „nicht gesetzt“.
4. **Lokalen Benutzer-/Passwortzugang als Übergang aktivieren** anhaken. Die Rechtequelle muss **Lokal** sein; mindestens ein Konto braucht **Entwickeln**. Vor dem Aktivieren muss jedes Konto ein Passwort haben.
5. **Zugang speichern** wählen. Erst jetzt werden die Änderungen dauerhaft übernommen. Beim Wechsel der Anmeldeart erfolgt eine Abmeldung.
6. Im Anmeldepanel Benutzername und Passwort eingeben und **Anmelden** oder Enter betätigen.

Kurze lokale Namen wie `Daniel` werden beim Speichern wie bisher zu `daniel@simuliert.local` normalisiert. Bei der Anmeldung sind der kurze Name und dieser vollständige lokale Anmeldename möglich. Groß-/Kleinschreibung des Benutzernamens spielt keine Rolle; das Passwort wird exakt einschließlich Leerzeichen geprüft. Es gibt keine voreingestellten Passwörter.

## Passwörter ändern und Konten wechseln

Berechtigte Entwickler können unter **Zugang** ein neues Passwort setzen. Das bisherige Passwort wird nicht angezeigt oder zurückgelesen. Abbrechen verwirft die Eingabe. Änderungen werden erst mit **Zugang speichern** wirksam. Wird das Passwort des aktuell angemeldeten Kontos geändert, muss es sich anschließend mit dem neuen Passwort erneut anmelden.

**Konto wechseln** meldet lokal zuerst ab. Danach das Anmeldepanel wieder öffnen und die Zugangsdaten des anderen Kontos eingeben. Abmelden und die bestehende Inaktivitätssperre entfernen die Benutzerrechte; laufende Prüfungen werden dadurch weiterhin nicht angehalten oder abgebrochen.

Das Passwortfeld ist verdeckt und wird nach dem Anmeldeversuch geleert. Die Anwendung legt das eingegebene Passwort nicht in der Zugangsdatei, im ViewModel, in Audit-Einträgen oder Protokollmeldungen ab.

## Speicherung und Einordnung

In `%ProgramData%/Testumgebung/zugang.json` stehen nur die Passworthashes je Konto (`passwordHash`) sowie die ausdrückliche Aktivierung (`allowLocalPasswords`). Verwendet wird PBKDF2-SHA256 mit zufälligem 16-Byte-Salt pro Passwort, 600.000 Iterationen und einem 32-Byte-Ergebnis. Die vorhandene Zugangsdatei bleibt lesbar; zusätzliche NuGet-Pakete sind nicht erforderlich.

Die Hashes werden nicht zusätzlich verschlüsselt. Wer die Zugangsdatei ändern darf, kann auch die lokalen Konten und Rechte ändern. Der lokale Übergangszugang ist deshalb keine zentral bestätigte Firmenidentität. Die Benutzeranzeige, das Audit und die Release-Ansicht unterscheiden ihn von Entra und von der passwortlosen Simulation.

Bei aktiviertem Passwortzugang kann die namenbasierte Entwicklungsanmeldung auch mit `--development` nicht zur Umgehung verwendet werden. Unbekannter Benutzer und falsches Passwort ergeben dieselbe verständliche Fehlermeldung. Fehlende beziehungsweise beschädigte Hashes gewähren keinen Zugang.

## Auf Entra umstellen oder deaktivieren

Sobald Client-ID und Mandant vollständig eingerichtet sind, hat **Entra Vorrang**. Ein fehlgeschlagener Entra-Login fällt nicht auf ein lokales Passwort zurück. Vor der Umstellung die tatsächlichen Firmen-UPNs beziehungsweise App-Rollen passend konfigurieren; die lokalen Passwörter sind keine Firmenpasswörter.

Wird der lokale Passwortzugang ohne Entra ausdrücklich ausgeschaltet und gespeichert, steht wieder der bisherige, ausdrücklich zu aktivierende lokale Entwicklungszugang zur Verfügung. Es erfolgt ein neuer Anmeldevorgang. Die Benutzerliste und vorhandene Hashes bleiben erhalten, sodass der Passwortzugang später erneut aktiviert werden kann.

Bei vergessenem Passwort kann ein anderes berechtigtes Entwicklerkonto es neu setzen. Falls kein solches Konto mehr zugänglich ist, kann ein Windows-Administrator die Zugangsdatei nach Sicherung verwalten. Es gibt keinen versteckten universellen Wiederherstellungscode.
