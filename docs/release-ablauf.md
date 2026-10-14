# Releases über GitHub-Issues und Pull Requests

Release Notes stehen ausschließlich im jeweiligen GitHub-Release. Es gibt
keine eingecheckte Release-Notes-Datei oder CHANGELOG im Projekt.

## Eine Version vorbereiten

Ein Issue mit Label `release` und Titel **Release 1.0.14** legt die gemeinsame
Basisversion fest. Ordne ihm den Milestone **1.0.14** zu. Es darf nur ein offenes
Release-Issue geben. Main- und PR-Builds lesen dieselbe Quelle; ohne offenes
Issue bleibt die letzte Release-Version maßgeblich. Die Repository-Variable
`RELEASE_VERSION` ist lediglich ein Fallback vor dem ersten Release-Issue.

Tag und Release heißen `v1.0.14` beziehungsweise `1.0.14`. MSIX benötigt eine
vierte Versionsstelle: Entwicklungsbuilds heißen `1.0.14.<Run-Nummer>`, das
offizielle Paket `1.0.14.65535`. Die reservierte höchste Revision erlaubt ein
Upgrade von Entwicklungsbuilds derselben Basisversion. Ab Run-Nummer 65535
stoppt der Build, statt eine widersprüchliche Versionsnummer zu erzeugen.
Neue offizielle Veröffentlichungen benötigen eine neue Basisversion.
Die interne CLR-AssemblyVersion bleibt dabei `X.Y.Z.0`; FileVersion und
MSIX tragen die jeweilige Build- beziehungsweise Release-Revision. Dadurch
wird die für CLR-Assemblies unzulässige Revision 65535 dort nicht verwendet.

## Mehrere Änderungen sammeln

Erstelle und merge beliebig viele normale Pull Requests. Das erzeugt Tests
und Entwicklungsartefakte, aber weder Tag noch Release. Die Issues der Version
sollten dem entsprechenden Milestone zugeordnet sein. Ein abschließender
Release-PR enthält im Beschreibungstext ausdrücklich **Closes #1**, wobei #1
die tatsächliche ID des Release-Issues ist. Dieser PR muss die erforderlichen
Buildprüfungen bestehen und nach main gemergt werden.

Erst dieser Merge erzeugt den Tag am exakten Merge-Commit und ruft den
Release-Build direkt auf. Das ist notwendig, weil Tags, die der normale
Actions-Token anlegt, keine weitere Push-Automation auslösen. Das signierte
Paket, Quellen, Hinweise und Prüfnachweise landen zunächst in einem
**Release-Entwurf**. Prüfe ihn und veröffentliche ihn anschließend bewusst.
Bei normalem PR-Build wird ein separates Entwicklungszertifikat verwendet;
der produktive Signierschlüssel wird dafür nicht bereitgestellt.

## Konkret zur ersten Version 1.0.14

PR #6 ist gemerged, schließt aber nur das Dokumentations-Issue ab. Für das offizielle
Release muss der abschließende Release-PR zusätzlich **Closes #1** enthalten.
Nach bestandenem Build und Merge erzeugt die Release-Issue-Automatik `v1.0.14` und
den Entwurf mit `1.0.14.65535`. Öffne anschließend GitHub → Releases, prüfe den
Entwurf samt MSIX, Zertifikat, Quellen und Lizenznachweisen und veröffentliche ihn.
Das manuelle Schließen des Release-Issues allein erzeugt keinen Tag.

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
