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
| `WIKI_SSH_KEY` | Alternative: privater Repository-Deploy-Schlüssel zur Wiki-Synchronisierung |

`setup-github.ps1` exportiert das bestehende Entwicklungszertifikat mit einem zufälligen Kennwort im Arbeitsspeicher und übergibt beide Signier-Secrets direkt an `gh secret set`. Das Zertifikat und der private Schlüssel bleiben identisch. Der Workflow schreibt die PFX-Datei ausschließlich in den temporären Runner-Ordner und löscht sie nach dem Signieren. Sie wird nicht als Artefakt hochgeladen. Die öffentliche `.cer`-Datei darf dem Release beiliegen.

Das Entwicklungszertifikat wird dadurch nicht zu einem öffentlich vertrauenswürdigen Zertifikat. Auf Zielrechnern muss weiterhin dessen Vertrauen eingerichtet werden. Für eine spätere öffentliche Verteilung ein geeignetes Code-Signing-Zertifikat verwenden und die Paketidentität sowie den Updatepfad prüfen. Die Secrets lassen sich ersetzen, ohne den Quellcode zu verändern.

## 3. Automatische MSIX-Erstellung

Der Workflow **MSIX-Build und Release** läuft für Änderungen an main, Pull Requests und manuelle Entwicklungsbuilds. Das Release-Issue liefert die gemeinsame Basisversion; vollständiger Ablauf: [Releases über Issues](release-ablauf.md).

| Auslöser | Ergebnis |
| --- | --- |
| Normaler PR oder Merge nach main | Entwicklungsbuild `X.Y.Z.<Laufnummer>`; kein Release |
| Merge des abschließenden PRs mit `Closes #<Release-Issue>` | Automatischer Tag `vX.Y.Z`, Bundle `X.Y.Z.<Run-Nummer>` und Release-Entwurf |
| Manueller Start | Dreiteilige Basisversion oder aktuelle Issue-Version, mit Buildnummer; kein offizielles Release |

Die Notes stehen direkt in GitHub und nennen die enthaltenen Issues und PRs. Vertraulich gekennzeichnete Einträge enthalten nur IDs. Mehrere normale Merges sind vor einem Release möglich. Eine neue offizielle Version benötigt ein neues Release-Issue und einen gleichnamigen Milestone. Entwicklungsartefakte bleiben 14 Tage verfügbar; Releases bis zu ihrer Löschung. Der letzte Einrichtungspush ist die Ausnahme; anschließend ist main durch den erforderlichen PR- und Build-Ablauf geschützt.

## 4. Wiki einrichten

GitHub Free unterstützt Wikis in öffentlichen Repositories. Für ein zunächst privates Wiki ist GitHub Pro beziehungsweise Team erforderlich. Ohne diesen Tarif bleiben die Anleitungen privat unter `docs/` verfügbar; erzeugte Wiki-Seiten und PDF stehen zusätzlich in Actions-Artefakten bereit. Es wird keine öffentliche Dokumentationsseite angelegt.

1. Im Repository **Settings → General → Features → Wikis** aktivieren, sofern verfügbar.
2. Unter **Wiki** einmal eine Seite namens **Home** erstellen. GitHub initialisiert damit das separate Wiki-Git-Repository.
3. Einen separaten GitHub-Zugang für die Wiki-Synchronisierung bereitstellen. Beim klassischen Personal Access Token ist für ein privates Wiki der Scope `repo` erforderlich. Das Token im Repository als Actions-Secret `WIKI_TOKEN` speichern; Ablaufdatum und Erneuerung beachten. Das Token nicht in die Workflow-Datei schreiben.
4. Unter **Actions → Dokumentation und Wiki → Run workflow** starten.

Die Synchronisierung überträgt alle Markdown-Anleitungen als einzelne Wiki-Seiten und erstellt Startseite, Seitenleiste sowie interne Verweise. Abbildungen verweisen auf den dazugehörigen Commit im Hauptrepository. Bei Änderungen an `docs/` auf `main` erfolgt die Aktualisierung automatisch. Änderungen direkt im Wiki werden beim nächsten Lauf für die automatisch erzeugten Seiten überschrieben; deshalb Anleitungen im Hauptrepository bearbeiten. Andere manuell angelegte Wiki-Seiten werden nicht gelöscht. Bei umbenannten oder entfernten Quelldateien bleiben alte Wiki-Seiten bestehen und können manuell entfernt werden.

Fehlen beide Wiki-Zugänge, erzeugt der Workflow dennoch PDF und Wiki-Dateien und weist auf die fehlende Synchronisierung hin. Ist ein Zugang vorhanden, aber das Wiki oder seine Berechtigung ungültig, schlägt die Synchronisierung sichtbar fehl. Das Standard-Workflow-Token wird nicht als allgemeiner Ersatz für einen Wiki-Zugang vorausgesetzt.

Die automatische Aktualisierung ist seit 13.10.2026 mit `WIKI_SSH_KEY` eingerichtet. Der öffentliche Deploy-Schlüssel **Wiki documentation automation** hat Schreibrecht ausschließlich für dieses Repository, einschließlich seines Wikis. Er ist nicht auf einzelne Wiki-Seiten beschränkt. Der private Teil liegt nur im verschlüsselten Actions-Secret. Der Workflow erstellt kurzzeitig eine Runner-Temp-Datei mit eingeschränkten Zugriffsrechten, überprüft die GitHub-Hostschlüssel anhand der HTTPS-Metadaten und entfernt die temporären Dateien danach. Die lokale Einrichtungskopie wurde gelöscht. Deploy-Schlüssel haben kein automatisches Ablaufdatum und lassen sich unter **Settings → Deploy keys** widerrufen.

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

Die Signier-Secrets bleiben als Secrets gespeichert. PR-Builds erhalten den produktiven Signierschlüssel nicht und verwenden ein separates Entwicklungszertifikat. Die produktive Signierung läuft auf main, bei manuellen Main-Builds oder beim Release des bereits gemergten Main-Commits. Schreibrechte zum Repository nur vertrauenswürdigen Personen geben, da Workflow-Änderungen Zugang zu Signier-Secrets ermöglichen können.

## Quellen

- [GitHub: Verfügbarkeit von Wikis](https://docs.github.com/en/communities/documenting-your-project-with-wikis/about-wikis)
- [GitHub: Secrets in Actions](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets)
- [GitHub: GITHUB_TOKEN](https://docs.github.com/en/actions/concepts/security/github_token)
