# MSIX-Bundle erstellen und installieren

## Release erstellen

Im Projektordner `Release-MSIX.cmd` doppelklicken und eine neue vierteilige Version eingeben, z. B. `1.0.1.0`. Alternativ:

```powershell
pwsh -NoProfile -File build/msix.ps1 -Version 1.0.1.0
```

Das Skript führt die Tests im Release-Modus aus, veröffentlicht die Anwendung und beide .NET-Worker als self-contained win-x64, erstellt das MSIX und bündelt und signiert es. Bereits vorhandene Bundle-Versionen werden nicht überschrieben. Der Rechner zum Bauen benötigt .NET SDK 10, PowerShell 7, Windows SDK mit MakeAppx und SignTool sowie die vorhandene portable Toolchain (`build/acquire-toolchain.ps1`). NuGet-Zugriff ist erforderlich, sofern die benötigten Pakete und Runtime-Packs noch nicht lokal vorliegen. `-SkipTests` ist nur für bewusst ausgelassene Tests gedacht.

Ergebnis: `artifacts/msix/<Version>/Testumgebung_<Version>_x64.msixbundle`, öffentliches Signierzertifikat, SHA-256-Prüfsummen und diese Anleitung. Es ist ein Bundle mit einem x64-Paket, kein ARM64- oder x86-Build. Mindestbetriebssystem: Windows 10 Version 2004. Die .NET-Laufzeit, der C#-Worker, C++-Worker, Compiler und GDB sind enthalten. Python 3.14.8 einschließlich Standardbibliothek wird ebenfalls portabel gebündelt und automatisch verwendet. Siehe `docs/portable-python.md` für zusätzliche Pakete.

Die Software-Freigabe ist unabhängig von den Testprogramm-Freigaben im Reiter „Releases“. Dort werden Prüfprojekte versioniert; `Release-MSIX.cmd` baut die installierbare Software.

## Installation des lokalen Test-Releases

Das automatisch erzeugte Zertifikat ist selbstsigniert. Windows muss ihm auf dem Zielrechner vertrauen, bevor das Bundle installiert werden kann. Der Build verändert keinen Zertifikatspeicher und installiert die Anwendung nicht automatisch.

1. `Testumgebung-Signer.cer` öffnen und das Zertifikat prüfen.
2. Für die lokale Installation als Administrator in **Lokaler Computer → Vertrauenswürdige Personen** importieren (oder durch die IT verteilen lassen).
3. Die `.msixbundle`-Datei öffnen und installieren.

Im Startmenü erscheinen „Testumgebung“ für den normalen Betrieb sowie „Testumgebung – Beispiele A bis D“ für den lokalen Entwicklungs-/Simulationsmodus. Für den normalen Betrieb gelten weiterhin die Zugangskonfiguration und Prüfplatzkonfiguration. A123/B123/C123/D123 wählen im Beispielstart das jeweilige Programm.

Für Updates die Version erhöhen und denselben Paketnamen sowie denselben Publisher beibehalten. Das lokale Zertifikat liegt unter `artifacts/signing/Testumgebung-Development.pfx` und wird wiederverwendet. Diese unverschlüsselte private Testdatei ist durch `.gitignore` ausgeschlossen: nicht verteilen oder einchecken, sicher aufbewahren. An andere Rechner nur Bundle und **öffentliches** `.cer` weitergeben. Beim Löschen von `artifacts` geht auch der lokale Signierschlüssel verloren; für weitere Updates muss ein neues Zertifikat gegebenenfalls erneut vertraut werden.

## Firmensignierung

```powershell
$env:TESTUMGEBUNG_SIGN_PASSWORD = 'Passwort aus sicherer Quelle'
pwsh -File build/msix.ps1 -Version 1.0.2.0 `
  -CertificatePath 'C:\Signierung\Firma.pfx' `
  -TimestampUrl 'https://Zeitstempeldienst-der-Firma'
```

Der Publisher wird aus dem Zertifikat übernommen. Der Wechsel vom Test-Publisher zum Firmen-Publisher erzeugt eine andere Paketidentität; eine bestehende Testinstallation wird dadurch nicht einfach aktualisiert. Für eine dauerhafte Verteilung das Firmenzertifikat und dessen Vertrauenskette verwenden.

Referenzen: [Microsoft: MSIX-Signierung](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview), [Paketmanifest für Desktopanwendungen](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion).

# Lizenzunterlagen bei Weitergabe

Der Build erzeugt neben dem Bundle ein gleichversioniertes `Licenses-and-Sources.zip`
und `PUBLICATION.md`. Beide gehören mit den Lizenztexten, Prüfnachweisen und
Prüfsummen zum Download. In der Anwendung öffnet **Lizenzen & Quellen** jederzeit
die Hinweise und den Ordner mit Originaltexten und passenden Toolchain-Quellen.
Details: [Öffentliche Veröffentlichung](oeffentliche-veroeffentlichung.md).

