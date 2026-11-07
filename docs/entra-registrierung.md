# Entra-Registrierung und Zugang einrichten

Die Anmeldung läuft über Microsoft Entra ID mit MSAL im Systembrowser. Die Anwendung sieht nie ein
Passwort. Solange keine Zugangsdatei existiert, läuft sie im **Entwicklungsmodus**: simulierte Anmeldung, jede
angemeldete Person hat alle Rechte.

## 1. App registrieren (einmalig, Entra-Administrator)

1. Entra Admin Center → *Identität* → *Anwendungen* → *App-Registrierungen* → **Neue Registrierung**.
   * Name: `Testumgebung Prüfplatz`
   * Unterstützte Kontotypen: **Nur Konten in diesem Organisationsverzeichnis** (ein Mandant)
2. *Authentifizierung* → **Plattform hinzufügen** → *Mobile Anwendungen und Desktopanwendungen*.
   * Umleitungs-URI: `http://localhost` (Systembrowser; MSAL verwendet einen freien lokalen Port)
   * *Öffentliche Clientflows zulassen*: **Ja**
3. *API-Berechtigungen*: `Microsoft Graph → User.Read` (delegiert) genügt; ggf. Administratorzustimmung erteilen.
4. Notieren: **Anwendungs-ID (Client-ID)** und **Verzeichnis-ID (Mandanten-ID)**.

**Bestehende Registrierungen:** Für die Browser-Anmeldung http://localhost ergänzen. Eine frühere WAM-Umleitungs-URI allein reicht nicht aus. Client-ID, Mandanten-ID und lokale Rechte bleiben gleich. Die Anmeldung zeigt weiterhin die Kontoauswahl; nach erfolgreicher Anmeldung kann der Browser-Tab geschlossen werden.

App-Rollen sind nicht nötig: Die Rechte werden je Prüfplatz lokal vergeben (Entscheidung Etappe 7).

### Firmenregistrierungen mit App-Zertifikat oder Client-Secret

Bei der persönlichen Anmeldung bestätigt Entra die Identität des Firmenkontos im Browser. Bei einer vertraulichen Web-App verlangt Entra zusätzlich einen Nachweis der Anwendung, sobald ein Anmeldedienst den Browser-Code gegen Tokens eintauscht.

| Begriff | Bedeutung |
|---|---|
| App-Zertifikat | Der Anmeldedienst signiert den App-Nachweis mit dem privaten Schlüssel. In der Entra-Registrierung liegt das öffentliche Zertifikat. |
| PFX/P12-Kennwort | Öffnet die Datei mit dem privaten Schlüssel beim Import. Es ist kein Parameter der Entra-Tokenanfrage. |
| Entra Client-Secret | Separater App-Nachweis, den Entra alternativ zur signierten Zertifikats-Assertion unterstützt. |
| Benutzerzertifikat | Authentifiziert gegebenenfalls das persönliche Konto im Browser, wenn der Mandant diese Methode anbietet. |

Bei **AADSTS7000218** erwartet die Registrierung einen App-Nachweis. Die IT sollte den Plattformtyp und die tatsächlich registrierte Umleitungs-URI prüfen. Der Desktop-Anmeldeweg benötigt die Registrierung als öffentliche Desktopanwendung mit Loopback-Redirect. Verlangt die Organisation einen vertraulichen Client, braucht dieser einen vertrauenswürdigen Anmeldedienst mit eigener HTTPS-Umleitungs-URI. Die derzeitige Desktop-Anbindung unterstützt diesen Dienst noch nicht.

Der Dienst muss ein persönliches Browser-Anmeldeergebnis des vorgesehenen Mandanten verifizieren und an die konkrete Anmeldesitzung des Prüfplatzes binden. App-Zertifikat, privater Schlüssel und gegebenenfalls Client-Secret liegen beim Dienst. Ein reines App-Token enthält keine bestätigte persönliche Identität für Entwickler- oder Freigaberechte.

