# Releases über GitHub-Issues und Pull Requests

Versionsbezogene Release Notes werden direkt im GitHub-Release erzeugt. Die historische Übersicht im [CHANGELOG](../CHANGELOG.md) dokumentiert die Entwicklungsetappen.

## Planung und Softwareversion getrennt halten

Ab dem erfolgreichen Release-Build von **1.2.3** heißt die nächste Planung **Release R1**, danach **Release R2** usw. Der Milestone trägt denselben Namen. Die Nummer bezeichnet den geplanten Lieferumfang und bleibt unabhängig davon, ob daraus später beispielsweise 1.2.4, 1.3.0 oder 2.0.0 wird.

Die Automatik legt die nächste Planung erst nach einem erfolgreichen offiziellen Paket-Build mit MSIX-Bundle an. Der Release-Entwurf muss dafür noch nicht öffentlich veröffentlicht sein. Es gibt weiterhin genau **ein offenes Issue mit Label `release`** als gemeinsame Planungsquelle. Beliebig viele Feature-PRs können vorher gesammelt und gemergt werden.

Issues und PRs dem Milestone **Release R1** zuordnen. Eine spätere Versionsentscheidung ändert diesen Milestone nicht; die bereits zugeordneten Arbeiten müssen nicht umsortiert werden.

## Zielversion erst zum Release-Abschluss auswählen

Im automatisch angelegten Issue steht zunächst dieser HTML-Kommentar:

```html
<!-- release-version: -->
```

Erst wenn der Lieferumfang feststeht, dort die gewünschte Version mit drei Zahlen eintragen, zum Beispiel:

```html
<!-- release-version:1.3.0 -->
```

Den vorhandenen Marker **ersetzen**, keinen zweiten hinzufügen. Der Issue-Titel **Release R1** und der gleichnamige Milestone bleiben bestehen. Nur einen höheren Versionsstand als die ausgelieferten Pakete wählen. Main- und PR-Builds verwenden ab dieser Festlegung dieselbe Basisversion. Bereits gebaute Pakete und vorhandene Tags werden nicht verändert.

Solange noch keine Zielversion festgelegt ist, verwenden Entwicklungsbuilds die zuletzt abgeschlossene Version mit einer neuen fortlaufenden Buildnummer. Die Automatik hält diese Basis im Marker `release-build-base` fest; diesen internen Herkunftsvermerk nicht als Zielversion bearbeiten. Ohne diesen Vermerk wird die letzte versionierte Planung beziehungsweise die Repository-Variable `RELEASE_VERSION` als Fallback verwendet.

Ein offizielles Release ohne Zielversion wird abgelehnt, bevor ein Tag erzeugt wird. Ungültige Versionen, doppelte Versionsmarker und mehrere offene Release-Issues werden ebenfalls abgelehnt. Die Auswahl muss drei Zahlen zwischen 0 und 65535 enthalten.

## Abschluss-PR, Tag und Release-Entwurf

1. Alle vorgesehenen Feature-PRs nach `main` mergen.
2. Die Zielversion im Planungs-Issue festlegen.
3. Einen Release-PR mit eindeutigem Namen wie **Release 1.3.0** erstellen. Er enthält ausdrücklich `Closes #<Planungs-Issue>` und kann die ausgewählte Version mit einem einzelnen Marker `release-version:1.3.0` bestätigen. Widerspricht dieser Marker der Issue-Version, wird der Tag nicht erzeugt.
4. Die erforderlichen Prüfungen bestehen lassen und den PR nach `main` mergen.
5. Die Automatik setzt **v1.3.0** am exakten Merge-Commit und erstellt signierte Pakete sowie einen **Release-Entwurf** mit Notes und Download-Anlagen.
6. Paket, Dokumentation und Lizenznachweise prüfen, danach den Entwurf bewusst veröffentlichen.

Ein normaler Feature-Merge oder das alleinige Schließen des Planungs-Issues erzeugt kein Release. Der Main-Run übernimmt den offiziellen Build im bestehenden MSIX-Workflow; der vom Actions-Token erzeugte Tag benötigt keinen zusätzlichen Push-Workflow.

Nach dem erfolgreichen offiziellen Build wird die nächste nummerierte Planung ohne vorgegebene Zielversion angelegt. Wiederholte Builds desselben Tags erzeugen kein zweites Planungs-Issue. Bereits abgeschlossene Folgeplanungen werden bei einem alten Build-Rerun nicht neu angelegt. Eine vorhandene spätere offene Planung bleibt erhalten.

## Release 1.2.3 abschließen

Das bestehende Issue **#68 – Release 1.2.3** und sein Milestone **1.2.3** bleiben für diesen Abschluss gültig. Der Release-PR schließt **#68**. Nach seinem Merge und bestandenem Paket-Build entstehen **v1.2.3**, die Pakete mit fortlaufender Buildrevision und der Release-Entwurf.

