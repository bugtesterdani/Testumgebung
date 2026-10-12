# Handbuch: Eigene C#-DLLs einbinden

Für Testumgebung ab Version 1.0.1.0. Die DLL kann Fertigungsaufträge auswerten, eigene Datenquellen abfragen und Berechnungen oder Bewertungen im Prüfprogramm durchführen.

## 1. Erforderliche öffentliche Schnittstelle

Es gibt **keinen vorgeschriebenen Methodennamen und kein verpflichtendes Interface**. Die Software ruft nach dem Import keine DLL-Methode automatisch auf. Ein C#-CodeBlock ruft die gewünschte öffentliche Methode ausdrücklich auf.

| Bestandteil | Anforderung |
| --- | --- |
| DLL | Verwaltete .NET-Klassenbibliothek, kompatibel mit dem .NET-10-Worker |
| Klasse | `public` |
| Methode | `public`; statische und Instanzmethoden sind möglich |
| Konstruktor | Bei `new MeineKlasse(...)` ein passender öffentlicher Konstruktor |
| Ergebnisobjekt | Verwendeter Typ und gelesene Eigenschaften öffentlich |
| Parameter / Rückgabe | Normale C#-Typen, z. B. `string`, `double`, `bool` oder eigene Datenobjekte |
| Dateiname | Entspricht dem Assemblynamen, z. B. `MeinePruefbibliothek.dll` |
| Start / Loop / End | Abschnitte des Prüfprojekts; keine vorgeschriebenen DLL-Exports |

Keine C-Exports, COM-Registrierung, `[DllExport]` oder Referenz auf die Testumgebung erforderlich. Ein Namespace ist keine technische Pflicht, hilft aber bei eindeutigen Aufrufen. Vollständige Typnamen vermeiden zusätzliche `using`-Anweisungen im CodeBlock.

`SelectTest`, `Pass`, `Fail`, `Error` und `Measured` gehören zum CodeBlock. Die DLL liefert gewöhnliche C#-Werte; der aufrufende CodeBlock wählt das Programm oder bewertet den Prüfschritt.

## 2. Vollständige Beispielbibliothek

Die folgenden zwei Dateien in einem eigenen Ordner anlegen. Das Beispiel benötigt keine NuGet-Pakete.

**Datei `MeinePruefbibliothek.csproj`:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
    <AssemblyName>MeinePruefbibliothek</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

**Datei `Prueffunktionen.cs`:**

```csharp
namespace MeinePruefbibliothek;

public sealed record Auftragsdaten(string Testprogramm, string Artikel);

public static class Auftragsauswertung
{
    public static Auftragsdaten Lesen(string fertigungsauftrag)
    {
        ArgumentNullException.ThrowIfNull(fertigungsauftrag);
        string auftrag = fertigungsauftrag.Trim().ToUpperInvariant();

        // Beispielregeln. Hier später ERP, Datenbank oder Webservice abfragen.
        return auftrag switch
        {
            "A123" => new Auftragsdaten("Programm A", "Artikel-A"),
            "B123" => new Auftragsdaten("Programm B", "Artikel-B"),
            "C123" => new Auftragsdaten("Programm C", "Artikel-C"),
            "D123" => new Auftragsdaten("Programm D", "Artikel-D"),
            _ => throw new ArgumentException(
                "Unbekannter Fertigungsauftrag: " + auftrag)
        };
    }
}

public sealed class Spannungspruefung
{
    public Spannungspruefung(double untergrenze, double obergrenze)
    {
        if (untergrenze > obergrenze)
            throw new ArgumentException("Ungültige Grenzen.");
        Untergrenze = untergrenze;
        Obergrenze = obergrenze;
    }

    public double Untergrenze { get; }
    public double Obergrenze { get; }

    public bool IstZulaessig(double spannung)
        => double.IsFinite(spannung)
           && spannung >= Untergrenze
           && spannung <= Obergrenze;

    public static double Korrigieren(double rohwert, double faktor)
        => rohwert * faktor;
}
```

Im Bibliotheksordner bauen:

```powershell
dotnet build MeinePruefbibliothek.csproj -c Release
```

