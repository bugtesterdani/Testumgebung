# Microsoft-Vertriebsbedingungen: Prüfung für Testumgebung

Stand: 13.10.2026. Geprüft wurden der inventarisierte MSIX-Stand 1.0.13.3,
die im Projekt hinterlegten Originaltexte, die aktuellen Microsoft-Primärquellen
und die derzeitige Zustimmung in der Software. Dies ist eine dokumentierte
Lizenzauswertung, keine verbindliche Rechtsberatung oder öffentliche Freigabe.

Nachtrag zur Vorbereitung: Original-.NET-/SDK-Verträge, Dateizuordnung,
Release-Prüfregeln und der Zustimmungsumfang sind inzwischen im Projekt ergänzt.
Die folgende Bestandsaufnahme beschreibt die ursprüngliche Lücke; der aktuelle
Umsetzungsstand steht in [GitHub-Veröffentlichung](../github-veroeffentlichung.md).
Vertragliche Pflichten und die Rechtsprüfung werden dadurch nicht aufgehoben.

## Ergebnis für dein Vorhaben

Das öffentliche MIT-Repository und eine kostenlose oder kommerzielle
Windows-Anwendung sind grundsätzlich möglich. Die fremden Binäranteile
behalten ihre Bedingungen. **Die bisherige Zustimmung deckt nur Python ab;
für den vollständigen selbst enthaltenen Windows-Release bestehen noch Lücken.**
Die bisherige technische Prüfung war kein vollständiger Nachweis aller
Microsoft-Vertriebspflichten. Ein bestandener Hash-/Inventarcheck bestätigt
Vollständigkeit und Dateistand, keine rechtliche Wirksamkeit.

## Welche Dateien welche Bedingungen haben

