# Beispiel: visuelle Blockprogrammierung

`bloecke.tproj.json` im Ablaufeditor öffnen und **Blockansicht** einschalten.
Das Beispiel benötigt keine Hardware oder externen Programme und endet mit PASS.

- **Wiederhole dreimal** erhöht die lokale Ganzzahl `Zaehler` von 0 auf 3.
- **Wenn / Sonst** prüft `Spannung >= 10.0 UND Bereit` einmal. Der Dann-Zweig
  setzt anschließend `Bereit` auf false. Trotzdem wird der Sonst-Zweig nicht
  ausgeführt, weil die Entscheidung bereits festgehalten wurde.
- **Parallel ausführen** startet zwei unabhängige bewertende Schritte und wartet
  auf beide.

Die Bedingung direkt im äußeren **Wenn / Sonst**-Block bearbeiten; für den
Baukasten **◈ Wert aus Blöcken** öffnen. Im klassischen Editor liegt derselbe
Ausdruck am Schritt **Bedingung einmal prüfen**.
Weitere Bedienung: [visuelle Blockprogrammierung](../../visuelle-blockprogrammierung.md).

Dasselbe Beispiel lässt sich über **Knotenansicht** mit Verbindungslinien, Zoom
und Debugwerten ansehen. [Anschlüsse und Debuggen](../../knotenansicht.md).