Importiert wird `bin/Release/net10.0/MeinePruefbibliothek.dll`, nicht eine Referenzassembly aus einem `ref`-Ordner. AnyCPU eignet sich für rein verwalteten Code. Die ausgelieferte Anwendung läuft als x64; erforderliche native Zusatzkomponenten müssen dazu passen.

## 3. Import in die Testumgebung

1. Das gewünschte Projekt im Editor öffnen.
2. **Baustein-Bibliothek → .NET-DLL importieren / ersetzen** wählen.
3. `MeinePruefbibliothek.dll` auswählen.
4. Weitere benötigte verwaltete DLLs einzeln importieren, auch indirekte Abhängigkeiten.
5. Projekt speichern und einen C#-CodeBlock mit dem gewünschten Aufruf anlegen.
6. **Build prüfen** ausführen und den Ablauf testen.
7. Für den Produktivbetrieb das geänderte Testprojekt neu prüfen und freigeben.

Die DLL-Bytes werden im Projekt gespeichert und mit der Testprogrammfreigabe ausgeliefert. Die Originaldatei auf dem Entwicklungsrechner wird danach nicht benötigt. Zusätzliche externe Komponenten der eigenen DLL sind davon unabhängig.

Auswahlprojekt und Testprojekt besitzen jeweils eigene DLL-Importe. Eine in beiden verwendete Bibliothek in **beide Projekte importieren**.

## 4. Fertigungsauftrag auswerten

### Besonderheit bei einem Bibliotheksbaustein

Ein C#-Bibliotheksbaustein sieht ausschließlich die Eingänge seiner eigenen Schnittstelle, nicht automatisch die Variablen des Prüfprojekts. Rechts unter **Schnittstelle (Baustein-Vertrag)** einen Port mit exakt diesen Angaben anlegen:

| Bezeichnung | Typ | Richtung | Testwert |
| --- | --- | --- | --- |
| `Auftrag` | `string` | Eingang | `A123` |

Groß-/Kleinschreibung beachten. Den Testwert als `A123` ohne Anführungszeichen eingeben; Anführungszeichen werden hier Teil des Strings. Beim Einfügen des Bibliotheksbausteins in den Auswahlablauf seinen Eingang `Auftrag` an die Projektvariable `Auftrag` binden. Die DLL erhält den Wert durch den Methodenparameter.

Die Meldung „Auftrag ist im aktuellen Kontext nicht vorhanden“ ist ein Compilerfehler des CodeBlocks: Sein Port fehlt oder ist noch nicht gültig übernommen. Sie bedeutet nicht, dass die DLL einen unbekannten Fertigungsauftrag meldet. „Unbekannter Fertigungsauftrag“ aus der Beispiel-DLL ist dagegen ein Laufzeitfehler für nicht unterstützte Eingaben.

Bei Version 1.0.1.0 nach einer Änderung des Portnamens das Feld verlassen und erneut **Build prüfen**. Ab Version 1.0.2.0 werden Porttexte unmittelbar übernommen; Build/Test blockieren bei ungültigem Vertrag ausdrücklich.

Für die Typauswahl mindestens Version 1.0.3.0 verwenden. In älteren Versionen konnte die Auswahlliste `string` anzeigen, während intern weiterhin ein Zahlentyp gespeichert war. Symptom: „Testwert: Wert muss Zahl sein“ trotz angezeigtem `string`. Nach dem Update den Porttyp erneut auf `string` setzen und `A123` als Testwert verwenden.

Im Auswahlprojekt eine Hauptvariable `Auftrag` vom Typ String bereitstellen. Sie enthält dort den gescannten Fertigungsauftrag. C#-CodeBlock im Auswahlablauf:

```csharp
var daten = MeinePruefbibliothek.Auftragsauswertung.Lesen(Auftrag);
SelectTest(daten.Testprogramm, null, Auftrag, null);
return Pass(daten.Testprogramm);
```

A123 wählt „Programm A“, B123 „Programm B“ usw. Die zurückgegebene Programmkennung muss zu einem verfügbaren Programm passen. Die DLL erzeugt oder installiert keine Testprogramme. Produktiv muss die passende Freigabe am Prüfplatz verfügbar sein.

Die CodeBlock-Funktion hat diese Signatur:

```csharp
SelectTest(string test, string version = null,
           string order = null, string parameters = null,
           string pythonVenv = null)
```

