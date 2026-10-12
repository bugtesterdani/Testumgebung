# Eigene .NET-DLLs in Auftragsauswahl und Tests

Das ausführliche [Handbuch zur C#-DLL](handbuch-csharp-dll.md) beschreibt die öffentlichen Klassen und Methoden, ein vollständiges Bibliotheksprojekt und Aufrufe im Auswahl- und Prüfprogramm.

## Einrichten

1. Das gewünschte Projekt im Editor öffnen: für die Auftragsauswertung das Auswahlprojekt, für Prüfungen das jeweilige Testprojekt.
2. **Baustein-Bibliothek → .NET-DLL importieren / ersetzen** wählen und die eigene DLL auswählen.
3. Weitere benötigte .NET-DLLs auf demselben Weg importieren, auch indirekte Abhängigkeiten. Die Liste zeigt die importierten Dateien. Eine erneut importierte Datei gleichen Namens ersetzt den bisherigen Stand.
4. Projekt speichern. Die DLL-Inhalte sind eingebettet; die Originaldateien werden beim späteren Start nicht mehr benötigt.
5. Im C#-CodeBlock öffentliche Typen und Methoden über ihren vollständigen Namespace aufrufen. **Build prüfen** und den Ablauf testen. Für Produktivbetrieb anschließend neu prüfen und freigeben.

Die eingebetteten DLLs stehen allen C#-CodeBlocks und C#-Bibliotheksbausteinen dieses Projekts zur Verfügung, auch Start/Init, Loop und End. Ein Auswahlprojekt und sein gewähltes Testprojekt haben jeweils ihre eigenen DLLs; benötigte Dateien in beiden importieren.

## Beispiel: Fertigungsauftrag auswerten

Eine DLL Auftragsregeln.dll könnte folgende öffentliche Schnittstelle enthalten:

```csharp
namespace Auftragsregeln;
public sealed record Auftragsdaten(string Testprogramm, string Artikel);
public static class Auswertung
{
    public static Auftragsdaten Lesen(string fertigungsauftrag)
    {
        // Hier die echte Datenquelle lesen und fachlich entscheiden.
        if (fertigungsauftrag == "A123")
            return new Auftragsdaten("Programm A", "11111");
        throw new ArgumentException("Fertigungsauftrag unbekannt: " + fertigungsauftrag);
    }
}
```

Nach dem Import im C#-Block des Auswahlprojekts:

```csharp
var daten = Auftragsregeln.Auswertung.Lesen(Auftrag);
SelectTest(daten.Testprogramm, null, Auftrag, "Artikel=" + daten.Artikel);
return Pass(daten.Testprogramm);
```

Die Hauptvariable Auftrag enthält den Scan. Fehler aus der DLL ergeben ERROR und verhindern die Auswahl. SelectTest gehört zum CodeBlock-Vertrag, nicht zur DLL; die DLL kann unabhängig von Testumgebung entwickelt werden und liefert gewöhnliche C#-Objekte oder Werte.

## Beispiel: im normalen Test verwenden

```csharp
var pruefung = new MeinePruefbibliothek.Spannungspruefung();
bool ok = pruefung.IstZulaessig(Spannung);
return ok ? Pass(Spannung) : Fail("Spannung außerhalb des zulässigen Bereichs", Spannung);
```

Es können statische Methoden und Instanzmethoden verwendet werden. Der CodeBlock übersetzt DLL-Rückgabewerte in Pass, Fail, Error oder Measured und schreibt bei Bedarf Prüfvariablen. Hardware- und Werkerzugriffe der Testumgebung weiterhin über die CodeBlock-Funktionen vermitteln.

## Laufzeit und Freigabe

Unterstützt werden verwaltete .NET-Bibliotheken, die mit dem mitgelieferten .NET-10-Worker kompatibel sind, etwa entsprechende moderne .NET- oder .NET-Standard-Bibliotheken. Klassische .NET-Framework-Bibliotheken mit Windows-spezifischen Framework-Abhängigkeiten sind nicht automatisch kompatibel. Native C/C++-DLLs werden über diesen Import abgelehnt; dafür bestehen die separaten nativen Schnittstellen.

Dateiname und Assemblyname müssen übereinstimmen. Fehlende Abhängigkeiten erscheinen als Build- oder Ausführungsfehler. NuGet-Pakete werden nicht automatisch heruntergeladen. Native Zusatzdateien, PDBs, Konfigurationsdateien und Zugangsdaten werden nicht als .NET-DLLs eingebettet. Solche projektspezifischen Anforderungen separat berücksichtigen.

DLLs werden im isolierten C#-Worker aus den eingebetteten Bytes geladen. Der Build-Cache berücksichtigt den Inhalt sämtlicher importierter DLLs; verschiedene Stände werden in getrennten AssemblyLoadContexts geladen. DLL-Code wird nicht in der WPF-Oberfläche ausgeführt. Das ist Prozessisolation, keine Sicherheits-Sandbox für nicht vertrauenswürdigen Code.

Freigabepakete enthalten die Projektbytes und einen SHA-256-Nachweis je DLL. DLL-Änderungen erfordern eine neue Prüfung/Freigabe und einen Neustart der Prüfsitzung; ein Wechsel zwischen zwei Prüflingen übernimmt keine veränderte DLL, damit Init und End denselben Stand verwenden. CodeBlock-Zeilen lassen sich weiterhin debuggen; das interne Stepping in fremden DLLs ist damit nicht hinzugefügt.
