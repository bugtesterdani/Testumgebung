# Dynamische COM-Ports und Netzwerkverbindungen

## COM-Port im Prüfablauf ändern

1. In der Prüfplatzkonfiguration ein Gerät „Seriell (RS-232/RS-485)“ anlegen und einer abstrakten Ressource, z. B. `Pruefling_Seriell`, zuordnen. Die Geräteadresse dient als Ausgangswert.
2. Im Prüfprogramm eine String-Variable `ComPort` mit Anfangswert `COM3` anlegen.
3. Im Baustein **Senden**, **Empfangen** oder **Abfrage** die Ressource wählen und bei **Adresse / COM-Port** `{ComPort}` eintragen.
4. `ComPort` später mit „Wert setzen“, Werker-Eingabe oder C#-Code auf `COM7` ändern. Beim nächsten Kommunikationsschritt wird die Variable erneut ausgewertet; die alte Verbindung wird geschlossen und die neue beim Zugriff geöffnet.

Alternativ ändert **Verbindung konfigurieren** die Adresse ausdrücklich für nachfolgende Schritte. Danach darf deren Adressfeld leer bleiben. Ein leeres Feld verwendet immer die momentan konfigurierte Adresse. Ein unverändertes Ziel behält seine Verbindung und den Empfangspuffer.

**Verbindungsoptionen** unterstützen mehrere Zeilen, z. B.:

```text
Baud=115200
Datenbits=8
Parität=N
Stoppbits=1
```

Optionen werden als vollständiger Satz ersetzt; leer behält die bisherigen Optionen. Optionen brauchen ein ausgefülltes Adressfeld. Auch Baud und andere Werte können aus `{Variablen}` stammen. Fehlerhafte Optionen lassen beim Streamadapter die bisherige Verbindung unverändert. Unterstützt: serielle Byte-Strom-Geräte, TCP, UDP, Telnet sowie SSH/SFTP und HTTP. VISA-/SCPI-, Modbus- und CAN-Treiber werden durch diesen Baustein nicht umkonfiguriert.

Bei gemeinsam benutzten Verbindungen die Ressource für die gesamte zusammengehörige Transaktion in der Gruppe reservieren. Die Zugriffe laufen weiterhin über die normale Hardwarevermittlung mit Sperren und Zugriffsprotokoll.

## SSH und SFTP

Geräteadapter **SSH / SFTP**, Adresse z. B. `192.168.1.20:22`, abstrakte Ressource `Pruefling_SSH`. Optionen:

```text
Benutzer=pruefer
HostKeySHA256=SHA256:FingerabdruckDerGegenstelle
```

Zusätzlich entweder `Passwort` oder `Schlüsseldatei` und bei Bedarf `Schlüsselpasswort`. Der Host-Fingerabdruck muss von der tatsächlichen Gegenstelle stammen und wird bei jeder Verbindung geprüft. Fehlender oder falscher Fingerabdruck verhindert die Verbindung.

Bausteine:

- **SSH-Befehl**: Befehl, Antwort-Zielvariable (stdout), Status-Zielvariable (Exitcode) und optional Fehler-Zielvariable (stderr). Standardbewertung PASS bei Exitcode 0, sonst FAIL. Jeder Schritt öffnet eine eigene SSH-Verbindung und führt einen eigenständigen Befehl aus. Ein `cd` in einem Schritt verändert nicht den nächsten; zusammengehörige Shell-Kommandos in denselben Befehl schreiben.
- **SFTP-Datei hochladen** / **herunterladen**: lokale Datei, Remote-Datei, Zeitlimit und ausdrückliche Auswahl zum Überschreiben. Zielvariable erhält die Byteanzahl. Ein Download wird zunächst in eine temporäre Nachbardatei geschrieben und erst nach erfolgreichem Abschluss übernommen. Der lokale Zielordner muss existieren. Uploads sind nicht atomar; ein abgebrochener Upload kann auf der Gegenstelle eine unvollständige Datei hinterlassen.

Adresse und Optionen lassen sich ebenfalls dynamisch setzen:

```text
Adresse / COM-Port: {Host}:{SshPort}
Verbindungsoptionen:
Benutzer={SshBenutzer}
Passwort={SshPasswort}
HostKeySHA256={HostFingerprint}
```

Die neuen Bausteine schreiben keine Verbindungsoptionen, Passwörter oder HTTP-Header in ihre Ergebnismeldungen. Projektdateien sind jedoch keine Geheimnisablage: fest eingetragene Passwörter und Variablen-Anfangswerte liegen dort lesbar. Zugangsdaten zur Laufzeit aus einer geeigneten Quelle laden, z. B. über die eigene C#-DLL. SSH.NET wird mit der Software ausgeliefert; kein externes ssh-/sftp-Programm erforderlich.

## HTTP / HTTPS statt curl-Aufruf

