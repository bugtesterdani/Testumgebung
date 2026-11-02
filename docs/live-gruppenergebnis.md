# Live-Gruppenergebnis

In jedem Gruppen-Scope steht automatisch die **schreibgeschützte** String-Systemvariable
`GruppenErgebnis` zur Verfügung. Sie braucht keine Deklaration und keine
manuelle Zuweisung. Zulässige Werte: `NRESULT`, `PASS`, `FAIL`, `ERROR`.

- `GruppenErgebnis`: bisher aggregiertes Ergebnis der aktuellen Gruppe.
- `../GruppenErgebnis`: Ergebnis der unmittelbaren Elterngruppe.
- `../../GruppenErgebnis`: Ergebnis der Großelterngruppe.

Der Zugriff funktioniert in Gruppenbedingungen, Wiederholungsausdrücken und
CodeBlocks (C#, Python und C++). Die Namenssuche respektiert Bausteingrenzen.
In einer verschachtelten Gruppe ist das eigene Ergebnis zu Beginn `NRESULT`,
auch wenn der Elternscope bereits `FAIL` meldet.

C#-CodeBlock:

```csharp
if (GruppenErgebnis == "FAIL" || Var<string>("../GruppenErgebnis") == "FAIL")
    Log("Ein bisheriger Prüfschritt ist fehlgeschlagen.");
return NResult();
```

Python-CodeBlock:

```python
if GruppenErgebnis == "FAIL" or var("../GruppenErgebnis") == "FAIL":
    log("Ein bisheriger Prüfschritt ist fehlgeschlagen.")
return NResult()
```

Die Bewertung umfasst nur bereits **übergebene** Ergebnisse; ein gerade
laufender oder am Haltepunkt vor der Rückgabe angehaltener Schritt ist noch
nicht enthalten. Parallel ausgeführte Geschwister können sich überholen:
bei einem Lesezugriff ist der zu diesem Zeitpunkt bereits feststehende
Teilstand sichtbar. Bei Gruppenwiederholung folgt der Wert der Einstellung
`Ergebnis vor Wiederholung` (`Keep` bzw. `ResetToNResult`). Das Gruppenergebnis
wird beim Verlassen der Gruppe final und als `GroupCompleted` gemeldet.

In CodeBlocks werden benannte Ordnervariablen beim Aufruf als Anfangswerte an
den isolierten Worker übergeben. Zum erneuten Lesen **innerhalb desselben
langen CodeBlock-Aufrufs** `Var<string>("GruppenErgebnis")` (C#) bzw.
`var("GruppenErgebnis")` (Python) verwenden. Ein geänderter Feld-/Namens-Schnappschuss wird nicht in die Systemvariable
zurückgeschrieben: Der Schritt endet dann mit `ERROR`. Explizites Schreiben
über `SetVar` oder `set_var` wird sofort vom Host abgewiesen. Das lokale Zuweisen
desselben Anfangswerts verändert die Systemvariable nicht.

C++ verwendet für erneutes Lesen `VarText("GruppenErgebnis")` beziehungsweise
`VarText("../GruppenErgebnis")`:

```cpp
if (VarText("GruppenErgebnis") == "FAIL")
    Log("Bisheriges Gruppenergebnis: FAIL");
return NResult();
```

`GruppenErgebnis` ist als Variablenname reserviert. Bereits vorhandene eigene
Variablen oder grafische Bausteinports dieses Namens müssen vor dem Start
umbenannt werden; sie werden nicht
stillschweigend ersetzt. Der Debugger zeigt den Wert mit Status
**Schreibgeschützt**. Die Projektdatei erhält dafür keine zusätzliche Deklaration.

Ein freier CodeBlock sieht den Gruppen-Scope. Beim Umwandeln in einen isolierten
Code-Bibliotheksbaustein wird ein lesender Zugriff als expliziter String-Eingang
übergeben und ist dort ein Aufruf-Schnappschuss. Der Bibliotheksbaustein hat
keinen impliziten Live-Zugriff auf den aufrufenden Scope. Schreibende Zugriffe
auf die Systemvariable können nicht als Ausgang konvertiert werden. In einer
grafischen Bibliotheksgruppe entsteht dagegen ein eigenes Live-Gruppenergebnis.

Der Live-Wert enthält nur an die Gruppe übergebene Bewertungen. Er sagt nichts
über noch laufende Geschwister, deren zukünftige Ergebnisse oder den
Werkerabbruchstatus aus. Bei Exit-Gruppen ist das Ergebnis der vorherigen
regulären Schritte über `../GruppenErgebnis` erreichbar; die Exit-Gruppe beginnt
selbst mit `NRESULT`. `While`-/`Until`-Bedingungen werden zu den bestehenden
Prüfzeitpunkten der Wiederholungslogik ausgewertet.
