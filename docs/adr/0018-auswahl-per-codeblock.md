# ADR 0018 – Eigener Auswahlablauf mit einem einzigen CodeBlock

Status: angenommen (Oktober 2026). Bezug: Abschnitt 5 (Auswahl/Auftrag, ADR 0010), eigener Auswahlablauf.

## Problem

Der eigene Auswahlablauf verlangte bisher den grafischen Baustein „Test auswählen“ (`auswahl.test`) als Ausgabe. Wer die
Zuordnung lieber frei in Code schreibt, musste trotzdem einen zweiten Baustein einbauen. Gewünscht: **ein einziger CodeBlock**
soll bestimmen können, welches Testprogramm und welche Artikelnummer am Ende genutzt werden.

## Entscheidung

Neue Code-API **`SelectTest(test, version, order, parameters)`** (Rückruf an den Host, Operation `select.test`), verfügbar in
allen vollwertigen CodeBlock-Sprachen:

* **C#** (Prelude): `SelectTest(string test, string version = null, string order = null, string parameters = null)`
* **Python** (worker): `select_test(test, version=None, order=None, parameters=None)`

Der Aufruf setzt im Host (`StepCodeHost`) eine `TestSelectionValue`. `CodeBlockAction` übernimmt sie als **Schrittergebnis**
(`StepOutcome.Value`), sofern der Schritt nicht abgebrochen wurde und nicht ERROR ist. Damit liest `TestSelector.RunCustomAsync`
genau wie beim Baustein „Test auswählen“ das Ergebnis (`StepCompleted { Value: TestSelectionValue }`) – **ohne Änderung** am
Auswahlpfad. Ein CodeBlock, der `SelectTest(...)` aufruft, ist somit ein vollständiger Auswahlablauf.

Bedeutung der Felder (unverändert zum Baustein): `test` = Name des freigegebenen Ablaufs (Testkennung); `version` = leer/null ⇒
aktuell freigegebene Version; `order` = Auftrag; `parameters` = Prüfparameter „Name=Wert;…“ (setzen gleichnamige Variablen im
eigentlichen Prüflauf). Eine **Artikelnummer** hat kein eigenes Feld – sie wird über `order` (Auftrag) und/oder einen
Prüfparameter (`Artikel=12345`) geführt.

Eingang bleibt wie gehabt: die Engine belegt beim Start `Kennung` (und, falls vorhanden, `Auftrag`) vor; der CodeBlock sieht sie
als Ordner-Variablen.

Beispielprojekt mit genau einem CodeBlock: `docs/beispiele/auswahl-codeblock.tproj.json` (auf der Auswahl-Seite unter „Eigener
Auswahlablauf“ → „Durchsuchen …“ einhängen; danach wird nur noch der CodeBlock-Quelltext bearbeitet).

## Nicht enthalten / Grenzen

* C++/LabVIEW: kein `SelectTest` in der Code-API (Auswahl-CodeBlocks sind in der Praxis C#/Python); dort weiterhin der Baustein
  „Test auswählen“.
* Kein eigenes Feld „Artikelnummer“ – bewusst über Auftrag/Prüfparameter, um das Ergebnismodell klein zu halten.
* `SelectTest` außerhalb eines CodeBlocks (anderer `ICodeHost`) wirft `NotSupportedException`.

## Folgen

* Der eigene Auswahlablauf kann aus einem einzigen CodeBlock bestehen; der Baustein „Test auswählen“ bleibt als grafische
  Alternative bestehen und ist weiterhin der Pflicht-Ausgang, wenn kein `SelectTest`-Aufruf erfolgt.
* Getestet: `SelectionTests.Eigener_Auswahlablauf_nur_mit_einem_CodeBlock` (C# und Python, inkl. Artikelnummer über Auftrag und
  Prüfparameter).