Geräteadapter **HTTP / HTTPS**, Basisadresse z. B. `https://192.168.1.20`, Ressource `Pruefling_HTTP`. Im Baustein **HTTP-Anfrage (GET / POST / …)**:

```text
Adresse / COM-Port: https://{Host}
Methode: POST
Pfad / URL: /api/test
Header:
Content-Type: application/json
Authorization: Bearer {Token}
Body:
{{"seriennummer":"{Kennung}","spannung":{Spannung}}}
```

`{{` und `}}` ergeben literale JSON-Klammern; `{Ausdruck}` setzt Variablen ein. Für komplexes JSON einschließlich beliebiger Zeichen in Strings im C#-Code `JsonSerializer.Serialize` nutzen und den resultierenden String als Variable `{JsonBody}` übergeben. Eingesetzte Werte werden nicht erneut als Vorlage interpretiert.

GET, POST, PUT, PATCH, DELETE, HEAD und OPTIONS sind verfügbar. Antworttext und Statuscode können in Variablen gespeichert werden. Standard: PASS bei 200–299, sonst FAIL. **Erwarteter Status / Exitcode** erlaubt andere Werte oder Bereiche, z. B. `201`, `200-204` oder `404`. Transportfehler und Zeitüberschreitungen ergeben ERROR; Werkerabbruch bricht die laufende Operation ab. Die Antwort ist auf 1 MiB begrenzt.

HTTP erfolgt über .NET HttpClient, ohne curl-Installation. Normale TLS-Zertifikatsprüfung bleibt aktiv. Redirects werden nicht automatisch verfolgt (3xx wird als Antwort zurückgegeben). Dies ist kein vollständiger Ersatz aller curl-Optionen, z. B. Multipart-Uploads, Proxyauswahl oder beliebige Download-Dateien sind noch keine eigenen Bausteinparameter.

## Telnet und normale TCP-Verbindungen

Adapter **Telnet** mit `Host:23` verwenden. Die vorhandenen Bausteine Senden/Empfangen/Abfrage funktionieren darauf. Der Adapter behandelt Telnet-IAC-Escaping und Verhandlungsbytes, lehnt optionale Telnet-Funktionen ab und liefert nur Nutzdaten weiter. Keine vollständige Terminalemulation. Für Login-/Passwortdialoge die Eingabeaufforderungen mit Empfangen abwarten und anschließend mit Senden antworten; „Abfrage“ verwirft wie bisher alte Eingabedaten. Bereits gespeicherte Promptdaten daher nicht versehentlich wegwerfen.

TCP-Client bleibt als Adapter für reine TCP-Text-/Binärprotokolle verfügbar. Auch hier kann **Adresse / COM-Port** `{Host}:{Port}` enthalten.

## C#- und Python-CodeBlocks

C#:

```csharp
ConfigureConnection("Pruefling_Seriell", ComPort);
CommSend("Pruefling_Seriell", "VERSION?\r\n");
var antwort = SshExecute("Pruefling_SSH", "uname -a");
return antwort["code"].GetValue<int>() == 0 ? Pass(antwort["text"].GetValue<string>()) : Fail();
```

Weitere Funktionen: `HttpRequest(resource, path, method, body, headers, timeoutMs)`, `SftpUpload(resource, local, remote, overwrite, timeoutMs)`, `SftpDownload(resource, remote, local, overwrite, timeoutMs)`. Netzwerkfunktionen liefern JSON-Felder `text`, `code`, `error`. `ConfigureConnection(resource, address, options)` setzt Optionen aus einem mehrzeiligen String. Im Code werden Werte direkt übergeben; keine `{…}`-Vorlagen nötig.

Python: `configure_connection`, `http_request`, `ssh_execute`, `sftp_upload`, `sftp_download`. Rückgaben sind Dictionaries mit denselben Feldern. Im C++-Code kann dieselbe Host-Schnittstelle über `tu::device` mit `comm.configure`, `http.request`, `ssh.exec`, `sftp.upload`, `sftp.download` angesprochen werden.

## Integrationstests

HTTP, COM-Variablenauflösung, TCP-Zielwechsel, Telnet und CodeBlock-Aufrufe sind reguläre Engine-Tests. Für die zusätzliche lokale SSH-/SFTP-Testgegenstelle Python mit Paramiko bereitstellen und `TESTUMGEBUNG_SSH_TEST_PYTHON` auf die Python-Datei setzen. Diese Testabhängigkeit wird nicht mit der Anwendung ausgeliefert. Ohne diese Umgebungsvariable wird der zusätzliche SSH-Integrationstest ausdrücklich übersprungen.

Verwendete Bibliothek: [SSH.NET 2026.0.0](https://www.nuget.org/packages/SSH.NET/2026.0.0), [offizielle API](https://sshnet.github.io/SSH.NET/examples.html).
