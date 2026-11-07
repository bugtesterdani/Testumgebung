# Lokaler Entra-Anmeldedienst

Die separate Lösung `Testumgebung.EntraBroker.slnx` enthält einen Windows-Dienst für den festen HTTPS-Rückruf auf `https://localhost:5001`. Die Desktop-App startet die Anmeldung im Browser. Der Dienst löst den Code bei Entra ein und liefert einmalig die geprüfte Benutzeridentität für genau diesen Anmeldeversuch. Entwickler- und Freigaberechte werden danach anhand der Zugangskonfiguration der Testumgebung ausgewertet.

## Voraussetzungen

- Die bestehende Entra-App ist als **Web** registriert. Anwendungs-ID, Mandanten-ID und die vollständige registrierte Adresse einschließlich Pfad müssen bekannt sein.
- Windows und die x64 ASP.NET-Core-Runtime 10.0.14 oder ein neuerer Sicherheitsstand derselben Hauptversion. Der Dienst wird separat gebaut und installiert.
- Port 5001 ist lokal frei. Ein vertrauenswürdiges TLS-Zertifikat für `localhost` liegt mit privatem Schlüssel in **Lokaler Computer → Eigene Zertifikate**. Der Browser und Windows müssen dessen Kette vertrauen.
- Als App-Nachweis entweder ein in Entra registriertes RSA-App-Zertifikat mit privatem Schlüssel im selben Windows-Speicher oder der Wert eines Entra-Client-Secrets. Für den App-Nachweis empfiehlt sich ein anderes Zertifikat als für TLS. Entra erwartet pro Tokenanfrage einen dieser Nachweise.

## Einmalig installieren

Aus dem Projektordner bauen. Der Publish-Ordner enthält den Dienst, ohne die ASP.NET-Core-Laufzeit mitzuliefern.

```powershell
dotnet test Testumgebung.EntraBroker.slnx -c Release
dotnet publish src/Testumgebung.EntraBroker/Testumgebung.EntraBroker.csproj -c Release -r win-x64 --self-contained false -o artifacts/entra-broker
```

Die IT kopiert den Publish-Inhalt nach `C:\Program Files\TestumgebungEntra`. Der Ordner und die ausführbaren Dateien dürfen nur durch Administratoren oder SYSTEM geändert werden. Anschließend in einer erhöhten PowerShell den Dienst anlegen und auf das eigene virtuelle Dienstkonto umstellen:

```powershell
$brokerExe = 'C:\Program Files\TestumgebungEntra\Testumgebung.EntraBroker.exe'
New-Service -Name TestumgebungEntra -DisplayName 'Testumgebung Entra-Anmeldung' `
    -BinaryPathName ('"{0}"' -f $brokerExe) -StartupType Manual
sc.exe config TestumgebungEntra obj= 'NT SERVICE\TestumgebungEntra' start= auto
if ($LASTEXITCODE) { throw 'Dienstkonto konnte nicht eingerichtet werden.' }

