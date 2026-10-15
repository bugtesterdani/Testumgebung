# Releases über GitHub-Issues und Pull Requests

Release Notes stehen ausschließlich im jeweiligen GitHub-Release. Es gibt
keine eingecheckte Release-Notes-Datei oder CHANGELOG im Projekt.

## Eine Version vorbereiten

Ein Issue mit Label `release` und Titel **Release 1.0.16** legt die gemeinsame
Basisversion fest. Ordne ihm den Milestone **1.0.16** zu. Es darf nur ein offenes
Release-Issue geben. Main- und PR-Builds lesen dieselbe Quelle; ohne offenes
Issue bleibt die letzte Release-Version maßgeblich. Die Repository-Variable
`RELEASE_VERSION` ist lediglich ein Fallback vor dem ersten Release-Issue.

Tag und Release heißen `v1.0.16` beziehungsweise `1.0.16`. Die vierte
MSIX-Versionsstelle ist die GitHub-`GITHUB_RUN_NUMBER` des Workflows `msix.yml`,
beispielsweise `1.0.16.123`. PR-, Main-, manuelle und offizielle Release-Builds
laufen in diesem einen Workflow und teilen dadurch denselben Zähler im Repository.
Andere Workflows, etwa die Wiki-Aktualisierung, verbrauchen keine Buildnummern.
Der Zähler läuft auch über Basisversionswechsel hinweg weiter; Lücken durch
abgebrochene oder fehlgeschlagene Runs sind normal. Größere Buildnummern bedeuten
später gestartete Runs, nicht zwingend neueren Quellcode oder spätere Fertigstellung.
Eine Wiederholung desselben Runs behält die Nummer. `BUILD-INFO.json` enthält
zusätzlich die vollständige Run-ID, Versuchszahl, Commit und den Link zum Run.
Die vierte Stelle identifiziert den Run; Run-ID plus Versuch identifizieren den Versuch.

Der vorhandene Workflow und sein Name/Pfad sollen deshalb nicht durch einen neuen
Workflow ersetzt werden: Dessen Zähler könnte neu beginnen. GitHub-Zähler gelten je
Workflow, nicht global über alle Workflows einer Organisation. Die vollständige
Run-ID passt nicht in die MSIX-Versionsstelle und wird weder gekürzt noch gehasht.
Ab Run-Nummer 65536 stoppt der Build ausdrücklich. Vor Erreichen dieser Grenze
benötigt die Vergabe eine neue, dokumentierte Strategie.

Die bisherige reservierte Release-Revision `.65535` entfällt für zukünftige Builds.
Bereits veröffentlichte Versionen, insbesondere `1.0.15.65535`, bleiben erhalten.
Neue Pakete benötigen daher eine höhere Basisversion (aktuell `1.0.16`), um diese
Installation aktualisieren zu können. Die CLR-AssemblyVersion bleibt `X.Y.Z.0`;
FileVersion und MSIX tragen die gemeinsame Buildrevision.

## Mehrere Änderungen sammeln

Erstelle und merge beliebig viele normale Pull Requests. Das erzeugt Tests
und Entwicklungsartefakte, aber weder Tag noch Release. Die Issues der Version
sollten dem entsprechenden Milestone zugeordnet sein. Ein abschließender
Release-PR enthält im Beschreibungstext ausdrücklich **Closes #1**, wobei #1
die tatsächliche ID des Release-Issues ist. Dieser PR muss die erforderlichen
Buildprüfungen bestehen und nach main gemergt werden.

Erst dieser Merge erzeugt den Tag am exakten Merge-Commit und führt den
Release-Build im selben MSIX-Workflow aus. Tags, die der normale
Actions-Token anlegt, lösen keine weitere Push-Automation aus; der Main-Run
übernimmt deshalb auch das offizielle Paket und den Release-Entwurf. Das signierte
Paket, Quellen, Hinweise und Prüfnachweise landen zunächst in einem
**Release-Entwurf**. Prüfe ihn und veröffentliche ihn anschließend bewusst.
Bei normalem PR-Build wird ein separates Entwicklungszertifikat verwendet;
der produktive Signierschlüssel wird dafür nicht bereitgestellt.

## Release 1.0.16 abschließen

