# Vorbereitung der öffentlichen GitHub-Freigabe

Stand: 13.10.2026. Repository: `bugtesterdani/Testumgebung`, derzeit privat.
Die Vorbereitung schaltet weder das Repository noch einen Release öffentlich.

## Im Projekt umgesetzt

- MIT für eigenen Code; Originaltexte und Drittanbieterquellen getrennt.
- Microsoft-.NET-/SDK-Originalverträge, Textextraktionen und Herkunftsnachweise
  unter `licenses/third-party/microsoft/`; genaue Ausnahmen im Binärinventar.
- Zustimmung vor Nutzung mit den vollständigen einschlägigen Originaltexten.
  Ändert sich einer dieser Texte, ist eine erneute Zustimmung nötig.
  Fehlende Texte eines gebündelten Runtimes verhindern den Start.
- Lizenz-/Quellenfenster ohne Anmeldung, Hinweise zur Datenverarbeitung.
- Release mit separat herunterladbaren Quellen, Originalhinweisen, Prüfsummen
  und technischem Verifikationsbericht. Tests und Dateiprüfung laufen in Actions.
- Sicherheits- und Beitragsrichtlinien; ausgewählte Token-/Schlüsselmuster und
  private Dateiendungen werden in der erreichbaren Git-Historie geprüft.
- Tags erstellen zunächst einen **Release-Entwurf**. Erst nach Prüfung des
  konkreten Pakets wird er bewusst veröffentlicht. Main-Builds bleiben Artefakte.

## Vor dem öffentlichen Umschalten

Der Rechteinhaber hat private Erstellung und eigene Rechte bestätigt. Für die
Microsoft-Anteile bleiben die Pflichten der jeweiligen Originalverträge bestehen.
Insbesondere Freistellung/Verteidigung und die Vereinbarung mit Weiterverteilern
benötigen eine bewusste Vertriebsentscheidung; die Software kann deren rechtliche
Wirksamkeit nicht bestätigen. Eine gezielte Prüfung dieser konkreten Klauseln ist
für verbindliche Rechtssicherheit sinnvoll. Dafür liegen die Originalverträge und
der [Microsoft-Bericht](licensing/microsoft-vertrieb.md) vor.

Der Secret-Check erfasst ausgewählte erkennbare Schlüssel und Tokens. Er ersetzt
keine vollständige Prüfung auf jede Art von Firmeninformation oder personenbezogenen
Daten; prüfe auch den Inhalt selbst erstellter Handbuchbilder und Beispiele.
Die Dokumente können den öffentlichen GitHub-Namen und lokale Entwicklungspfade
enthalten. PFX/Passwörter bleiben lokal beziehungsweise in GitHub-Secrets und
gehören nicht in Downloads.

GitHubs private Sicherheitsmeldung ist für öffentliche Repositories vorgesehen.
Nach dem Umschalten unter **Settings → Advanced Security → Private vulnerability
reporting** aktivieren und den Meldeweg aus SECURITY.md prüfen. Die Richtlinie
behauptet ausdrücklich nicht, dass der private Meldeweg bereits aktiv ist.
Siehe [GitHub-Anleitung](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository).

## Konkreter Veröffentlichungsablauf

1. Einen erfolgreich geprüften Commit und eine neue vierteilige Version wählen.
2. Tag `v<Version>` erstellen: Actions erzeugt den signierten Bundle-Stand und
   einen Release-Entwurf mit allen nötigen Anlagen. Keine alten unbereinigten
   Bundles erneut freigeben.
3. Installer, Originaltexte, passende Quellen, Verifikationsbericht und
   Prüfsummen des Entwurfs kontrollieren; auf einem Zielsystem installieren.
4. Den bisherigen Entwicklungssignierer bewusst berücksichtigen: Er ist kein
   allgemein vertrauenswürdiges Zertifikat. Empfänger müssen ihn ausdrücklich
   vertrauen; ein anderer Signierweg erfordert eine passende Migration.
5. Nach der Vertriebsentscheidung Repository öffentlich schalten, privaten
   Sicherheits-Meldeweg aktivieren und den geprüften Release-Entwurf veröffentlichen.

Wiki und Dokumentation stammen aus den eingecheckten Quellen. Hersteller-Treiber,
eigene Nutzer-DLLs und spätere pip-Pakete werden durch diese Freigabe nicht
automatisch mitlizenziert. Produktive Tests mit realen Geräten bleiben vom
automatisierten Softwaretest zu unterscheiden.