Microsoft ordnet `coreclr.dll`, `PresentationNative_cor3.dll`,
`wpfgfx_cor3.dll` und `vcruntime140_cor3.dll` der **.NET Library License** zu.
`D3DCompiler_47_cor3.dll` unterliegt der **Windows SDK License**.
Diese Dateien sind in unserem Inventar tatsächlich vorhanden, coreclr auch
in beiden .NET-Workern. Der umbenannte WPF-VC-Runtime ist ausdrücklich
von Microsoft unter der .NET-Lizenz neu lizenziert; hierfür ist nicht einfach
die normale Visual-C++-Nutzerlizenz anzusetzen. Andere .NET-Dateien ordnet
Microsoft MIT zu. Das bisherige CSV-Feld MIT beschreibt die Paketdeklaration,
ist für die genannten Einzeldateien aber keine ausreichende Lizenzzuordnung.
[Microsoft: Dateizuordnung für Windows](https://github.com/dotnet/core/blob/main/license-information-windows.md).

## Was die .NET-Vertriebserlaubnis verlangt

Die .NET Library License erlaubt Objektcode als Bestandteil eigener Anwendungen
und Weitergabe durch Vertriebspartner. Sie verlangt schützende Bedingungen für
externe Nutzer und Verteiler; ein separates Angebot dieser Microsoft-Komponenten
ist nicht erlaubt. Microsoft verlangt außerdem Freistellung und Verteidigung
gegen anwendungsbezogene Ansprüche; Ansprüche allein wegen unverändertem
Distributable Code sind ausgenommen. Das ist eine mögliche Verpflichtung des
Verteilers, die ein Zustimmungsbutton nicht beseitigt.

Microsoft-Marken dürfen keine Unterstützung suggerieren. Microsoft-Code darf
nicht so unter eine ausgeschlossene Lizenz gestellt werden, dass Offenlegung
oder Änderungsrechte erzwungen werden. Das ist kein pauschales Verbot, getrennte
GNU-Werkzeuge mitzuliefern. Originalhinweise, gesetzliche Rechte, Exportregeln
und gegebenenfalls Datenschutzhinweise bleiben zu beachten.
[Originalvertrag, insbesondere 3–6](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm).

## Windows SDK / Grafikbibliothek

Der SDK-Vertrag erlaubt benannten Redistributable-Objektcode mit eigener
wesentlicher Programmfunktion. Er verlangt Nutzer-/Verteilerschutzbedingungen,
einen gültigen eigenen Copyright-Hinweis und eine Freistellung gegenüber
Microsoft. Windows-Plattformbindung, unveränderte Rechtehinweise und Verbot
irreführender oder rechtswidriger Anwendungen sind relevant. Nicht jede
SDK-Datei ist deshalb frei weitergebbar. Die konkret zulässige Herkunft und
Redistributable-Zuordnung der Grafikdatei müssen bei der Dokumentation des
Runtimepacks erhalten bleiben. SDK-Buildprogramme wie MakeAppx und SignTool
werden im geprüften Produkt nicht mitgeliefert; ihre Entwicklungslizenz ist
kein allgemeines Recht, das ganze SDK zu veröffentlichen.
[Windows-SDK-Originalvertrag](https://learn.microsoft.com/en-us/legal/windows-sdk/license).

## Python und normale Visual-C++-Laufzeit

Das mitgelieferte Python-LICENSE, Abschnitt „Additional Conditions for this
Windows binary build“, erklärt die Weitergabe des Windows-Python-Builds unter
seinen Bedingungen für zulässig. Es verlangt Vereinbarungen mit externen
Nutzern/Verteilern, erhaltene Microsoft-Hinweise, zulässige Microsoft-Plattformen
und keine Markenirreführung oder schädliche Programme. Das betrifft die
Microsoft-Anteile, nicht deine Python-Skripte. Die vorhandene Zustimmung ist
ein technischer Ansatz für diese Pflicht; ihre vollständige Vertragswirkung
ist nicht durch den gespeicherten Hash bewiesen.

`vcruntime140.dll` und `vcruntime140_1.dll` stammen hier aus der offiziellen
CPython-Distribution. Für separat aus Visual Studio heruntergeladene VC-Dateien
ist dagegen eine eigene Vertriebsberechtigung erforderlich. Microsoft beschränkt
diesen Vertriebsweg auf entsprechend lizenzierte Visual-Studio-Nutzer und
unveränderte zulässige Redist-Dateien; Debug-Nonredist-Dateien sind ausgenommen.
Die bloße Endnutzerlizenz der VC-Runtime erlaubt Nutzung, nicht Weiterverteilung.
Eine kostenpflichtige VS-Lizenz ist deshalb nicht automatisch erforderlich;
eine zutreffende kostenlose Lizenz kann genügen. Die genaue Version und
Bezugsquelle entscheiden, nicht der Dateiname allein.
[Microsoft: VC-Redistribution](https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution),
[VC-Nutzerlizenz](https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/).

## Praktische Entscheidungstabelle

| Vorhaben | Bewertung für dieses Projekt |
| --- | --- |
| Eigenen Quellcode öffentlich unter MIT bereitstellen | Möglich; fremde Lizenzen und Hinweise getrennt erhalten |
| Anwendung kostenlos oder gegen Entgelt auf Windows verteilen | Grundsätzlich möglich, nach Erfüllung der beschriebenen Vertriebsbedingungen |
| Installieren und im Unternehmen Prüfungen ausführen | Vorgesehene Nutzung; eigene Testprogramm-/Treiberlizenzen bleiben relevant |
| pip nutzen und eigene Skripte ausführen | Möglich; neue Pakete haben ihre eigenen Lizenzen |
| Python-Kopie lokal für die Testumgebung nutzen | Plausibel als zugehörige Laufzeit; keine pauschale Berechtigung für Weiterverkauf als separates Runtimeprodukt |
| MSIX komplett als „alles MIT“ bezeichnen | Nicht zulässig als Lizenzbeschreibung |
| Proprietäre Microsoft-DLLs allein als eigenes Downloadprodukt anbieten | Nicht durch die hier geprüfte Anwendungs-Vertriebserlaubnis gedeckt |
| Microsoft-Dateien entfernen, umbenennen oder patchen, um Beschränkungen zu umgehen | Keine pauschale Erlaubnis; Komponentenvertrag und zwingendes Recht beachten |
| Windows-Binäranteile für Linux/Wine weitergeben | Keine Freigabe aus dieser Prüfung; Plattformbedingungen gesondert prüfen |
| Microsoft-Code unter MIT/GPL umdeklarieren | Eigene Lizenzwahl erteilt dafür kein Recht |

## Konkrete noch offene Umsetzung

1. Original-.NET-Library- und SDK-Verträge sowie genaue Dateizuordnung in die
   mitgelieferte Sammlung aufnehmen; Inventargenerator und Prüfregeln anpassen.
2. Die bisher ausdrücklich auf Python begrenzte Nutzerzustimmung auf die
   tatsächlich betroffenen Komponenten erweitern. Verteiler, die die Software
   ohne Start weitergeben, brauchen ebenfalls passende Bedingungen; ein
   Erststartdialog allein erfasst diesen Fall nicht zuverlässig.
3. Freistellungspflichten und Vertragsdurchsetzung für deinen konkreten Vertrieb
   rechtlich bewerten. Diese Verpflichtung kann nicht durch eine technische
   Prüfung als erfüllt oder risikofrei bestätigt werden.
4. .NET-/Windows-Fehlerberichte und die optionale Entra-Anmeldung hinsichtlich
   tatsächlicher Datenübertragung prüfen, bevor Datenschutzbehauptungen erfolgen.
   Eine Lizenzklausel beweist nicht, dass jede App immer Telemetrie sendet.

Diese Prüfung baut kein neues MSIX und ändert keine Nutzerverträge automatisch.
Die Belege wurden lokal unter `artifacts/licensing/microsoft-review/` gesichert.
