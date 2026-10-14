# Sicherheitsupdate .NET 10.0.13 – Nachweis vom 14.10.2026

SDK 10.0.402 und Runtime 10.0.13 stammen aus Microsofts offiziellen Release-Metadaten:
https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json

Das lokal geprüfte SDK-ZIP hat SHA-512
`ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd`.
Es wird als Entwicklungswerkzeug verwendet und nicht mit der Anwendung verteilt.

Die NuGet-Originaltexte sind gegenüber 10.0.12 bytegleich:

- NETCore-Runtime `LICENSE.TXT`: SHA-256 `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b`.
- WindowsDesktop-Runtime `LICENSE`: SHA-256 `a89886665765362eb77e0f8e26602c924520041d1711b2eedc136434fe4d01ab`.

Die aktuelle Microsoft-Dateizuordnung wurde erneut von
https://raw.githubusercontent.com/dotnet/core/main/license-information-windows.md
bezogen und ist bytegleich mit dem eingecheckten Nachweis
`dotnet-windows-mapping.md`. Die .NET-Library- und Windows-SDK-Ausnahmen bleiben
unverändert gültig; die Paketdeklaration MIT ersetzt diese Dateizuordnung nicht.
Neue Pakettexte und Prüfsummen liegen zusätzlich im versionierten NuGet-Lizenzordner.

Die technische Release-Prüfung akzeptiert jetzt ausdrücklich 10.0.13 und kontrolliert
weiterhin die Originalverträge, Zuordnung jeder Binärdatei und mitgelieferten Quellen.
Ein Versionsupdate ersetzt keine zukünftige Neubewertung geänderter Vertragstexte.
