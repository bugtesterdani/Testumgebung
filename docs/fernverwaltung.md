# Prüfplatz-Fernverwaltung

`Testumgebung.RemoteAdmin.exe` ist eine separate Windows-Anwendung. Sie läuft auch auf einem anderen Rechner parallel zur Testumgebung und verbindet sich direkt mit derselben SQL-Datenbank wie die Prüfprotokolle. Die Entra-Anmeldung öffnet den Systembrowser. Für die Fernverwaltung sind weder ein Webdienst noch der lokale Entra-Broker erforderlich.

Die Oberfläche zeigt den letzten gemeldeten Stand der verbundenen Prüfplatzrechner. Änderbar sind die gemeinsame Prüfplatzanzeige und die Alarmauslöser. Ein Prüfplatz holt die Änderung über seine vorhandene Datenbankverbindung ab und übernimmt sie im Leerlauf. Der Testbetrieb verwendet bei einer unterbrochenen Verbindung weiterhin die zuletzt gespeicherten lokalen Einstellungen.

## Windows-Anwendung einrichten

1. Eine Entra-App als öffentlichen Desktopclient im gewünschten Mandanten registrieren. Den in `zugang.json` eingetragenen Loopback-Redirect (standardmäßig `http://localhost`) unter **Mobile und Desktopanwendungen** hinterlegen. Die Entra-App-Rolle `Testumgebung.Fernverwaltung` den vorgesehenen Personen oder Gruppen zuweisen. Die Rolle gilt für alle Prüfplätze, die das Datenbankkonto lesen kann.
2. Mandanten- und Anwendungs-ID in `%ProgramData%\Testumgebung\zugang.json` eintragen. Für eine getrennte Registrierung kann `TESTUMGEBUNG_REMOTE_ACCESS` auf eine eigene Zugangsdatei zeigen. Die Datei enthält für die Anwendung die Felder `tenantId`, `clientId` und optional `entraRedirectUri`.
3. `Testumgebung.RemoteAdmin.exe` öffnen, bei Entra anmelden und dieselbe Datenbank wie an den Prüfplätzen auswählen. Auf einem Rechner mit bereits eingerichteter Testumgebung wird dessen Verbindung beim ersten Start vorgeschlagen. Die Fernverwaltung speichert ihre Verbindung separat im Windows-Benutzerprofil und schützt sie mit DPAPI.
4. An jedem beteiligten Prüfplatz `TESTUMGEBUNG_REMOTE_ID` im Windows-Kontext der Prüfplatzanwendung auf eine eindeutige, dauerhaft verwendete Rechner-ID setzen. Erlaubt sind maximal 64 Buchstaben, Ziffern, Bindestriche oder Unterstriche. Danach die Prüfplatzanwendung neu starten. Eine ID kennzeichnet eine laufende Installation und deren lokale Prüfplätze. Nach der ersten Meldung den Prüfplatz in der Fernverwaltung über **Prüfplätze aktualisieren** laden.

Das Release-ZIP **Testumgebung-Fernverwaltung** enthält die Anwendung. Für das ZIP werden Windows x64 und die .NET-Desktop-Runtime 10 benötigt. Aus dem Quellcode erzeugen:

```powershell
dotnet publish src/Testumgebung.RemoteAdmin -c Release -r win-x64 --no-self-contained -o artifacts/remote-admin
```

## Datenbank und Protokoll

Die Tabelle `tu_station_settings` liegt in derselben Datenbank wie die Prüfprotokolle. Die Anwendung legt die Tabelle bei der ersten Nutzung an, sofern das verwendete Datenbankkonto sie anlegen darf. Anschließend benötigen die Anwendungen Zugriff auf die Tabelle. Für verschiedene Rechner eine gemeinsam erreichbare SQL-Server-, PostgreSQL- oder MySQL-Datenbank verwenden. SQLite eignet sich für einen lokalen Test auf einem Rechner.

Die Tabelle enthält je Rechner den gemeldeten Konfigurationsstand und die einzelnen Änderungsrevisionen mit Entra-Anmeldename, Objekt-ID, UTC-Zeit, Vorher-/Nachherwerten und Ergebnis der Übernahme. Die Anwendung meldet zwischenzeitliche lokale Änderungen als Konflikt, damit sie nicht still überschrieben werden. Pro Rechner kann eine Änderung ausstehen.

Die Entra-Rolle schützt den Zugang zur Windows-Oberfläche. Direkte Datenbankzugriffe richten sich nach den Berechtigungen der verwendeten Datenbankkonten. Verteilen Sie keine gemeinsam genutzten administrativen Datenbankzugänge, wenn der Änderungsverlauf einzelnen Personen zugeordnet werden soll. Direkte Tabellenänderungen außerhalb der Anwendungen sind von deren Protokollierung unabhängig.

Laufzeitpfade, Versandziele, Zugangsdaten und Prüfprogramme werden über die Fernverwaltung nicht übertragen. Start und Stopp eines Prüflaufs bleiben am Prüfplatz.