Danach legt die neue Automatik **Release R1** samt Milestone an. Eine Zielversion wie 1.2.4 wird ausdrücklich **noch nicht** festgelegt. Die Version wird erst beim nächsten Abschluss ausgewählt.

Bisherige Planungen mit Titel **Release X.Y.Z** bleiben kompatibel. Bei ihnen ist weiterhin der Titel die Versionsquelle und X.Y.Z der Milestone. Eine zusätzliche Versionsangabe im Body muss damit übereinstimmen. Bereits veröffentlichte Tags, Versionen und historische Milestones werden nicht umbenannt.

Für ältere Planungen wird ein vorhandener Marker `next-release-version:X.Y.Z` weiterhin berücksichtigt und die ausdrücklich vorgemerkte Folgeversion in die nächste nummerierte Planung übernommen. Ohne diese ausdrückliche Vorgabe gibt es keine automatische Erhöhung der Softwareversion mehr.

## Fortlaufende Buildnummer und MSIX

Tag und Release verwenden drei Versionsstellen, zum Beispiel **v1.3.0**. Die vierte MSIX-/FileVersion-Stelle ist weiterhin die `GITHUB_RUN_NUMBER` des bestehenden Workflows **msix.yml**, zum Beispiel **1.3.0.123**. PR-, Main-, manuelle und offizielle Builds teilen sich diesen Zähler im Repository. Andere Workflows, beispielsweise die Wiki-Aktualisierung, verbrauchen keine Buildnummern.

Der Zähler läuft über Basisversionswechsel hinweg weiter. Lücken durch abgebrochene oder fehlgeschlagene Runs sind normal. Größere Nummern bedeuten später gestartete Runs, nicht zwingend neueren Quellcode oder spätere Fertigstellung. Eine Wiederholung desselben Runs behält seine Nummer. `BUILD-INFO.json` enthält zusätzlich Run-ID, Versuchszahl, Commit und den Link zum Run.

Workflow-Name und -Pfad des MSIX-Builds bleiben erhalten, damit der Zähler nicht durch einen neuen Workflow neu beginnt. Die vollständige Run-ID wird nicht auf eine MSIX-Zahl gekürzt oder gehasht. Ab Run-Nummer 65536 stoppt der Build; vorher ist eine neue dokumentierte Vergabestrategie erforderlich. Die CLR-AssemblyVersion bleibt X.Y.Z.0.

Historische Pakete wie **1.0.15.65535** bleiben erhalten. Neue Pakete benötigen zum Aktualisieren einen höheren gesamten MSIX-Versionsstand. Die frühere reservierte Release-Revision .65535 wird für neue Builds nicht mehr verwendet.

## Release Notes und Anlagen

Die Notes nennen die gemergten PRs der aufgenommenen Commits seit dem vorherigen veröffentlichten regulären Release-Tag. Dazu gehören Issues des zugehörigen Release-Milestones – auch bei neutralem Namen wie **Release R1** –, verknüpfte Abschluss-Issues sowie im Zeitraum erstellte oder geschlossene Issues ohne anderen Milestone. Das erste Release betrachtet die bisherige Git-Historie.

Bodies und Kommentare werden nicht übernommen. Mit `confidential`, `vertraulich`, `private`, `security` oder `security-sensitive` markierte Einträge erscheinen ausschließlich mit ID und einem Hinweis auf ausgeblendeten Inhalt. Ein solches Label macht das Original-Issue im öffentlichen Repository nicht vertraulich; geheime Meldungen benötigen einen privaten Meldeweg.

MSIX-Bundle, Kommandozeile, KI-Paket, Dokumentation, passende Quellen und Lizenznachweise liegen als Anlagen im Release-Entwurf. Ab 1.2.4 liegt die separat ausführbare Entra-Anmeldeanwendung als eigenes Windows-x64-ZIP mit SHA-256-Prüfsumme bei. Beide Pakete tragen dieselbe Buildversion. Ein veröffentlichter Release-Text wird von einem erneuten Lauf nicht überschrieben. Ohne erfolgreiches MSIX-Bundle beginnt keine neue Planung.

## Main-Schutz

Änderungen erfolgen über Pull Requests. Direkt- und Force-Pushes sowie das Löschen von `main` bleiben blockiert, auch für Administratoren ohne Änderung der Schutzkonfiguration. Vor dem Merge muss `build` erfolgreich und die Branch aktuell sein. Offene Review-Diskussionen müssen abgeschlossen sein; ein zweiter Pflichtreviewer ist für dieses von einer Person betreute Repository nicht vorgesehen. Der Wiki-Schlüssel erhält keine Main-Ausnahme.
