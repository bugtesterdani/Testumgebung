# Beispiel: visuelle Blockprogrammierung

`bloecke.tproj.json` im Ablaufeditor öffnen und **Blockansicht** einschalten.
Das Beispiel benötigt keine Hardware oder externen Programme und endet mit PASS.

- **Wiederhole dreimal** erhöht die lokale Ganzzahl `Zaehler` von 0 auf 3.
- **Wenn / Sonst** prüft `Spannung >= 10.0 UND Bereit` einmal. Der Dann-Zweig
  setzt anschließend `Bereit` auf false. Trotzdem wird der Sonst-Zweig nicht
  ausgeführt, weil die Entscheidung bereits festgehalten wurde.
- **Parallel ausführen** startet zwei unabhängige bewertende Schritte und wartet
  auf beide.

Für die Bedingung **Bedingung einmal prüfen → Ausdruck → Blöcke …** öffnen.
Weitere Bedienung: [visuelle Blockprogrammierung](../../visuelle-blockprogrammierung.md).
