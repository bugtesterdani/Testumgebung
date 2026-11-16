# Prüfplatz-Fernverwaltung

Die separate Webanwendung zeigt den gemeldeten Stand angeschlossener Prüfplatzrechner und bietet Änderungen an der gemeinsamen Prüfplatzanzeige und den Alarmauslösern an. Die Prüfplatzsoftware bezieht die Änderungen ausgehend über HTTPS und übernimmt sie zwischen Prüfungen und Aufträgen. Der Prüfplatz bleibt bei Dienstausfall mit seinen letzten lokalen Einstellungen betriebsfähig.

Die zentrale SQLite-Datei enthält den letzten gemeldeten Stand pro Rechner sowie jede angenommene Änderung mit Entra-Objekt-ID, UTC-Zeit und Vorher-/Nachherwerten. Der Verlauf zeigt, ob der Prüfplatz die Änderung übernommen, einen Konflikt gemeldet oder die Werte abgelehnt hat. Hat jemand am Prüfplatz seit der Anforderung dieselben Einstellungen geändert, wird der Filialstand nicht überschrieben. Pro Rechner kann eine Änderung ausstehen.

## Server einrichten

1. In einem einzelnen Entra-Mandanten eine App-Registrierung vom Typ **Web** anlegen. Die HTTPS-Rückleitung ist `https://<server>/signin-oidc`; für die Abmeldung zusätzlich `https://<server>/signout-callback-oidc` registrieren. Die App-Rolle `Testumgebung.Fernverwaltung` für Benutzer und Gruppen definieren und den vorgesehenen Personen zuweisen. Diese Rolle gilt für alle im Dienst angeschlossenen Prüfplätze.
2. Prozesskonfiguration setzen: `Entra__TenantId`, `Entra__ClientId` und `Entra__ClientSecret`. Das Secret über die Secret-Verwaltung des Serverprozesses setzen. Kestrel mit HTTPS und einem für Browser und Prüfplätze vertrauenswürdigen Zertifikat konfigurieren.
3. Mit `DatabasePath` den Pfad zu einer dauerhaft gespeicherten, durch Betriebssystemrechte geschützten SQLite-Datei setzen. Die Datei gehört in die Datensicherung des Servers.
4. Für jeden Rechner eine eindeutige ID aus Buchstaben, Ziffern, Bindestrichen und Unterstrichen und einen zufälligen Schlüssel aus 64 Hex-Zeichen vergeben. Serverseitig unter `StationTokenHashes__<ID>` den SHA-256-Hash des **Schlüsseltexts** als Hex-String hinterlegen.

Die ausführbare Webanwendung lässt sich separat veröffentlichen:

```powershell
dotnet publish src/Testumgebung.RemoteAdmin -c Release -o artifacts/remote-admin
dotnet artifacts/remote-admin/Testumgebung.RemoteAdmin.dll
```

ASP.NET Core muss auf dem Server vorhanden sein. Details zur Entra-Registrierung und den Rückleitungen beschreibt die [ASP.NET-Core-Dokumentation zu OpenID Connect](https://learn.microsoft.com/aspnet/core/security/authentication/configure-oidc-web-authentication).

## Prüfplatz verbinden

Im Windows-Kontext, unter dem die Prüfplatzanwendung läuft, `TESTUMGEBUNG_REMOTE_URL` auf die HTTPS-Adresse des Servers, `TESTUMGEBUNG_REMOTE_ID` auf die vergebene Rechner-ID und `TESTUMGEBUNG_REMOTE_TOKEN` auf den unverschlüsselten Schlüssel setzen. Danach die Prüfplatzanwendung neu starten. Der Rechner erscheint nach seiner ersten Meldung in der Webanwendung. Jede Meldung bezieht sich auf das lokale Windows-Profil der laufenden Anwendung.

Übertragen werden nur die einstellbaren Anzeigewerte, Alarmauslöser und Prüfplatznamen. Zugangsdaten, Programme, Laufzeitpfade und Versandziele bleiben am Prüfplatz. Der Dienst stellt keine Bedienfunktionen für den laufenden Test bereit.