Die Planung erfolgt im Issue **#12 – Release 1.0.16** mit dem Milestone **1.0.16**.
Die vorgesehenen Feature-PRs sind gemerged. Der abschließende Pull Request für
Version 1.0.16 enthält **Closes #12** und muss vor dem Merge den erforderlichen
Build bestehen. Er wird über den normalen Main-Schutz gemergt.

Nach diesem Merge setzt die bestehende Automatik den Tag **v1.0.16** auf den
Merge-Commit und erstellt das signierte MSIX-Bundle mit der fortlaufenden
Buildnummer sowie den Release-Entwurf. Die Release Notes werden direkt auf
GitHub aus den aufgenommenen Pull Requests und Issues erzeugt. Dort liegen
auch KI-Paket, Dokumentation, Quellen und Lizenznachweise als Anlagen.

Den fertigen Entwurf unter GitHub → Releases prüfen und anschließend bewusst
veröffentlichen. Der erfolgreiche Paket-Build legt bereits die bearbeitbare
Planung für **1.0.17** an. Das manuelle Schließen von #12 allein erzeugt keinen Tag.

## Nächste Version automatisch planen und bearbeiten

Nach erfolgreichem offiziellen Paket-Build, Release-Entwurf und Release Notes legt
die Automatik ein Issue **Release 1.0.15** mit Label `release` und passendem Milestone
an. Standardmäßig wird die dritte Versionsstelle erhöht. An der MSIX-Grenze erfolgt
ein Übertrag in die nächste Stelle. Das neue Issue entsteht bereits beim fertigen
Entwurf; die öffentliche Veröffentlichung bleibt eine bewusste Entscheidung.

Du kannst den Titel später beispielsweise in **Release 1.1.0** ändern. Drei
Versionsstellen sind erforderlich. Die Issue-Automatik erstellt beziehungsweise
verwendet den passenden Milestone und ordnet ihn dem Release-Issue zu. Feature-Issues
und PRs werden nicht still verschoben; ändere deren Milestone bei Bedarf selbst.
Main- und PR-Builds verwenden den aktuellen Issue-Titel; bereits gebaute Pakete und
vorhandene Tags ändern sich dadurch nicht. Verwende eine höhere Version als die
bereits ausgelieferten Versionen.

Wiederholte Builds desselben Tags erzeugen keine doppelten Planungs-Issues. Eine
bereits vorhandene spätere offene Planung wird nicht überschrieben. Auch ein
umbenanntes und später geschlossenes Folge-Issue wird beim Wiederholen des alten
Builds nicht erneut erstellt. Mehrere offene Release-Issues sind ein Fehler, weil
sonst die gemeinsame Versionsquelle unklar wäre. Ohne erfolgreiches MSIX-Bundle
wird keine nächste Planung begonnen.

## Inhalt der automatisch erzeugten Notes

Die Notes nennen alle gemergten PRs, die zu den Commits seit dem vorherigen
Release-Tag gehören. Hinzu kommen alle Issues des Versions-Milestones,
deren verknüpfte Abschluss-Issues und im Zeitraum erstellte/geschlossene Issues
ohne anderen Versions-Milestone. Das erste Release betrachtet die gesamte
bisherige Historie. IDs und der Zustand öffentlicher Issues werden aufgeführt.
Die Bodies und Kommentare werden nicht in Release Notes übernommen.

Labels `confidential`, `vertraulich`, `private`, `security` oder
`security-sensitive` blenden Titel und Details aus; dann steht dort ausschließlich
beispielsweise **PR #42: Vertraulicher Eintrag (Inhalt ausgeblendet)**.
GitHub bietet damit keine echte Vertraulichkeit einzelner Issues: Ein Label
verhindert nicht den Zugriff auf den Originaleintrag in einem öffentlichen
Repository. Wirklich geheime Inhalte benötigen einen privaten Meldeweg.

## Main-Schutz

Nach der einmaligen Einrichtung erfolgen Änderungen nur über Pull Requests.
Direkt- und Force-Pushes sowie das Löschen von main sind blockiert, auch für
Administratoren ohne Änderung der Schutzkonfiguration. Vor Merge muss der
Statuscheck `build` erfolgreich sein und die PR-Branch aktuell sein.
Für dieses derzeit von einer Person betreute Repository wird keine zweite
Person als Pflichtreviewer verlangt; offene Review-Diskussionen müssen jedoch
abgeschlossen sein. Der Wiki-Schlüssel erhält keine Main-Ausnahme.
