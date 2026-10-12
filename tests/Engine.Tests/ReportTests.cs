using System.Globalization;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Reports;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.ViewModels;
using Xunit;

namespace Testumgebung.Engine.Tests;

/// <summary>Auswertung &amp; Berichte: Kennzahlen (Ausbeute, FPY, Endausbeute), Pareto, Cpk, CSV, PDF-Prüfbericht und -Auswertung.</summary>
public sealed class ReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tu-rep-" + Guid.NewGuid().ToString("N")[..8]);

    public ReportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 8, 0, 0, TimeSpan.FromHours(2));

    private static ResultRun Run(string id, string dut, ResultRating rating, double volt, int minute, string station = "Prüfplatz 01", bool dev = false, string version = "1.5")
    {
        var start = T0.AddMinutes(minute);
        var events = new List<ResultEvent>
        {
            new(1, ResultEventKind.Step, "kennung", "Prüfling > Kennung erfassen", 1, start, ResultRating.NResult, dut),
            new(2, ResultEventKind.Step, "spannung", "Prüfling > Messung Spannung", 1, start.AddSeconds(2), volt is >= 220 and <= 240 ? ResultRating.Pass : ResultRating.Fail,
                volt, "V", 220, 240, volt is >= 220 and <= 240 ? null : "außerhalb"),
        };
        if (rating == ResultRating.Error)
            events.Add(new(3, ResultEventKind.Step, "kom", "Prüfling > Abfrage", 1, start.AddSeconds(3), ResultRating.Error, Message: "Keine vollständige Antwort innerhalb von 1000 ms"));
        return new ResultRun
        {
            RunId = id, DutId = dut, Station = station, TestName = "AC-Prüfung", TestVersion = version, Started = start, Ended = start.AddSeconds(10),
            Rating = rating, IsDevelopmentRun = dev,
            Structure = [new("kennung", null, "Kennung erfassen", "schritt", null, null), new("spannung", null, "Messung Spannung", "schritt", null, null), new("kom", null, "Abfrage", "schritt", null, null)],
            Events = events,
        };
    }

    private static List<ResultRun> Sample() =>
    [
        Run("r1", "SN-1", ResultRating.Pass, 230.1, 0),
        Run("r2", "SN-2", ResultRating.Fail, 245.0, 1),
        Run("r3", "SN-2", ResultRating.Pass, 229.8, 2),          // Wiederholprüfung, danach PASS
        Run("r4", "SN-3", ResultRating.Error, 230.4, 3, "Prüfplatz 02"),
        Run("r5", "SN-4", ResultRating.Pass, 229.9, 60 * 24),    // Folgetag
        Run("r6", "SN-5", ResultRating.Pass, 231.0, 5, dev: true),
    ];

    [Fact]
    public void Kennzahlen_Pareto_und_Prozessfaehigkeit()
    {
        var s = ResultAnalysis.Compute(Sample(), new StatisticsFilter());
        Assert.Equal(5, s.Runs);                       // Entwicklungslauf ausgeschlossen
        Assert.Equal((3, 1, 1), (s.Pass, s.Fail, s.Error));
        Assert.Equal(4, s.Duts);
        Assert.Equal(1, s.Retests);
        Assert.Equal(0.5, s.FirstPassYield);           // SN-1, SN-4 beim ersten Mal PASS
        Assert.Equal(0.75, s.FinalYield);              // zusätzlich SN-2
        Assert.Equal(TimeSpan.FromSeconds(10), s.MeanDuration);
        Assert.Equal(2, s.Daily.Count);
        Assert.Equal(2, s.Stations.Count);

        var volt = s.Measurements.Single();
        Assert.Equal("Messung Spannung", volt.Name);
        Assert.Equal(5, volt.Count);
        Assert.Equal(1, volt.OutOfLimits);
        Assert.Equal((220.0, 240.0), (volt.Low!.Value, volt.High!.Value));
        var values = new[] { 230.1, 245.0, 229.8, 230.4, 229.9 };
        var mean = values.Average();
        var sd = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / 4);
        Assert.Equal(Math.Min(240 - mean, mean - 220) / (3 * sd), volt.Cpk!.Value, 1e-9);
        Assert.Equal("nicht fähig", volt.Capability);
        Assert.Equal(12, volt.Histogram().Count);
        Assert.Equal(5, volt.Histogram().Sum());

        Assert.Equal(["Messung Spannung", "Abfrage"], s.Failures.Select(f => f.Name));
        Assert.Contains("Keine vollständige Antwort", s.Failures[1].TypicalMessage);

        // Filter
        var filtered = ResultAnalysis.Compute(Sample(), new StatisticsFilter { Station = "Prüfplatz 02" });
        Assert.Equal(1, filtered.Runs);
        Assert.Equal(6, ResultAnalysis.Compute(Sample(), new StatisticsFilter { IncludeDevelopment = true }).Runs);
        Assert.Equal(2, ResultAnalysis.Compute(Sample(), new StatisticsFilter { DutPattern = "sn-2" }).Runs);
        Assert.Equal(1, ResultAnalysis.Compute(Sample(), new StatisticsFilter { From = T0.AddHours(12) }).Runs);
        Assert.Contains("Prüfplatz 02", filtered.Filter.Describe());
        Assert.Equal(0, ResultAnalysis.Compute([], new StatisticsFilter()).Runs);
    }

    [Fact]
    public void CSV_fuer_Excel_mit_Semikolon_und_Dezimalkomma()
    {
        var s = ResultAnalysis.Compute(Sample(), new StatisticsFilter());
        var csv = ResultAnalysis.StatisticsCsv(s);
        Assert.Contains("Messung Spannung;V;5;229,8;245;", csv);
        Assert.Contains("Schritt;FAIL;ERROR", csv);
        var run = ResultAnalysis.RunCsv(Sample()[1]);
        Assert.Contains("Messung Spannung", run);
        Assert.Contains(";245;V;220;240;FAIL;außerhalb", run);
        var path = Path.Combine(_dir, "a.csv");
        ResultAnalysis.SaveCsv(path, csv);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path)[..3]);
    }

    /// <summary>Strukturprüfung: Kopf, Objekte an den Offsets der Querverweistabelle, startxref, EOF.</summary>
    private static void AssertValidPdf(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        var startxref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0], CultureInfo.InvariantCulture);
        Assert.StartsWith("xref", text[startxref..]);
        var lines = text[startxref..].Split('\n');
        var count = int.Parse(lines[1].Split(' ')[1], CultureInfo.InvariantCulture);
        for (var i = 1; i < count; i++)
        {
            var offset = int.Parse(lines[2 + i][..10], CultureInfo.InvariantCulture);
            Assert.StartsWith($"{i} 0 obj", text[offset..]);
        }
    }

    [Fact]
    public async Task Pruefbericht_und_Auswertung_als_PDF()
    {
        // Echter Prüflingstest mit Kurven (L1–L3) aus dem Beispielprojekt
        var project = SampleProjects.AcPruefung();
        var (_, registry) = SampleProjects.CreateSimulation(project);
        var op = new ScriptedOperator()
            .Answer("Kennung erfassen", _ => "SN-PDF").Answer("Sichtprüfung", _ => "i. O.")
            .Answer("Gehäusetemperatur erfassen", _ => "22").Answer("Prüfling anschließen", _ => true);
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = registry, Operator = op, StationName = "Prüfplatz 01" });
        var recorder = new ResultRecorder(run);
        var recorded = new TaskCompletionSource<ResultRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.RunRecorded += r => recorded.TrySetResult(r);
        var task = run.RunAsync();
        var result = await recorded.Task.Within();
        run.RequestAbort();
        await task.Within();
        Assert.NotEmpty(result.Waveforms);

        var store = new ResultFileStore(Path.Combine(_dir, "ergebnisse"));
        var saved = store.Save(result);
        var reread = ResultFile.Read(saved.Path!).Run;
        var pdf = Reports.TestReport(reread, saved.Path).ToBytes();
        AssertValidPdf(pdf);
        var content = Encoding.Latin1.GetString(pdf);
        Assert.Contains("(Pr\\374fbericht) Tj", content);        // „ü“ als WinAnsi-Oktal
        Assert.Contains("SN-PDF", content);
        Assert.Contains("Messung Spannung", content);
        Assert.Contains("Kurven: Netzkurven aufnehmen", content);
        Assert.Contains(" l S", content);                         // Kurvenzug gezeichnet
        File.WriteAllBytes(Path.Combine(_dir, "pruefbericht.pdf"), pdf);

        // Auswertung über Ordner (inkl. beschädigter Datei)
        foreach (var r in Sample()) store.Save(r);
        File.WriteAllText(Path.Combine(store.Folder, "kaputt.tres"), "keine Ergebnisdatei");
        var loaded = ResultAnalysis.Load(store.Folder);
        Assert.Equal(8, loaded.Files);
        Assert.Equal(7, loaded.Runs.Count);
        Assert.Single(loaded.Problems);
        Assert.All(loaded.Runs, r => Assert.Empty(r.Waveforms));
        var stats = ResultAnalysis.Compute(loaded.Runs, new StatisticsFilter());
        var report = Reports.StatisticsReport(stats).ToBytes();
        AssertValidPdf(report);
        Assert.Contains("(Messwerte und Prozessf\\344higkeit) Tj", Encoding.Latin1.GetString(report));
        File.WriteAllBytes(Path.Combine(_dir, "auswertung.pdf"), report);
        PdfSamples.Keep(Path.Combine(_dir, "pruefbericht.pdf"), Path.Combine(_dir, "auswertung.pdf"));

        // Viele Zeilen → mehrere Seiten mit „Seite n von m“
        var many = Enumerable.Range(0, 120).Select(i => Run($"m{i}", $"SN-{i}", ResultRating.Pass, 230 + i % 7 * 0.1, i)).ToList();
        var big = Reports.TestReport(new ResultRun
        {
            RunId = "gross", DutId = "SN-X", TestName = "T", TestVersion = "1", Structure = many[0].Structure,
            Events = many.SelectMany(r => r.Events).Select((e, i) => e with { Sequence = i + 1 }).ToList(),
        });
        Assert.True(big.Pages.Count >= 3);
        Assert.Contains($"(Seite 1 von {big.Pages.Count}) Tj", Encoding.Latin1.GetString(big.ToBytes()));
    }

    [Fact]
    public void Auswertungsseite_filtert_und_exportiert()
    {
        var page = new ReportsViewModel(() => _dir, null, new QueueDispatcher());
        page.Use(Sample());
        Assert.True(page.HasStatistics);
        Assert.Equal("60,0 %", page.YieldText);
        Assert.Equal("50,0 %", page.FpyText);
        Assert.Equal("Messung Spannung", page.SelectedMeasurement!.Name);
        Assert.Equal(12, page.Histogram.Count);
        Assert.Contains(page.Histogram, b => b.Highlight);   // 245 V außerhalb
        Assert.Equal(["", "Prüfplatz 01", "Prüfplatz 02"], page.Stations);
        page.Station = "Prüfplatz 01";
        page.Apply();
        Assert.Equal(4, page.Statistics!.Runs);
        page.ResetFilterCommand.Execute(null);
        Assert.Equal(5, page.Statistics!.Runs);

        page.ExportPdf(Path.Combine(_dir, "seite.pdf"));
        page.ExportCsv(Path.Combine(_dir, "seite.csv"));
        AssertValidPdf(File.ReadAllBytes(Path.Combine(_dir, "seite.pdf")));
        Assert.True(File.Exists(Path.Combine(_dir, "seite.csv")));
    }


    /// <summary>In-Memory-Datenbankquelle für die Auswertung (anstelle von SQL).</summary>
    private sealed class FakeDatabase(IReadOnlyList<ResultRun> runs) : IRunQuerySource
    {
        public string Name => "Testdatenbank";
        public RunQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<ResultRun>> QueryRunsAsync(RunQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            IReadOnlyList<ResultRun> result = runs.Where(query.Matches).ToList();
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task Auswertung_aus_der_Datenbank_mit_serverseitigen_Filtern()
    {
        var db = new FakeDatabase(Sample());
        var page = new ReportsViewModel(() => _dir, null, new QueueDispatcher(), () => db);
        Assert.Equal([ReportsViewModel.FolderSource, ReportsViewModel.DatabaseSource], page.Sources);
        Assert.True(page.IsFolderSource);
        Assert.Contains("Testdatenbank", page.DatabaseHint);

        page.Source = ReportsViewModel.DatabaseSource;
        Assert.False(page.IsFolderSource);
        page.Station = "Prüfplatz 01";
        await page.LoadAsync();

        Assert.Equal("Prüfplatz 01", db.LastQuery!.Station);
        Assert.False(db.LastQuery.IncludeDevelopment);          // Standard: ohne Entwicklungsläufe
        Assert.True(page.HasStatistics);
        Assert.Equal(4, page.Statistics!.Runs);                 // r4 ist Prüfplatz 02, r6 Entwicklung
        Assert.Contains("Datenbank gelesen", page.Status);
    }

    [Fact]
    public async Task Auswertung_ohne_lesbare_Datenbank_meldet_das()
    {
        var page = new ReportsViewModel(() => _dir, null, new QueueDispatcher(), () => null);
        page.Source = ReportsViewModel.DatabaseSource;
        await page.LoadAsync();
        Assert.False(page.HasStatistics);
        Assert.Contains("Keine lesbare Datenbank", page.Status);
    }

}

/// <summary>Testhilfe: erzeugte PDFs zur Sichtprüfung aufbewahren (Umgebungsvariable TU_PDF_AUSGABE).</summary>
internal static class PdfSamples
{
    public static void Keep(params string[] files)
    {
        if (Environment.GetEnvironmentVariable("TU_PDF_AUSGABE") is not { Length: > 0 } target) return;
        Directory.CreateDirectory(target);
        foreach (var f in files) File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: true);
    }
}