Quellen: [Entra-Codeflow mit Zertifikatsnachweis](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow#request-an-access-token-with-a-certificate-credential), [öffentliche und vertrauliche Clients](https://learn.microsoft.com/en-us/entra/identity-platform/msal-client-applications), [Benutzerzertifikate](https://learn.microsoft.com/en-us/entra/identity/authentication/concept-certificate-based-authentication).

## 2. Zugangsdatei je Prüfplatz

Datei `%ProgramData%\Testumgebung\zugang.json` – gilt für alle Windows-Benutzer des Rechners. Bearbeiten über
*Entwicklung → Einstellungen → Zugang und Rechte* (Anwendung als Administrator starten) oder von Hand:

```json
{
  "format": 1,
  "clientId": "11111111-2222-3333-4444-555555555555",
  "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "inactivityMinutes": 0,
  "users": [
    { "upn": "m.weber@firma.example", "name": "M. Weber", "develop": true, "release": true },
    { "upn": "qs.leitung@firma.example", "name": "QS-Leitung", "develop": false, "release": true }
  ]
}
```

* `develop` = Entwickeln (Editor, Debugger, Simulation, Einstellungen, Entwürfe, Entwicklungsprüfung).
* `release` = Freigeben (geprüfte Pakete ausdrücklich freigeben). Getrennt oder kombiniert vergebbar.
* Konten aus anderen Mandanten und nicht bestätigte (simulierte) Anmeldungen werden abgelehnt, sobald `clientId` und
  `tenantId` gesetzt sind. Ist die Datei beschädigt, hat niemand Rechte (sicherer Zustand).
* `inactivityMinutes` > 0: automatische Abmeldung nach Inaktivität. Ein laufender oder pausierter Test wird dabei
  weder gestoppt noch fortgesetzt.

## 3. Schreibschutz (wichtig)

Unter `%ProgramData%` dürfen normale Benutzer standardmäßig neue Dateien anlegen. Den Ordner deshalb einmalig
absichern (Administrator-Eingabeaufforderung):

```bat
mkdir "%ProgramData%\Testumgebung"
icacls "%ProgramData%\Testumgebung" /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX"
```

(Administratoren und SYSTEM: Vollzugriff, Benutzer: Lesen.) Die Installation (Etappe 11) übernimmt das später.

## 4. Ablauf im Betrieb

* **Anmelden** (oben rechts): Windows zeigt die Kontoauswahl. Es wird nie automatisch das Konto der Windows-Sitzung
  verwendet – der Prüfplatz läuft unter dem Werker. Bis zur Bestätigung gelten keine erweiterten Rechte.
* **Firmenkonto wechseln**: Rechte der bisherigen Person werden sofort entzogen.
* **Abmelden**: Rechte weg, laufende Prüfungen laufen weiter.
* Alle An- und Abmeldungen, Entwicklereingriffe und Freigaben stehen im unveränderlichen Audit-Log (Releases).

## Rechte zentral über App-Rollen (optional)

Statt (oder zusätzlich zu) der Liste je Prüfplatz können die Rechte zentral in Entra vergeben werden:

1. App-Registrierung → **App-Rollen** → zwei Rollen anlegen, zulässige Mitgliedstypen „Benutzer/Gruppen“:
   Wert `Testumgebung.Entwickeln` und Wert `Testumgebung.Freigeben` (andere Werte in der Zugangskonfiguration einstellbar).
2. Unternehmensanwendungen → die App → **Benutzer und Gruppen** → Personen oder Gruppen den Rollen zuweisen.
3. In der Anwendung: Einstellungen → Zugang → **Rechtequelle** = `AppRollen` (nur zentral) oder `Beide` (Vereinigung mit der
   lokalen Liste). Die Rollen kommen aus dem ID-Token (Claim `roles`) und gelten nur für bestätigte Anmeldungen im
   konfigurierten Mandanten.

Rollenänderungen wirken bei der nächsten Anmeldung.
