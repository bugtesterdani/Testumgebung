# GitHub, Wiki und automatische MSIX-Releases

Das Projekt ist für `bugtesterdani/Testumgebung` vorbereitet. Das Repository wird zunächst **privat** angelegt. Die Workflows ändern seine Sichtbarkeit nicht. Alle Anleitungen bleiben als bearbeitbare Markdown-Dateien unter `docs/` erhalten; Wiki und Gesamthandbuch werden daraus erzeugt.

## 1. Einmalig anmelden und hochladen

Im Projektordner eine PowerShell 7 öffnen. Wenn die lokal bereitgestellte GitHub CLI vorhanden ist:

```powershell
artifacts/tools/github-cli/bin/gh.exe auth login --hostname github.com --git-protocol https --web
pwsh -File build/setup-github.ps1
```

Alternativ die offizielle GitHub CLI installieren und `gh auth login` verwenden. `setup-github.ps1` kann den Pfad über `-GhPath` erhalten. Die Anmeldung muss Repository-Erstellung, Git-Upload einschließlich Workflow-Dateien und Repository-Secrets erlauben. Tokens und Passwörter nicht in Chats, Dateien oder Git speichern.

Das Einrichtungsskript legt das private Repository an (oder prüft, dass das vorhandene Repository privat ist), speichert das bisherige Signierzertifikat als verschlüsselte Actions-Secrets und pusht den Projektstand nach `main`. Es lädt keine Buildordner, Laufzeitarchive, privaten Schlüssel oder lokalen Sicherungen hoch.

## 2. Signierzertifikat in Actions-Secrets

Unter **Settings → Secrets and variables → Actions** müssen diese Repository-Secrets existieren:

| Secret | Inhalt |
| --- | --- |
| `SIGNING_PFX_BASE64` | Base64 der PFX-Datei einschließlich privatem Schlüssel |
| `SIGNING_PFX_PASSWORD` | Kennwort für diese PFX-Datei |
| `WIKI_TOKEN` | Separater Zugang mit Schreibrechten zum Wiki; nur für die Wiki-Synchronisierung |

`setup-github.ps1` exportiert das bestehende Entwicklungszertifikat mit einem zufälligen Kennwort im Arbeitsspeicher und übergibt beide Signier-Secrets direkt an `gh secret set`. Das Zertifikat und der private Schlüssel bleiben identisch. Der Workflow schreibt die PFX-Datei ausschließlich in den temporären Runner-Ordner und löscht sie nach dem Signieren. Sie wird nicht als Artefakt hochgeladen. Die öffentliche `.cer`-Datei darf dem Release beiliegen.

Das Entwicklungszertifikat wird dadurch nicht zu einem öffentlich vertrauenswürdigen Zertifikat. Auf Zielrechnern muss weiterhin dessen Vertrauen eingerichtet werden. Für eine spätere öffentliche Verteilung ein geeignetes Code-Signing-Zertifikat verwenden und die Paketidentität sowie den Updatepfad prüfen. Die Secrets lassen sich ersetzen, ohne den Quellcode zu verändern.

## 3. Automatische MSIX-Erstellung

Der Workflow **MSIX-Build und Release** läuft bei jedem Push nach `main`, bei Versionstags und über **Actions → Run workflow**. Er verwendet einen GitHub-Windows-Runner, installiert .NET 10, lädt die fest versionierten Python-/C++-Toolchains mit Prüfsummenprüfung, erzeugt das PDF, führt die Release-Tests aus und erstellt das signierte Bundle.

| Auslöser | Ergebnis |
| --- | --- |
| Push nach `main` | Entwicklungsbuild `1.0.12.<Workflow-Laufnummer>` als Actions-Artefakt |
| Tag `v1.0.13.0` | Bundle `1.0.13.0` und GitHub Release mit MSIX, Zertifikat, Prüfsummen, Installationsanleitung und PDF |
| Manueller Start | Angegebene vierteilige Version, sonst automatische Entwicklungsbuild-Version; als Actions-Artefakt |

Die Laufnummer ist kein offizieller Release-Zähler. Die aktuelle Entwicklungsbasis `1.0.12` liegt unter dem nächsten offiziellen Release `1.0.13.0`. Bei größer werdenden Versionen die automatische Versionsbasis im Workflow anpassen und für das nächste offizielle Release eine höhere dritte Zahl verwenden. MSIX-Versionsteile dürfen höchstens 65535 sein. Für eine Installation über einen Entwicklungsbuild muss ein offizielles Release eine höhere Version tragen.

