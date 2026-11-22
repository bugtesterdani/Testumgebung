using System.Text;
using System.Text.Json;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabCatalogCoverageReportTests
{
    private static IEnumerable<CabCatalogEntry> GraphicEntries => CabCatalog.Default.Entries.Where(e => e.Kind is "barcode" or "grafik"
        || new[] { "JS-4.7", "JS-4.9", "JS-4.16", "JS-4.17" }.Contains(e.Id));

    [Fact]
    public void Coverage_report_is_current_and_deferrals_are_exact()
    {
        // Generate only after actually running the evidence, not from catalog counts alone.
        RunEvidence();
        var deferrals = ReadDeferrals();
        var expected = CabCatalog.Default.Entries.Select(e => "properties/5/" + e.Id)
            .Concat(GraphicEntries.Select(e => "graphics/6/" + e.Id)).Append("integration/7/project").Order().ToArray();
        Assert.Equal(expected, deferrals.Order());
        Assert.Equal(deferrals.Length, deferrals.Distinct().Count());
        var report = BuildReport();
        var path = Path.Combine(CabJScriptCatalogCoverageTests.RepositoryRoot(), "docs/cab/abdeckung.md");
        if (Environment.GetEnvironmentVariable("TESTUMGEBUNG_CAB_WRITE_REPORT") == "1") File.WriteAllText(path, report, new UTF8Encoding(false));
        Assert.True(File.Exists(path), "Bericht fehlt; build/update-cab-coverage.ps1 ausführen.");
        Assert.Equal(report.ReplaceLineEndings("\n"), File.ReadAllText(path).ReplaceLineEndings("\n"));
    }

    private static void RunEvidence()
    {
        var source = new CabSourceEditorTests();
        foreach (var generation in Enum.GetValues<CabPrinterGeneration>())
            source.Every_entry_parameter_and_value_is_reachable_with_manual_help(generation);
        foreach (var row in CabSourceEditorTests.CatalogFiles())
            source.Every_catalog_fixture_opens_in_source_view_and_saves_byte_identically((string)row[0], (string)row[1]);
        var coverage = new CabJScriptCatalogCoverageTests();
        coverage.Repository_catalog_and_definitions_match_in_both_directions();
        coverage.Explicit_attribute_inventory_covers_every_entry_and_value_once();
        foreach (var row in CabJScriptCatalogCoverageTests.Entries())
            coverage.Every_entry_has_a_parsed_validated_lossless_protocol_fixture((string)row[0], (string)row[1], (string)row[2], (string)row[3]);
        foreach (var row in CabJScriptCatalogCoverageTests.Values())
            coverage.Every_table_value_has_protocol_bytes_typed_validation_and_lossless_writing((string)row[0], (string)row[1], (string)row[2], (string)row[3], (CabPrinterGeneration)row[4]);
        new CabParameterCorpusTests().Every_non_content_parameter_has_a_valid_example_in_the_versioned_corpus();
        var content = new CabContentCorpusTests();
        foreach (var row in CabContentCorpusTests.Corpus()) content.Every_catalog_content_field_is_recognized_bound_and_validated((string)row[0], (string)row[1]);
        Assert.Equal(1416, CabCatalog.Default.Entries.Sum(e => e.Parameters.Count));
    }

    private static string[] ReadDeferrals()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CabJScriptCatalogCoverageTests.Fixture("coverage-deferrals.json")));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        return document.RootElement.GetProperty("deferrals").EnumerateArray().Select(e =>
            e.GetProperty("stage").GetString() + "/" + e.GetProperty("ap").GetInt32() + "/" + e.GetProperty("id").GetString()).ToArray();
    }

    private static string BuildReport()
    {
        var cases = CabJScriptCatalogCoverageTests.Inventory();
        var text = new StringBuilder("# cab-JScript-Abdeckung\n\n");
        text.Append("Generiert durch `build/update-cab-coverage.ps1` nach Ausführung der Nachweise.\nDer normale Testlauf prüft denselben Bericht auf Aktualität und führt die Nachweise erneut aus.\n\n");
        text.Append("237 Einträge, 1.416 Parameter und 1.801 Tabellenwerte. Der geprüfte Katalog enthält\nzwei nachgetragene Abschnitte gegenüber der ursprünglich genannten Zahl 235.\n\n");
        text.Append("| Art | Gesamt | Erkannt | Validiert | Quelltext | Eigenschaften-Editor | Grafisch vollständig | Vorschau | Registrierte Fälle |\n| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: |\n");
        foreach (var group in CabCatalog.Default.Entries.GroupBy(e => e.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ids = group.Select(e => e.Id).ToHashSet();
            var graphic = GraphicEntries.Count(e => ids.Contains(e.Id));
            var count = cases.Count(c => ids.Contains(c.Id)) + group.Count(e => e.Kind == "barcode") + group.Count();
            text.Append($"| {group.Key} | {group.Count()} | {group.Count()} | {group.Count()} | {group.Count()} | 0 | {(graphic == 0 ? "—" : "0 / " + graphic)} | {(group.Key == "inhaltsfeld" ? "107 / 107" : "—")} | {count} |\n");
        }
        text.Append("\n## Bedeutung und Grenzen\n\nErkannt und validiert: je Eintrag eine ausführbare Protokolldatei mit unverändertem\nSchreiben und geprüfter Befehls-/Inhaltsfeldbindung. Allgemeine Regeln und drei\nBarcode-Anwendungsprofile werden ausdrücklich über ihren Kontext bzw. Referenzbefehl\nnachgewiesen; sie sind keine erfundenen zusätzlichen Druckerbefehle.\n\n");
        text.Append("Jeder Tabellenwert hat zusätzlich einen expliziten `CatalogCase` mit konkretem\nParameterwert. Dieser wird als isolierter Parameter aus einem Protokollträger\ngelesen, gegen seine eigene Definition geprüft und unverändert geschrieben. Das\nersetzt keine beliebige Kombination von Optionen in einem vollständigen Befehl.\nSchematische Tabellenwörter werden durch konkrete Werte instanziiert; Antwortfelder\nbleiben als solche gekennzeichnet. Alle Parameter besitzen außerdem die gültigen\nKorpuswerte aus AP 2 bzw. die tatsächlichen Inhaltsfeldbindungen aus AP 3.\n\n");
        text.Append("Vorschau: 82 offline auswertbare Definitionen und 25 explizite druckerabhängige\nPlatzhalter; zusätzlich gelten die Grenz- und Erwartungswerttests aus AP 3. Ein\nPlatzhalter behauptet keinen gelesenen Hardwarewert.\n\n");
        text.Append("## Quelltextansicht (AP 4)\n\nAlle 237 Korpusdateien öffnen und speichern bytegleich in der Quelltextansicht.\nAlle 237 Einträge, 1.416 Parameter und 1.801 Werte sind für alle vier Generationen\nin der Katalogvervollständigung erreichbar. Gezielte Text-, Codepage-, Binärschutz-\nund Oberflächentests prüfen Bearbeitung, Navigation und unveränderte Objekte.\nDie 237 Source-Ausnahmen wurden entfernt.\n\n## Ausnahmen der frühen Einführung\n\nDie empfohlene Reihenfolge führt AP 8 vor AP 4–7 ein. Deshalb sind Eigenschaften-Editor (237, AP 5), vollständige Grafiknachweise\n(50, AP 6) und abschließende Projekt-/Release-/ZPL-Integration (1, AP 7) noch\nausdrücklich zurückgestellt. Bestehende Teilansichten zählen nicht als vollständiger\nNachweis einer Spalte. Die Pflichtspalte Eigenschaften-Editor steht tatsächlich auf\n0, nicht auf behaupteten 100 %. Der Build erlaubt ausschließlich die 288 konkret\naufgelisteten Ausnahmen in `tests/Fixtures/cab/coverage-deferrals.json`; keine\nWildcards, Duplikate oder unbekannten IDs. Die späteren APs müssen ihren tatsächlichen\nNachweis ergänzen und ihre Ausnahmen entfernen. Am Ende muss die Liste leer sein.\n\n");
        text.Append("## Korpus und lokales Handbuch\n\n`tests/Fixtures/cab/` enthält eigene Dateien je Eintrag, eine ungültige Datei\nje Barcodeprofil sowie Protokollgrenzen und die beiden bestehenden verlustfreien\nFixtures. Es werden keine zusätzlichen Herstellerexporte erfunden. Hersteller-PDF\nund daraus lokal extrahierte Beispiele werden nicht eingecheckt. Der optionale\nHandbuchtest und sein lokales Ergebnis werden getrennt vom synthetischen Nachweis\ndokumentiert. Ein übersprungener Handbuchtest ist kein erfolgreicher Handbuchlauf.\n");
        return text.ToString();
    }
}
