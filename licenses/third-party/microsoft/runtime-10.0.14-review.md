# Sicherheitsupdate .NET 10.0.14 – Nachweis vom 03.11.2026

Microsofts offizielle Release-Metadaten nennen SDK 10.0.403 und Runtime 10.0.14 als aktuellen stabilen Stand:
https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json

Die Runtime korrigiert unter anderem CVE-2026-70197 (Remote Code Execution) und
GHSA-p3jp-9v83-76w9 (Brotli Decoder Out-of-bounds Write):

- https://github.com/advisories/GHSA-4mpw-cpwj-hxxj
- https://github.com/advisories/GHSA-p3jp-9v83-76w9

Die aus den NuGet-Paketen 10.0.14 gelesenen Originaltexte sind gegenüber dem
bereits geprüften Stand 10.0.13 bytegleich:

- NETCore-Runtime `LICENSE.TXT`: SHA-256 `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b`.
- WindowsDesktop-Runtime `LICENSE`: SHA-256 `a89886665765362eb77e0f8e26602c924520041d1711b2eedc136434fe4d01ab`.

Die Microsoft-Dateizuordnung wurde erneut von
https://raw.githubusercontent.com/dotnet/core/main/license-information-windows.md
bezogen. SHA-256 `3bdf7142d570f11780d1a5fcb6cbe47f5cf4a15bc634acf8db93fe3be8837119`
entspricht weiterhin dem eingecheckten Original. Die .NET-Library- und
Windows-SDK-Ausnahmen gelten unverändert; die allgemeine MIT-Paketdeklaration
ersetzt diese Dateizuordnung nicht.

SDK und CI verwenden 10.0.403. `RuntimeFrameworkVersion` legt zusätzlich 10.0.14
für alle Projekte fest. Die technische Sicherheitsprüfung kontrolliert die
aufgelösten Runtime-Downloads; die Release-Lizenzprüfung akzeptiert ausdrücklich
die beiden Runtime-Pakete 10.0.14 und prüft weiterhin Originalverträge,
Dateizuordnung und mitgelieferte Quellen.
