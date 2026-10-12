# ADR 0006 – Identität, Rechte und unveränderliche Release-Pakete

Stand: 02.10.2026 · Status: angenommen für Etappe 7

## Anmeldung

* `IIdentityProvider` mit zwei Umsetzungen: `MsalIdentityProvider` (Desktop, MSAL + WAM, Microsoft.Identity.Client
  und .Broker) und `SimulatedIdentityProvider` (Entwicklungsmodus, Tests). Der Produktcode prüft keine Passwörter.
* Interaktiv mit **Kontoauswahl**, nie still mit dem Windows-Konto: Der Prüfplatz läuft unter der Sitzung des Werkers;
  die Rechte sollen der Person gehören, die sich ausdrücklich anmeldet (A01).
* `SessionService`: Rechte entstehen erst nach Bestätigung; während der Anmeldung und beim Kontowechsel gelten keine
  erweiterten Rechte. Abmelden entzieht sie sofort. Die Engine kennt die Anmeldung nicht – laufende Tests laufen weiter.
  Entwicklereingriffe werden mit dem UPN der angemeldeten Person protokolliert.
* Mit Entra-Konfiguration werden nur bestätigte Konten des konfigurierten Mandanten akzeptiert.

## Rechte (Entscheidung: lokal je Prüfplatz)

* `%ProgramData%\Testumgebung\zugang.json`: Client-/Mandanten-ID, Inaktivitätssperre, Liste UPN → Entwickeln/Freigeben.
  Für alle Windows-Benutzer gleich, Schreibrecht nur für Administratoren (siehe `docs/entra-registrierung.md`).
* Fehlt die Datei: Entwicklungsmodus (simulierte Anmeldung, alle Rechte). Ist sie beschädigt: niemand hat Rechte.
* Seiten: Editor, Debugger, Bibliothek, Ergebnisse, Einstellungen → Entwickeln; Releases → Entwickeln oder Freigeben;
  Freigabe-Aktion → Freigeben.

## Release-Pakete (Entscheidung: in der Prüfdatenbank)

* Paket = ZIP mit `manifest.json` (Bereichshashes SHA-256: Ablauf, Bausteine, Codeartefakte je Sprache, Kurven,
  Definitionen), `projekt.tproj.json` und Quelltexten unter `code/`. Der SHA-256 der Paketbytes steht am Release.
* Lebenszyklus: **Entwurf** (Recht Entwickeln; Version eindeutig) → **Entwicklungsprüfung** (Entwickeln; Integrität,
  Validierung, Build aller Codeartefakte) → **Freigegeben** (Freigeben; ausdrückliche Bestätigung; Paket-Hash wird
  erneut geprüft; die bisherige Freigabe desselben Ablaufs wird **archiviert**).
* Unveränderlich: Pakete werden nur eingefügt, nie geändert. Änderungen erfordern einen neuen Entwurf mit neuer Version.
  Zustandswechsel sind bedingte Updates (nur aus dem erwarteten Zustand) in einer Transaktion mit dem Audit-Eintrag.
* Ablage: `tu_releases` und `tu_audit_log` (Schema v2) in der eingestellten SQL-Datenbank; ohne SQL-Datenbank
  (simuliert/keine) ein lokaler Ordner `%LOCALAPPDATA%\Testumgebung\Releases`.
* Empfehlung für den Betrieb: dem Datenbankkonto der Prüfplätze kein `UPDATE` auf `tu_releases.package` und kein
  `UPDATE`/`DELETE` auf `tu_audit_log` erlauben (Spaltenrechte bzw. Trigger je System).

## Audit-Log

* Nur anhängen; jeder Eintrag enthält den Hash des Vorgängers (Hash-Kette). Änderungen, Lücken und Löschungen fallen
  bei der Anzeige auf („Hash-Kette geprüft“). Parallele Schreiber (mehrere Prüfplätze) wiederholen bei Schlüsselkonflikt.
* Protokolliert: Anmeldung/Abmeldung (auch abgelehnte), Entwurf, Entwicklungsprüfung (bestanden/nicht bestanden),
  Freigabe, Entwicklereingriffe im Lauf (Variablen, Run-Patch, gezielte Ausführung).

## Werkerbetrieb und Versionswechsel (V01)

* Werker starten nur die freigegebene Version; ohne Freigabe kein Start. Entwickler können ausdrücklich den
  **Entwurf testen** (wird als nicht freigegeben angezeigt; „Simulation starten“ im Editor nutzt immer den Entwurf).
* Neue Freigabe während eines Laufs: Die Engine fragt `IVersionSource` **nur beim Eintritt in den nächsten
  Prüflingstest**. Der laufende Prüfling behält seine Version (Ergebnis trägt seine Version).
* Übernahme nur, wenn Hauptablauf-Variablen, Init, Finalization, Loop außerhalb des Prüflings und dort verwendete
  Bausteine unverändert sind (`DefinitionCompatibility`); sonst Hinweis „wird beim nächsten Start aktiv“.
* Freigaben anderer Prüfplätze werden alle 30 s aus der gemeinsamen Datenbank übernommen.

## Offen

* Verhalten bei automatischer Abmeldung im pausierten Test ist umgesetzt als „Pause bleibt, nichts wird übernommen“;
  fachliche Bestätigung steht aus (Abschnitt 15).
* Rechteverwaltung zentral (App-Rollen) bleibt als Option; die Schnittstelle `AccessConfig.RightsFor` ist der Ansatzpunkt.
* Kurven werden Teil des Pakets, sobald der Kurveneditor existiert (Etappe 8).