Neues offizielles Release erstellen:

```powershell
git tag v1.0.13.0
git push origin v1.0.13.0
```

Eine Version nur einmal verwenden. Die Dateien erscheinen nach erfolgreichem Build unter **Releases**. Entwicklungsartefakte werden 14 Tage gespeichert; Releases bleiben bis zur Löschung erhalten. Laufzeit, Speicher und Downloads können bei privaten Repositories das Actions-Kontingent verbrauchen.

## 4. Wiki einrichten

GitHub Free unterstützt Wikis in öffentlichen Repositories. Für ein zunächst privates Wiki ist GitHub Pro beziehungsweise Team erforderlich. Ohne diesen Tarif bleiben die Anleitungen privat unter `docs/` verfügbar; erzeugte Wiki-Seiten und PDF stehen zusätzlich in Actions-Artefakten bereit. Es wird keine öffentliche Dokumentationsseite angelegt.

1. Im Repository **Settings → General → Features → Wikis** aktivieren, sofern verfügbar.
2. Unter **Wiki** einmal eine Seite namens **Home** erstellen. GitHub initialisiert damit das separate Wiki-Git-Repository.
3. Einen separaten GitHub-Zugang für die Wiki-Synchronisierung bereitstellen. Beim klassischen Personal Access Token ist für ein privates Wiki der Scope `repo` erforderlich. Das Token im Repository als Actions-Secret `WIKI_TOKEN` speichern; Ablaufdatum und Erneuerung beachten. Das Token nicht in die Workflow-Datei schreiben.
4. Unter **Actions → Dokumentation und Wiki → Run workflow** starten.

Die Synchronisierung überträgt alle Markdown-Anleitungen als einzelne Wiki-Seiten und erstellt Startseite, Seitenleiste sowie interne Verweise. Abbildungen verweisen auf den dazugehörigen Commit im Hauptrepository. Bei Änderungen an `docs/` auf `main` erfolgt die Aktualisierung automatisch. Änderungen direkt im Wiki werden beim nächsten Lauf für die automatisch erzeugten Seiten überschrieben; deshalb Anleitungen im Hauptrepository bearbeiten. Andere manuell angelegte Wiki-Seiten werden nicht gelöscht. Bei umbenannten oder entfernten Quelldateien bleiben alte Wiki-Seiten bestehen und können manuell entfernt werden.

Fehlt `WIKI_TOKEN`, erzeugt der Workflow dennoch PDF und Wiki-Dateien und weist auf die fehlende Synchronisierung hin. Ist ein Token vorhanden, aber das Wiki oder seine Berechtigung ungültig, schlägt die Synchronisierung sichtbar fehl. Das Standard-Workflow-Token wird nicht als allgemeiner Ersatz für einen Wiki-Zugang vorausgesetzt.

## 5. Dokumentation lokal neu erstellen

Auf Windows mit Python und ReportLab:

```powershell
python -m pip install reportlab==4.4.9
python build/create-complete-manual.py
python build/create-wiki.py --repository bugtesterdani/Testumgebung
```

Ausgaben: `output/pdf/Testumgebung-Gesamthandbuch.pdf` und `artifacts/wiki/`. Der PDF-Generator berücksichtigt neue Markdown-Anleitungen automatisch. Er verwendet die Windows-Schriftarten Segoe UI und Consolas. Die Abbildungen stammen aus den vorhandenen Dokumentationsdateien.

## 6. Später öffentlich schalten

Unter **Settings → General → Danger Zone → Change repository visibility** kann der Eigentümer das Repository später öffentlich machen. Dadurch werden Quellcode, Git-Historie, Wiki und Releases öffentlich zugänglich. Vorher Projektbeispiele, Dokumentation, Lizenzentscheidung und verbleibende interne Angaben prüfen. Eine Open-Source-Lizenz wird nicht automatisch gewählt.

Die Signier-Secrets bleiben als Secrets gespeichert. Die Workflows werden durch externe Pull Requests nicht zur Signierung gestartet; der Signierbuild läuft nur bei `main`, Tags oder manueller Ausführung. Schreibrechte zum Repository nur vertrauenswürdigen Personen geben, da Workflow-Änderungen Zugang zu Signier-Secrets ermöglichen können.

## Quellen

- [GitHub: Verfügbarkeit von Wikis](https://docs.github.com/en/communities/documenting-your-project-with-wikis/about-wikis)
- [GitHub: Secrets in Actions](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets)
- [GitHub: GITHUB_TOKEN](https://docs.github.com/en/actions/concepts/security/github_token)
