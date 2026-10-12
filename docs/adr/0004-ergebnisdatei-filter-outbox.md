# ADR 0004 – Ergebnisdatei, Filter und Outbox

Stand: 02.10.2026 · Status: angenommen für Etappe 5 (DB-Adapter und MQTT: Etappe 6)

## Ablauf am Prüflingsende

```text
Engine-Ereignisse ──► ResultRecorder (Zwischenaufzeichnung je Prüfling, ungefiltert)
                          │ DutRunCompleted (auch WERROR)
                          ▼
                     ResultPipeline (außerhalb des Engine-Threads)
                     ├─ Filter „Ergebnisdatei“ ─► ResultFileStore: tmp → flush → atomar umbenennen
                     │                              Fehler → Wiederherstellungsordner + deutliche Anzeige
                     └─ Filter „Datenbank“ ────► OutboxService: Enqueue (Datenträger) → Versuch
                                                    Erfolg → löschen · Fehler → 10-s-Warnung, Hintergrund-Retry
```

Die Anzeige filtert der Prüfplatz direkt (Protokoll/Messwerte). Übertragungsstatus und Bewertung sind unabhängig.

## Filter (Abschnitt 9.1)

* Ziele: Anzeige, Ergebnisdatei, Kurven in Datei, Datenbank, Kurven in Datenbank.
* Jeder Knoten hat je Ziel `null` (erben), `true` oder `false`. Effektiver Wert: eigener Override, sonst nächster
  Vorfahre mit Override, sonst **an**. Gruppen liefern damit vererbbare Defaults, Schritte explizite Overrides.
* Ausgeschlossene Ereignisse fehlen vollständig (keine Rohdatenkopie). Kurven sind separat wählbar: Werte ohne Kurven
  und Kurven ohne Werte sind möglich.
* **Technische Mindestmenge** (immer enthalten, im Editor gekennzeichnet): Run-ID, Prüflingskennung, Start/Ende,
  Gesamtbewertung, WERROR, Testname/-version, Ablaufstruktur (IDs, Namen, Bausteintyp).
* Grenze: In Körpern grafischer Bausteine gelten die Einstellungen innerhalb des Körpers (Aufrufstelle vererbt nicht).

## Ergebnisdatei `.tres`, Format v1

| Teil | Inhalt |
|---|---|
| Datei | `TRES`, u16 Formatversion, u16 reserviert, Chunks, Trailer (u64 Indexposition, `TEND`) |
| Chunk | Typ (4 Zeichen), Codec (0 roh, 1 Deflate), gespeicherte Länge, Rohlänge, CRC-32 der gespeicherten Bytes |
| `HEAD` | Run-ID, Prüfplatz, Kennung, Test/Version, Start/Ende (UTC-Ticks + Offset), Bewertung, WERROR, Entwicklungslauf, Filterziel |
| `STRU` | Ablaufstruktur einmalig: Id, Eltern-Id, Name, Art, Bausteintyp, Beschreibung |
| `EVNT` | je 2000 Ereignisse; Pfade/Einheiten als Stringtabelle, Knoten als Index in `STRU` (Schema-Referenzen) |
| `WAVE` | Kurvenblock ≤ 65 536 Samples: Kanal, Einheit, Abtastrate, Start, Qualität, Sync-Gruppe, `f64le`-Array, optional Zeitpunkte |
| `ATTA` | Anhang (z. B. Anweisungsbild), Id = SHA-256 → dedupliziert |
| `INDX` | Typ, Position, Länge, CRC aller Chunks |

* Metadaten sind MessagePack (eigene, abhängigkeitsfreie Teilmenge; mit jeder MessagePack-Bibliothek lesbar).
* Werte sind typisiert (`b`, `i`, `d`, `s`, `t`) – Dateien sind ohne installierte Testversion interpretierbar.
* Lesen: über den Index mit CRC-Prüfung; fehlt der Index (abgeschnitten), sequenziell. Beschädigte Chunks werden
  gemeldet und übersprungen; nur ein unlesbarer `HEAD` macht die Datei unbrauchbar. Unbekannte Chunktypen werden
  ignoriert (vorwärtskompatibel), neuere Formatversionen abgelehnt.
* Codec: Deflate je Chunk, nur wenn kleiner. Festschreibung nach Messbenchmark mit echten Kurven (Abschnitt 9.2) –
  der Codec-Wert je Chunk erlaubt einen späteren Wechsel (z. B. Zstandard) ohne Formatbruch.
* Dateiname: `JJJJMMTT_hhmmss_<Kennung>_<Run-ID>.tres`; existiert er (gleiche Run-ID), wird `_2`, `_3` … angehängt –
  es wird nie überschrieben.

## Outbox und Datenbankvertrag (Abschnitt 9.3)

* Ein Outbox-Eintrag ist eine `.tres`-Datei mit genau der DB-Auswahl plus Zustandsdatei (Versuche, letzter Fehler,
  nächster Versuch). Beides wird atomar geschrieben, **bevor** übertragen wird.
* `IRunDatabase.StoreAsync`: eine Transaktion je Run, Unique-Key Run-ID, idempotent – vorhandener Run gilt als Erfolg.
  Damit erzeugt die Wiederholung nach verlorener Commit-Antwort keine Duplikate (P05).
* Initialer Versuch am Testende: bei Fehler große Werkerwarnung (10 s, je betroffenem Run) und Meldung `StoreFailed`.
* Hintergrund: Backoff `RetryInterval × Faktor^(n−1)` bis `MaxRetryInterval` (Standard 30 s, ×2, max. 10 min),
  Zeitlimit je Versuch 10 s. Fehler → `RetryFailed` mit Versuchsnummer, **keine** Werkerwarnung; Erfolg → `StoredLater`.
* Start der Anwendung (manuelles Öffnen) lädt offene Einträge und setzt fort (P04); kein Autostart. Beenden bricht
  laufende Versuche ab und wartet höchstens 1 s – Herunterfahren wird nicht verzögert.
* Nicht lesbare Einträge wandern nach `Outbox\defekt` (Meldung `OutboxDamaged`).
* Bis Etappe 6 gibt es `InMemoryRunDatabase` (Tests) und `FolderRunDatabase` (simulierte DB als Ordner, Ausfall
  schaltbar). Meldungen gehen an `IRunNotifier` – der MQTT-Adapter (best effort, QoS 0, keine Wiederholung) folgt.

## Kurven

* `Waveform`: Kanal (abstrakter Name), Einheit, Abtastrate oder explizite Zeitpunkte, Samples, Qualität, Sync-Gruppe.
  `WaveformSet` verbindet Kanäle mit gemeinsamer Zeitbasis (L1/L2/L3).
* `IWaveformAdapter.CaptureAsync` – alle Kanäle eines Aufrufs am selben Gerät, sonst ERROR. Zugriff unterliegt wie
  Einzelwerte der Ressourcenvermittlung.
* Baustein „Kurve aufnehmen“ (`kurve.aufnehmen`) mit optionaler Effektivwertbewertung je Kanal.

## Offen

* Codec-Benchmark mit realen Datenmengen; ggf. `f32` für Rohsamples.
* Unvollständige Läufe nach Stromausfall (Zwischenaufzeichnung auf Datenträger) – Behandlung separat freizugeben.
* Lokale Wiederherstellung: Rückkopieren aus dem Wiederherstellungsordner (derzeit manuell).
