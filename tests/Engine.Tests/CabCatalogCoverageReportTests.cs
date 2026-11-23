using System.Text;
using System.Text.Json;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabCatalogCoverageReportTests
{
    private static IEnumerable<CabCatalogEntry> GraphicEntries => CabCatalog.Default.Entries.Where(e => e.Kind is "barcode" or "grafik"
        || new[] { "JS-4.7", "JS-4.9", "JS-4.16", "JS-4.17" }.Contains(e.Id));

    [Fact]
    public async Task Coverage_report_is_current_and_deferrals_are_exact()
    {
        // Generate only after actually running the evidence, not from catalog counts alone.
        await CabProjectIntegrationTests.VerifyContractAsync();
        RunEvidence();
        var deferrals = ReadDeferrals();
        string[] expected = [];
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
        RunPropertyEvidence();
        var graphics = new CabGraphicTests();
        graphics.Graphic_catalog_contains_all_50_definitions_and_43_barcode_profiles();
        foreach (var row in CabGraphicTests.Entries())
            graphics.Every_graphic_template_has_finite_transformed_bounds_and_byte_preserving_gestures((string)row[0]);
        foreach (var row in CabGraphicTests.BarcodeEntries())
            graphics.Every_barcode_profile_produces_real_modules_or_an_explicit_empty_fallback((string)row[0]);
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

    private static void RunPropertyEvidence()
    {
        var properties = new CabPropertyBindingsTests();
        foreach (var generation in Enum.GetValues<CabPrinterGeneration>())
            properties.Every_catalog_parameter_has_a_typed_form_and_complete_choices(generation);
        foreach (var row in CabJScriptCatalogCoverageTests.Entries())
            properties.Every_protocol_template_exposes_actual_command_properties_without_changing_source((string)row[0], (string)row[1], (string)row[2], (string)row[3]);
        var forms = new CabContentFormTests();
        foreach (var row in CabContentCorpusTests.Corpus())
        {
            properties.Every_content_parameter_has_an_exact_editable_source_binding((string)row[0], (string)row[1]);
            forms.Every_content_insertion_has_real_parameter_bindings_and_an_offline_preview((string)row[0], (string)row[1]);
        }
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
            text.Append($"| {group.Key} | {group.Count()} | {group.Count()} | {group.Count()} | {group.Count()} | {group.Count()} | {(graphic == 0 ? "—" : graphic + " / " + graphic)} | {(group.Key == "inhaltsfeld" ? "107 / 107" : "—")} | {count} |\n");
        }
        text.Append("\n## Bedeutung und Grenzen\n\nErkannt und validiert: je Eintrag eine ausführbare Protokolldatei mit unverändertem\nSchreiben und geprüfter Befehls-/Inhaltsfeldbindung. Allgemeine Regeln und drei\nBarcode-Anwendungsprofile werden ausdrücklich über ihren Kontext bzw. Referenzbefehl\nnachgewiesen; sie sind keine erfundenen zusätzlichen Druckerbefehle.\n\n");
        text.Append("Jeder Tabellenwert hat zusätzlich einen expliziten `CatalogCase` mit konkretem\nParameterwert. Dieser wird als isolierter Parameter aus einem Protokollträger\ngelesen, gegen seine eigene Definition geprüft und unverändert geschrieben. Das\nersetzt keine beliebige Kombination von Optionen in einem vollständigen Befehl.\nSchematische Tabellenwörter werden durch konkrete Werte instanziiert; Antwortfelder\nbleiben als solche gekennzeichnet. Alle Parameter besitzen außerdem die gültigen\nKorpuswerte aus AP 2 bzw. die tatsächlichen Inhaltsfeldbindungen aus AP 3.\n\n");
        text.Append("Vorschau: 82 offline auswertbare Definitionen und 25 explizite druckerabhängige\nPlatzhalter; zusätzlich gelten die Grenz- und Erwartungswerttests aus AP 3. Ein\nPlatzhalter behauptet keinen gelesenen Hardwarewert.\n\n");
        text.Append("## Quelltextansicht (AP 4)\n\nAlle 237 Korpusdateien öffnen und speichern bytegleich in der Quelltextansicht.\nAlle 237 Einträge, 1.416 Parameter und 1.801 Werte sind für alle vier Generationen\nin der Katalogvervollständigung erreichbar. Gezielte Text-, Codepage-, Binärschutz-\nund Oberflächentests prüfen Bearbeitung, Navigation und unveränderte Objekte.\nDie 237 Source-Ausnahmen wurden entfernt.\n\n");
        text.Append("## Eigenschaften und Aufträge (AP 5)\n\nAlle 237 Definitionen und ihre 1.416 Parameterschemata sind erreichbar, mit Typ,\nEinheit, Generation, Beschreibung und Wahlwerten. Alle Protokollvorlagen prüfen\ndie tatsächlichen Parameterbindungen über vier Generationen und unverändertes\nSchreiben vorhandener gültiger Eingaben. Alle 107 Inhaltsfeldformulare besitzen\nechte Argumentbindungen und Offline-Vorschau. Änderungen, optionale Ergänzungen,\nAuftragsreihenfolge, Dateiaustausch und echte WPF-Bedienung haben zusätzliche Tests.\nAntwortfelder bleiben lesbare Druckerausgaben; allgemeine Kontextregeln erzeugen\nkeine zusätzlichen Befehle. Variantenparameter setzen die passende Schrift,\nBarcodeart oder übergeordnete Option voraus. Ressourcen werden als Dateien ersetzt.\nDie Spalte bedeutet vollständige Erschließung der Definitionen, keine Schreibbarkeit\nvon Druckerantworten oder beliebiger widersprüchlicher Optionskombinationen.\nDie 237 Properties-Ausnahmen wurden entfernt.\n\n");
        text.Append("## Grafik (AP 6)\n\nAlle 50 grafischen Definitionen einschließlich der 43 Barcodeprofile besitzen\nausführbare Nachweise für Einfügen, Verschieben, Drehen, Entfernen und bytegenaue\nErhaltung der Nachbarbereiche. Separate Erwartungswerte prüfen Grundlinie,\nKreiszentrum, Einheiten und Drehung. WPF-Tests rendern alle Vorlagen und prüfen\nBedienung, Bildformate, Schrift-/HTML-Effekte und Füllmuster. Echte unterstützte\nBarcodes stammen aus ZXing.Net 0.16.11; nicht erzeugte Symbologien/Optionen bleiben\nbeschriftete Flächen berechneter Näherungsgröße. Code128 und QR werden rückgelesen.\nSchriften und Klarschriftmetriken sind erklärte Windows-Näherungen. Die Spalte\nbehauptet keine druckergleiche Pixelwiedergabe oder Hardwareauswertung.\nDie 50 Grafik-Ausnahmen sind entfernt.\n\n");
        text.Append("## Dateien und Projektintegration (AP 7)\n\nDer Bericht führt zusätzlich den tatsächlichen Integrationsvertrag aus:\nsyntaxgebundene Druck-Platzhalter in Text/Barcode/Rich Text und R-Ersetzungen,\nbytegleiche Downloads in allen drei Rahmungen einschließlich lesbarer Klammern,\nunveränderte Codepagegrenzen und ältere Druck-/Layoutprojekte. Migration erzeugt\neine private Quellkopie; neue gespeicherte Vorlagen benötigen keinen Text-Offset.\nProjekt- und CLI-Diagnosen erhalten ihre Schwere; Fehler verhindern eine neue\nFreigabe, Warnungen werden angezeigt und lassen sie zu. Separate echte WPF-Tests\nprüfen Dateiablage, bytegleiches Speichern, Abbrechen und Übernehmen. Die bisherigen\nZPL-Tests bleiben Teil des vollständigen Testsatzes.\nDie Ausnahmeliste `tests/Fixtures/cab/coverage-deferrals.json` ist leer; der Test\nerlaubt keine weiteren Rückstellungen.\n\n");
        text.Append("## Korpus und lokales Handbuch\n\n`tests/Fixtures/cab/` enthält eigene Dateien je Eintrag, eine ungültige Datei\nje Barcodeprofil sowie Protokollgrenzen und die beiden bestehenden verlustfreien\nFixtures. Es werden keine zusätzlichen Herstellerexporte erfunden. Hersteller-PDF\nund daraus lokal extrahierte Beispiele werden nicht eingecheckt. Der optionale\nHandbuchtest und sein lokales Ergebnis werden getrennt vom synthetischen Nachweis\ndokumentiert. Ein übersprungener Handbuchtest ist kein erfolgreicher Handbuchlauf.\n");
        return text.ToString();
    }
}