$brokerDir = Join-Path $env:ProgramData 'TestumgebungEntra'
New-Item -Path $brokerDir -ItemType Directory -ErrorAction Stop | Out-Null
icacls.exe $brokerDir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' 'NT SERVICE\TestumgebungEntra:(OI)(CI)RX'
if ($LASTEXITCODE) { throw 'Dienstordner konnte nicht abgesichert werden.' }
icacls.exe $brokerDir /setowner '*S-1-5-32-544'
if ($LASTEXITCODE) { throw 'Eigentümer konnte nicht gesetzt werden.' }
```

Bei einem bereits vorhandenen Dienstordner prüft die IT zuerst Eigentümer, Berechtigungen und Inhalt. Die Einrichtung erwartet einen Ordner ohne Verknüpfungen. Das Dienstkonto benötigt Leserechte auf dem privaten TLS-Schlüssel und bei Zertifikatsanmeldung auch auf dem privaten App-Schlüssel. Die IT setzt sie in `certlm.msc` über **Alle Aufgaben → Private Schlüssel verwalten**. Wenn eine PFX-Datei importiert wird, den Schlüssel ohne Exportoption importieren. Das PFX-Kennwort wird nur beim Import benötigt.

Den SHA-256-Fingerabdruck eines installierten Zertifikats kann die IT über dessen Windows-Fingerabdruck ermitteln:

```powershell
$brokerCert = Get-Item 'Cert:\LocalMachine\My\WINDOWS_FINGERABDRUCK'
$sha256 = [Security.Cryptography.SHA256]::Create()
try { [BitConverter]::ToString($sha256.ComputeHash($brokerCert.RawData)).Replace('-', '') }
finally { $sha256.Dispose() }
```

Im geschützten Dienstordner `broker.json` anlegen. Nach dem Schreiben den Eigentümer auf die Administratorengruppe setzen. Die Beispielwerte für IDs, Pfad und Fingerabdrücke durch die echten Werte ersetzen.

```json
{
  "EntraBroker": {
    "TenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    "ClientId": "11111111-2222-3333-4444-555555555555",
    "Callback": "https://localhost:5001/Anmeldung",
    "TlsCertificateSha256": "SHA256_DES_LOKALHOST_TLS_ZERTIFIKATS",
    "AppCertificateSha256": "SHA256_DES_ENTRA_APP_ZERTIFIKATS"
  }
}
```

```powershell
icacls.exe (Join-Path $brokerDir 'broker.json') /setowner '*S-1-5-32-544'
if ($LASTEXITCODE) { throw 'Eigentümer der Konfiguration konnte nicht gesetzt werden.' }
```

### Bei Verwendung eines Client-Secrets

`AppCertificateSha256` aus `broker.json` entfernen. In **Windows PowerShell 5.1** als Administrator das Secret verdeckt eingeben und mit DPAPI an diesen Rechner binden. Der Blob muss im zuvor abgesicherten Dienstordner verbleiben. DPAPI im Rechnerkontext ist auf die Windows-Dateirechte angewiesen. Das Dienstkonto prüft diese Rechte beim Start und bricht bei zu weit gefassten Freigaben ab.

```powershell
Add-Type -AssemblyName System.Security
$brokerDir = Join-Path $env:ProgramData 'TestumgebungEntra'
$secretPath = Join-Path $brokerDir 'client-secret.dpapi'
$secret = Read-Host 'Entra Client-Secret (Wert)' -AsSecureString
if ($secret.Length -eq 0) { $secret.Dispose(); throw 'Das Secret ist leer.' }
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
$bytes = $null
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes([Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer))
    $encrypted = [Security.Cryptography.ProtectedData]::Protect(
        $bytes, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    [IO.File]::WriteAllBytes($secretPath, $encrypted)
} finally {
    if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    $secret.Dispose()
}
icacls.exe $secretPath /setowner '*S-1-5-32-544'
if ($LASTEXITCODE) { throw 'Eigentümer des Secret-Blobs konnte nicht gesetzt werden.' }
```

Bei Rotation den Dienst stoppen, den Nachweis im geschützten Ordner oder Zertifikatsspeicher ersetzen und neu starten.

## Desktop-App verbinden

1. Den Windows-Dienst mit `Start-Service TestumgebungEntra` starten. Bei einem Fehler Dienststatus und Windows-Ereignisanzeige prüfen. Häufige Ursachen sind ein belegter Port, ein fehlender privater Schlüssel oder ein unzulässiger Zugriff auf die Konfiguration.
2. Unter **Einstellungen → Zugang und Rechte** dieselbe Anwendungs-ID, Mandanten-ID und vollständige **Entra-Umleitungs-URI** eintragen.
3. Unter **SHA-256-Fingerabdruck des lokalen Entra-Dienstes** den Fingerabdruck des **TLS-Zertifikats** eintragen. Der Prüfplatz verifiziert bei jeder Anmeldung Zertifikatskette, Host und diesen Fingerabdruck. Die Datei `zugang.json` vor Änderungen durch normale Prüfplatzkonten schützen.
4. Rechtequelle wie gewohnt wählen und einen echten Browser-Anmeldeversuch durchführen. Die IT prüft sowohl ein berechtigtes Konto als auch ein Konto ohne Prüfplatzrechte.

Der Dienst lauscht auf Loopback. Browser- und Abrufschlüssel sind getrennt und laufen nach fünf Minuten ab. Der Abrufschlüssel bleibt im Desktop-Prozess. Ein erfolgreiches Ergebnis kann einmal abgeholt werden und enthält keine Entra-Tokens. Der Dienst verteilt weder Windows-Rechte noch App-only-Tokens. Die Schnittstelle bindet das Ergebnis an einen Anmeldeversuch. Prozesse unter demselben Windows-Konto kann Windows dadurch nicht anhand des Namens der EXE unterscheiden.

Quellen: [OpenID Connect in ASP.NET Core](https://learn.microsoft.com/aspnet/core/security/authentication/configure-oidc-web-authentication), [Entra-Zertifikatssignatur](https://learn.microsoft.com/en-us/entra/identity-platform/certificate-credentials), [Windows-Zertifikatsspeicher](https://learn.microsoft.com/en-us/windows-server/identity/ad-cs/inspect-certificates-manage-certificate-stores), [DPAPI-Schutzbereiche](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope).
