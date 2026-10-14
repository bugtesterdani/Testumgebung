# Sicherheitsprüfung und .NET-Laufzeit

Prüfstand: 14.10.2026. SDK und GitHub-Runner verwenden .NET SDK **10.0.402**.
Eigenständige Installationspakete enthalten **.NET und WindowsDesktop 10.0.13**.
Die Testabhängigkeit `System.Security.Cryptography.Pkcs` ist auf **9.0.21** festgelegt;
die SQLite-Pakete verwenden **10.0.13**. Lockdateien sind neu aufgelöst.

Die vielen GitHub-Meldungen betrafen wiederholt dieselben Advisories in unterschiedlichen
Projekten. ASP.NET erscheint als SDK-generierter Runtime-Download, obwohl die WPF-Anwendung
keinen ASP.NET-Webserver verwendet. Auch diese Build-Abhängigkeit wird auf 10.0.13 geprüft.
NETCore und WindowsDesktop sind dagegen tatsächlich im eigenständigen Paket enthalten.

## Automatische Prüfung

```powershell
dotnet restore Testumgebung.sln
pwsh -File build/check-security.ps1
```

Die Prüfung untersucht direkte und transitive NuGet-Pakete anhand der verfügbaren
Advisory-Daten und kontrolliert zusätzlich die SDK-generierten Runtime-Downloads.
Bekannte verwundbare Paketauflösungen oder nicht freigegebene Runtime-Versionen
verhindern einen erfolgreichen Build. Der JSON-Nachweis wird im Actions-Artefakt
`Sicherheitspruefung-<Version>` gespeichert. Ein grüner Audit bestätigt keine Abwesenheit
unbekannter Schwachstellen und bewertet nicht alle nativen Werkzeuge oder externen Treiber.
GitHub-Meldungen zum bisherigen main-Stand werden erst nach Merge und erneuter
Abhängigkeitsanalyse aktualisiert; sie werden nicht manuell als behoben ausgeblendet.

## Nachweise dieses Updates

- NuGet-Audit: keine bekannten verwundbaren direkten oder transitiven Pakete.
- 60 Runtime-Download-Einträge auf 10.0.13 geprüft.
- Vollständiger Coverage-Lauf: 6.001 Tests bestanden, fünf externe Prüfungen übersprungen.
- Zeilenabdeckung 94,057171 %, Zweigabdeckung 85,660937 %, höchster CRAP 28,867932.
- Eigenständiges Testbundle `1.0.15.1` gebaut und mit dem Entwicklungszertifikat signiert.
- Nachprüfung: 2.106 Binärdateien, 468 Lizenzdateien und 46 zugehörige Quelldateien bestätigt.
- Lizenzinventar enthält keine unzugeordneten Binärdateien oder ungelösten gebündelten Pakete.

Das Testbundle ist kein offizielles Release mit dem produktiven Signierschlüssel.
Die Lizenztexte und Microsoft-Dateizuordnung wurden erneut verglichen; der Nachweis steht
unter `licenses/third-party/microsoft/runtime-10.0.13-review.md`. Die Inventargenerierung
erfolgt vor der endgültigen Lizenzprüfung, Paketierung und Signierung. Der vorbereitende
Publish-Durchlauf erstellt noch kein auslieferbares Paket.

## Release-Action und gelöschte Tags

Der Build für 1.0.14 war erfolgreich; anschließend scheiterte die Entwurfsauflösung
mit HTTP 404. Der Benutzer hatte den Tag gelöscht. `v1.0.14` wurde am ursprünglichen,
durch Build-Protokoll und PR #8 belegten Commit `fc2d3fa7bbe900880e344860b4f80b4dec7ed470`
wiederhergestellt. Der alte Entwurf enthält weiterhin den damaligen Runtime-Stand.

Die korrigierte Automatik findet Entwürfe über die authentifizierte Release-Liste und
aktualisiert sie über ihre ID. Dabei bleiben Tag, Commit und Entwurfsstatus explizit
erhalten. Bereits veröffentlichte Release Notes werden nicht überschrieben.
Vorhandene Tags werden nicht auf einen neuen Code-Stand verschoben. Das Sicherheitsupdate
wird als **1.0.15** vorbereitet; Release-Issue #10 ist die zugehörige Planung.
Alte fehlgeschlagene Actions behalten ihren historischen Status. Ein erneuter Lauf eines
alten Workflows verwendet dessen alten Commit; die korrigierte Version gilt nach Merge.

Offizielle Quellen: [Microsoft .NET 10 Downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0),
[Release-Metadaten](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