- `test`: zu ladendes Testprogramm.
- `version`: optional eine bestimmte Version; `null` verwendet die normale Versionsauflösung.
- `order`: Fertigungsauftrag für den Prüfablauf.
- `parameters`: optionale Vorbelegungen als `Name=Wert;Name2=Wert2` für passende Variablen im Zielprojekt.
- `pythonVenv`: optionaler absoluter Umgebungspfad für das ausgewählte Testprogramm. Die [Python-Anleitung](portable-python.md#python-je-ausgewähltem-testprogramm-festlegen) beschreibt die Rückfallreihenfolge und Beispiele.

Beispiel: Im Zielprojekt existiert eine String-Variable `Artikel`:

```csharp
var daten = MeinePruefbibliothek.Auftragsauswertung.Lesen(Auftrag);
SelectTest(daten.Testprogramm, null, Auftrag, "Artikel=" + daten.Artikel);
return Pass(daten.Testprogramm);
```

Dieses einfache Format setzt trennzeichenfreie Werte voraus. Keine beliebigen Objekte oder JSON-Strukturen als `parameters` übergeben; bei Werten mit `;` oder `=` eine geeignete eigene Übergabestrategie verwenden.

## 5. DLL im normalen Prüfprogramm

Die Bibliothek steht allen C#-CodeBlocks und C#-Bibliotheksbausteinen des importierenden Projekts zur Verfügung, auch in Start/Init, Loop und End.

**Instanzmethode:** Eine numerische Prüfvariable `Spannung` anlegen und vor diesem Schritt mit dem Messwert belegen.

```csharp
var pruefung = new MeinePruefbibliothek.Spannungspruefung(220.0, 240.0);
bool ok = pruefung.IstZulaessig(Spannung);
return ok
    ? Pass(Spannung)
    : Fail("Spannung außerhalb 220–240 V", Spannung);
```

**Statische Berechnung:** Numerische Variablen `Rohspannung` und `Spannung` im Projekt definieren.

```csharp
Spannung = MeinePruefbibliothek.Spannungspruefung.Korrigieren(
    Rohspannung, 2.0);
return Measured(Spannung, 220.0, 240.0, "V");
```

Die DLL liefert hier `bool`, `double` oder `Auftragsdaten`. Der CodeBlock schreibt Prüfvariablen und übersetzt Ergebnisse in `Pass`, `Fail`, `Error`, `NResult` oder `Measured`. Andere öffentliche C#-Typen sowie `void`-Methoden sind ebenfalls möglich; es gibt keinen verpflichtenden DLL-Ergebnistyp.

## 6. Fehler, Abbruch und asynchrone Methoden

Eine unbehandelte DLL-Ausnahme führt zu ERROR. Bei der Auftragsauswertung verhindert sie eine erfolgreiche Auswahl. Erwartete Eingabefehler lassen sich gezielt behandeln:

```csharp
try
{
    var daten = MeinePruefbibliothek.Auftragsauswertung.Lesen(Auftrag);
    SelectTest(daten.Testprogramm, null, Auftrag, null);
    return Pass(daten.Testprogramm);
}
catch (ArgumentException ex)
{
    return Error(ex.Message);
}
```

Lang laufende DLL-Methoden sollten einen `System.Threading.CancellationToken` entgegennehmen und regelmäßig `ThrowIfCancellationRequested()` aufrufen. Der CodeBlock stellt `CancellationToken` bereit; diesen an die DLL weiterreichen. Abbruchausnahmen nicht pauschal abfangen und in PASS umwandeln.

Der C#-CodeBlock führt synchron `Run()` aus. Direktes `await` auf oberster Ebene ist daher nicht vorgesehen. Eine eigene Methode `Task<Auftragsdaten> LesenAsync(string auftrag, CancellationToken token)` kann so aufgerufen werden:

```csharp
// Schema: LesenAsync muss zusätzlich in der eigenen DLL implementiert sein.
var daten = MeinePruefbibliothek.Auftragsauswertung
    .LesenAsync(Auftrag, CancellationToken).GetAwaiter().GetResult();
SelectTest(daten.Testprogramm, null, Auftrag, null);
return Pass(daten.Testprogramm);
```

`LesenAsync` ist kein Bestandteil der Beispielbibliothek in Abschnitt 2. Für Netzwerk-/Datenbankaufrufe eigene Zeitlimits und Abbruchunterstützung vorsehen. Ein synchron blockierender Fremdaufruf wird durch einen CancellationToken allein nicht unterbrochen.

## 7. Abhängigkeiten und Lebensdauer

- Empfohlener Ausgangspunkt: reine `net10.0`-Klassenbibliothek. Andere moderne .NET- oder .NET-Standard-DLLs funktionieren nur bei kompatiblen APIs und Abhängigkeiten. Klassische .NET-Framework-DLLs sind nicht automatisch kompatibel.
- NuGet-Abhängigkeiten werden nicht automatisch heruntergeladen. Zusätzlich benötigte verwaltete DLLs einschließlich indirekter Abhängigkeiten importieren. Framework-DLLs der mitgelieferten Laufzeit nicht als eigene Bibliotheken ersetzen.
- Native DLLs, externe Programme, PDBs, Konfigurations- und Zusatzdateien werden durch den DLL-Import nicht eingebettet. Diese separat bereitstellen oder Ressourcen bewusst in die eigene Assembly einbetten.
- Assemblies werden aus Bytes geladen. Nicht auf den ursprünglichen DLL-Pfad oder eine daneben liegende Konfigurationsdatei vertrauen. Pfade und Einstellungen ausdrücklich als Parameter übergeben.
- Pro CodeBlock-Aufruf entsteht eine neue CodeBlock-Instanz. Eine lokale DLL-Instanz aus `new ...` bleibt nicht automatisch zwischen Start, Loop und End erhalten. DLL-`static`-Zustand kann im Worker fortbestehen; nicht als definierte Auftragsspeicherung verwenden. Prüfvariablen und ausdrücklich gespeicherte Daten bevorzugen.
- Parallel ausgeführte Schritte können DLL-Code gleichzeitig aufrufen. Gemeinsam genutzten Zustand synchronisieren oder vermeiden.
- Hardware- und Werkerzugriffe über die CodeBlock-Funktionen vermitteln. Direkte Hardwarezugriffe der DLL umgehen die Ressourcenverwaltung.
- Ausführung im separaten C#-Workerprozess bedeutet Prozessisolation, keine Sicherheits-Sandbox für beliebigen fremden Code.

## 8. Aktualisieren und Fehler beheben

Eine neue DLL gleichen Dateinamens über **.NET-DLL importieren / ersetzen** importieren. Geänderte Abhängigkeiten ebenfalls ersetzen. Speichern, **Build prüfen**, testen, gegebenenfalls neu freigeben und die Prüfsitzung neu starten. Eine laufende Sitzung übernimmt zwischen zwei Prüflingen keine geänderte DLL.

| Fehler / Verhalten | Prüfen |
| --- | --- |
| Typ / Namespace nicht gefunden | DLL im richtigen Projekt importiert? Vollständiger Namespace korrekt? |
| Typ / Methode nicht zugänglich | Klasse, Methode und Ergebnistypen `public`? |
| Kein passender Konstruktor / keine Überladung | Argumenttypen und Anzahl passend zur öffentlichen Signatur? |
| Dateiname muss Assemblynamen entsprechen | Originalnamen verwenden oder `<AssemblyName>` beim Bauen passend setzen |
| Keine gültige .NET-Assembly | Verwaltete Implementierungs-DLL gewählt, keine native DLL oder Referenzassembly? |
| Abhängigkeit fehlt | Alle benötigten verwalteten DLLs einschließlich indirekter Abhängigkeiten importiert? |
| Programm nicht verfügbar | Kennung korrekt und passende Freigabe am Prüfplatz verfügbar? |
| Im eigenen Tool funktioniert es, hier fehlen Dateien | Explizite Pfade, Arbeitsverzeichnis und Zusatzdateien prüfen |
| Alter DLL-Stand wird verwendet | Erneut importiert, richtige Freigabe geladen und Sitzung neu gestartet? |

CodeBlock-Zeilen bleiben debugbar. Internes Stepping in der importierten DLL wird durch den Import nicht eingerichtet.

Weitere Hinweise: [DLL-Kurzanleitung](dotnet-dlls.md), [Kommunikationsfunktionen](dynamische-verbindungen.md).
