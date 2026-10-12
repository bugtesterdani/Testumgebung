# ADR 0005 – Datenbankadapter und MQTT-Meldeweg

Stand: 02.10.2026 · Status: angenommen für Etappe 6

## Datenbankadapter

* Ein Adapter (`SqlRunDatabase`) für alle vier Systeme über ADO.NET (`DbProviderFactory`); das Projekt
  `Testumgebung.DatabaseAdapters` hat keine Treiberabhängigkeit. Die Anwendung bindet die Treiber ein:
  Microsoft.Data.SqlClient, Npgsql, MySqlConnector, Microsoft.Data.Sqlite.
* Providerunterschiede stecken in `SqlDialect` (Typen, `CREATE TABLE IF NOT EXISTS` bzw. `IF OBJECT_ID` bei SQL Server,
  `ENGINE=InnoDB DEFAULT CHARSET=utf8mb4` bei MySQL). Parameter heißen überall `@name`.
* Parameter sind explizit typisiert, auch `NULL` (Npgsql und SqlClient lehnen untypisiertes `NULL` für Zahlen- und
  Binärspalten ab).
* Zeiten portabel als UTC-Ticks (`BIGINT`) plus lesbarer ISO-8601-Text; Flags als `BIGINT` 0/1.

### Schema v1 (migrationsfähig)

| Tabelle | Schlüssel | Inhalt |
|---|---|---|
| `tu_schema_version` | version | angewandte Migrationen |
| `tu_definitions` | definition_id (Hash aus Name, Version, Struktur) | Testname, Version, Ablaufstruktur als JSON |
| `tu_runs` | **run_id** | Prüfplatz, Kennung, Start/Ende, Bewertung, WERROR, Entwicklungslauf |
| `tu_step_executions` | run_id, seq | Schritt-/Gruppenereignisse mit Pfad, Iteration, Bewertung, Meldung |
| `tu_measurements` | run_id, seq | typisiert: value_double / value_int / value_text, Einheit, Grenzen |
| `tu_waveform_chunks` | run_id, waveform_no, part | Kurven in Blöcken zu 65 536 Samples (`f64le`), optional Zeitpunkte |
| `tu_operator_inputs` | run_id, seq | Werkereingaben mit Anweisungstext |
| `tu_development_events` | run_id, seq | Entwicklereingriffe |

* Gespeichert werden genau die für die Datenbank gefilterten Daten (Outbox-Eintrag), keine Rohdatenkopie.
* Migrationen: `SchemaMigrator` legt `tu_schema_version` an und wendet fehlende Versionen je in einer Transaktion an.
  Referenzskripte für DBAs: `docs/sql/schema-*.sql` (ein Test hält sie aktuell).

### Idempotenz und Transaktion

* Eine Transaktion je Run. Ist `run_id` schon vorhanden, wird ohne Einfügen bestätigt – auch nach einem Commit, dessen
  Antwort verloren ging (P05). Ein Wettlauf zweier Einfügungen scheitert am Primärschlüssel und gelingt beim nächsten
  Outbox-Versuch als „bereits vorhanden“.
* `DatabaseContractCheck` weist zur Laufzeit gegen die eingestellte Datenbank nach: Speichern, idempotente Wiederholung,
  Antwortverlust nach Commit, bitgenaue Kurven; die Test-Runs `VERTRAG-…` werden danach gelöscht.

### Prüfung in Etappe 6

* SQLite: vollständig über die native Bibliothek (Testprovider `NativeSqlite`, unter Windows `winsqlite3.dll`).
* PostgreSQL 16: Schema und alle erzeugten Anweisungen mit streng typisierten Parametern gegen einen echten Server ausgeführt.
* MySQL und T-SQL: Schema und Anweisungen syntaktisch je Dialekt geprüft.
* Auf dem Entwicklungsrechner: dieselben Vertragstests mit den echten Treibern über `TESTUMGEBUNG_TEST_MSSQL`,
  `TESTUMGEBUNG_TEST_POSTGRES`, `TESTUMGEBUNG_TEST_MYSQL` sowie die Vertragsprüfung in den Einstellungen.

## MQTT-Meldeweg

* Eigener MQTT-3.1.1-Client ohne Bibliothek: CONNECT → CONNACK → PUBLISH (QoS 0, nicht retained) → DISCONNECT.
  Je Meldung eine kurze Verbindung, damit gibt es keinen Reconnect-Puffer und keine Sitzungsdaten.
* TLS über `SslStream` mit Systemvertrauen; optional eine Firmen-CA (PEM/DER) als zusätzliche Wurzel. Der Hostname
  muss immer passen; ein Abschalten der Zertifikatsprüfung ist bewusst nicht vorgesehen.
* `MqttNotifier`: `Notify` blockiert nie; ein Speicherpuffer (64 Meldungen, bei Überlauf verworfen) speist einen
  Sende-Thread mit genau einem begrenzten Versuch je Meldung (Standard 5 s). Fehler werden angezeigt, nicht wiederholt (P06).
* Ereignisse (aus der Outbox): `stored` (nach DB-Commit), `store_failed` (je Run), `retry_failed` (je Run und Versuch),
  `stored_later`, `outbox_damaged`. Nachricht: runId, dutId, event, time, attempt, error, station, application.

## Einstellungen

* Datei `%LOCALAPPDATA%\Testumgebung\einstellungen.json`; Verbindungsstring und MQTT-Passwort per DPAPI
  (benutzergebunden). Nicht entschlüsselbare Werte führen zu einem Hinweis, nicht zum Absturz.
* `ResultServices` tauscht Datenbank (`SwitchableRunDatabase`), Meldeweg (`SwitchableNotifier`), Ergebnisordner und
  Outbox-Parameter zur Laufzeit; offene Outbox-Einträge gehen an die neu eingestellte Datenbank.

## Offen

* Abfrage- und Auswerteoberfläche über die Datenbank (bisher nur Schreiben und Prüfen).
* Weitere Meldeadapter (z. B. OPC UA, HTTP) über `IRunNotifier`.
* Migration v2 nach Bedarf (z. B. Anhänge/Bilder in der Datenbank).

Nachtrag (ADR 0014): Die ersten beiden Punkte sind umgesetzt – Auswertung und Prüfberichte lesen auch aus der Datenbank
(`IRunQuerySource`), und es gibt Meldeadapter für HTTP und OPC UA.
