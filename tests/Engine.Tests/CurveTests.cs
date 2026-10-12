using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Curves;
using Testumgebung.Persistence.Releases;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.ViewModels;
using Xunit;

namespace Testumgebung.Engine.Tests;

/// <summary>Etappe 8: Kurven – drei Entstehungswege, Bearbeitung, Versionen, Geräteeignung, Ausgabe (W01).</summary>
public sealed class CurveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tu-kurve-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 10, 0, 0, TimeSpan.FromHours(2));

    public CurveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static CurveDefinition Sine3(string version = "0.1", double rate = 20000, double duration = 0.04)
        => CurveGenerator.Generate(new GeneratorSettings { SampleRate = rate, Duration = duration }, "AC_Profil", "AC_Profil", version, "m.weber", T0);

    // ------------------------------------------------------------------ Weg 1: Generator / Editor

    [Fact]
    public void Generator_dreiphasig_reproduzierbar_mit_gemeinsamer_Zeitbasis()
    {
        var a = Sine3();
        var b = Sine3();
        Assert.Equal(a.ContentHash(), b.ContentHash());                        // gleiche Einstellungen → bitgleich
        Assert.Equal(["L1", "L2", "L3"], a.Channels.Select(c => c.Id));
        Assert.Equal(800, a.Count);
        Assert.Equal(20000, a.SampleRate);
        Assert.All(a.Channels, c => Assert.InRange(c.Rms(), 229.7, 229.9));      // 325/√2
        // L2 = L1 um 120° verzögert: L2(t) = L1(t − 6,667 ms)
        var lag = (int)Math.Round(20000 / 50.0 / 3);
        for (var i = lag; i < 800; i += 37)
            Assert.Equal(a.Channels[0].Samples[i - lag], a.Channels[1].Samples[i], 2.0); // Versatz 133,3 Samples → max. 1,7 V
        Assert.Contains("Sinus", a.SourceText);
        Assert.Contains("120", a.SourceText);

        var ramp = CurveGenerator.Generate(new GeneratorSettings { Kind = GeneratorKind.Ramp, ChannelIds = ["L1"], RampStart = 0, RampEnd = 230, Duration = 0.01, SampleRate = 1000 },
            "R", "Rampe", "0.1", "x", T0);
        Assert.Equal([0, 23, 46], ramp.Channels[0].Samples.Take(3).Select(v => Math.Round(v, 6)));
        var steps = CurveGenerator.Generate(new GeneratorSettings { Kind = GeneratorKind.Step, ChannelIds = ["L1"], Steps = "0:0.002;230:0.003", SampleRate = 1000 },
            "S", "Stufe", "0.1", "x", T0);
        Assert.Equal([0, 0, 230, 230, 230], steps.Channels[0].Samples);
        var formula = CurveGenerator.Generate(new GeneratorSettings
        {
            Kind = GeneratorKind.Formula, ChannelIds = ["L1", "L2"], SampleRate = 1000, Duration = 0.004,
            Formulas = ["100*t*1000", "max(0, 2^3 - t*1000) + clamp(-5, -1, 1) + step(t-0.002)"],
        }, "F", "Formel", "0.1", "x", T0);
        Assert.Equal([0, 100, 200, 300], formula.Channels[0].Samples.Select(v => Math.Round(v, 9)));
        Assert.Equal([7, 6, 6, 5], formula.Channels[1].Samples.Select(v => Math.Round(v, 9)));
        Assert.Throws<CurveFormulaException>(() => CurveFormula.Compile("sin(t"));
        Assert.Throws<CurveFormulaException>(() => CurveFormula.Compile("foo(t)"));
        Assert.Throws<CurveGeneratorException>(() => CurveGenerator.Generate(new GeneratorSettings { Kind = GeneratorKind.Formula, Formulas = ["1/0"], ChannelIds = ["L1"] }, "x", "x", "1", "x", T0));
    }

    [Fact]
    public void Bearbeitung_ausdruecklich_mit_Historie_ohne_automatische_Konvertierung()
    {
        var c = Sine3();
        var trimmed = CurveOps.Trim(c, 0.010, 0.030);
        Assert.Equal(400, trimmed.Count);
        Assert.Equal(c.Channels[0].Samples[200], trimmed.Channels[0].Samples[0]);
        var scaled = CurveOps.Scale(trimmed, 0.5, "L2");
        Assert.Equal(trimmed.Channels[1].Samples[10] * 0.5, scaled.Channels[1].Samples[10]);
        Assert.Equal(trimmed.Channels[0].Samples[10], scaled.Channels[0].Samples[10]);
        var shifted = CurveOps.Shift(scaled, 0.001);
        Assert.Equal(420, shifted.Count);
        Assert.Equal(shifted.Channels[0].Samples[0], shifted.Channels[0].Samples[19]);
        var resampled = CurveOps.Resample(shifted, 10000);
        Assert.Equal(10000, resampled.SampleRate);
        Assert.Equal(["Zuschnitt 10–30 ms", "Skalierung ×0.5 (L2)", "Zeitverschiebung 1 ms (20 Samples)", "Abtastrate ausdrücklich geändert: 20000 → 10000 Hz (linear interpoliert)"],
            resampled.History);
        Assert.Equal(800, c.Count);                                              // Original unverändert
        var mapped = CurveOps.MapChannels(c, [("L3", "L1"), ("L1", "L3")]);
        Assert.Equal(c.Channels[2].Samples, mapped.Channels[0].Samples);
        Assert.Throws<ArgumentException>(() => CurveOps.MapChannels(c, [("L1", "X"), ("L2", "X")]));
    }

    [Fact]
    public void Versionen_unveraenderlich_im_Projekt_und_im_Release_Paket()
    {
        var project = SampleProjects.AcPruefung();
        var lib = project.Definition.Curves;
        Assert.Equal("0.4", lib.Find("AC_Profil")!.Version);
        Assert.Throws<InvalidOperationException>(() => lib.Add(Sine3("0.4")));     // Version vergeben
        Assert.Equal("0.5", lib.NextVersion("AC_Profil"));
        lib.Add(CurveOps.Scale(lib.Find("AC_Profil")!, 1.05).WithVersion("0.5"));
        Assert.Equal(["0.4", "0.5"], lib.VersionsOf("AC_Profil").Select(v => v.Version));
        Assert.Equal("0.5", lib.Find("AC_Profil")!.Version);
        Assert.Equal("0.4", lib.Find("AC_Profil", "0.4")!.Version);

        var serializer = new ProjectSerializer();
        var json = serializer.Serialize(project);
        var loaded = serializer.Deserialize(json);
        Assert.Equal(lib.Versions.Select(v => v.ContentHash()), loaded.Definition.Curves.Versions.Select(v => v.ContentHash()));
        Assert.Equal(lib.Find("AC_Profil", "0.5")!.History, loaded.Definition.Curves.Find("AC_Profil", "0.5")!.History);
        // Manipulierte Samples fallen beim Laden auf
        var at = json.IndexOf("\"samples\": \"", StringComparison.Ordinal) + 12 + 40;
        var tampered = json[..at] + (json.Substring(at, 4) == "AAAA" ? "////" : "AAAA") + json[(at + 4)..];
        Assert.Throws<ProjectFormatException>(() => serializer.Deserialize(tampered));

        var (bytes, manifest) = ReleasePackage.Build(project, "m.weber", T0);
        var curves = manifest.Entries.Where(e => e.Area == "Kurven").ToList();
        Assert.Equal([("AC_Profil", "0.4"), ("AC_Profil", "0.5")], curves.Select(e => (e.Name, e.Version)));
        Assert.True(ReleasePackage.Open(bytes).IsIntact);

        // Kurve in Init geändert → nicht am Prüfling übernehmbar
        var next = serializer.Clone(project);
        ((StepDefinition)next.Definition.Find("init.kurve.laden")!).Parameters["Version"] = "0.5";
        Assert.False(DefinitionCompatibility.CanSwapAtDut(project, next, out var reason));
        Assert.Contains("Init", reason);
    }

    // ------------------------------------------------------------------ Weg 2: Datei

    [Fact]
    public void CSV_mit_Kanalzuordnung_Einheiten_expliziter_Zeit_und_Qualitaetscodes()
    {
        var csv = "Zeit_s;U_L1 [V];U_L2 [V];U_L3 [V];Qualitätscode\n0,000;0,0;0,0;0,0;0\n0,010;12,5;-12,1;0,3;0\n0,020;230,1;-229,4;0,8;0\n0,030;325,0;-324,1;1,2;3\n0,040;229,8;-230,6;0,9;0\n";
        File.WriteAllText(P("beispiel.csv"), csv, Encoding.UTF8);
        var t = CurveImport.Read(P("beispiel.csv"));
        Assert.Equal(("CSV/Text", 5, 5), (t.Format, t.Rows, t.Columns.Count));
        Assert.Equal(("Zeit_s", 1.0), (t.TimeColumn, t.TimeFactor));
        Assert.Equal("Qualitätscode", t.QualityColumn);
        Assert.Equal("V", t.Columns.Single(c => c.Name == "U_L1").Unit);

        var s = CurveImport.SuggestSettings(t);
        Assert.Equal(["U_L1→L1", "U_L2→L2", "U_L3→L3"], s.Channels.Select(m => $"{m.SourceColumn}→{m.TargetChannel}"));
        var curve = CurveImport.ToCurve(t, s, "AC_Import", "AC_Import", "0.1", "m.weber", T0);
        Assert.True(curve.HasExplicitTimes);
        Assert.Equal([0, 0.01, 0.02, 0.03, 0.04], curve.Times!.Select(x => Math.Round(x, 9)));
        Assert.Equal(325.0, curve.Channels[0].Samples[3]);
        Assert.Equal("1 Zeile(n) mit Qualitätscode ≠ 0", curve.Quality);
        Assert.Contains("U_L1→L1 [V]", curve.SourceText);
        var summary = CurveImport.Summarize(t, s);
        Assert.Equal((-324.1, 0.0), (summary[1].Min, summary[1].Max));

        // Reproduzierbar: gleiche Datei, gleiche Zuordnung → gleicher Inhalt
        var again = CurveImport.ToCurve(CurveImport.Read(P("beispiel.csv")), s, "AC_Import", "AC_Import", "0.1", "m.weber", T0);
        Assert.Equal(curve.ContentHash(), again.ContentHash());

        // Andere Zuordnung und feste Abtastrate (ausdrücklich angegeben)
        var fixedRate = CurveImport.ToCurve(t, s with { Mode = TimeBaseMode.FixedRate, SampleRate = 100, Channels = [new("U_L3", "L1", "V")] }, "X", "X", "0.1", "x", T0);
        Assert.Equal((100.0, false), (fixedRate.SampleRate, fixedRate.HasExplicitTimes));
        Assert.Equal(1.2, fixedRate.Channels[0].Samples[3]);
        Assert.Throws<CurveImportException>(() => CurveImport.ToCurve(t, s with { Mode = TimeBaseMode.FixedRate, SampleRate = null }, "X", "X", "1", "x", T0));

        // Komma-getrennt mit Punkt, Millisekunden
        var t2 = Csv.Parse(Encoding.UTF8.GetBytes("t_ms,L1\n0,1.5\n1,2.5\n"), "ms.csv");
        Assert.Equal((0.001, ","), (t2.TimeFactor, ","));
        Assert.Equal(2.5, t2.Column("L1")[1]);
        var unsorted = Csv.Parse(Encoding.UTF8.GetBytes("Zeit_s;L1\n0;1\n0;2\n"), "x.csv");
        Assert.Throws<CurveImportException>(() => CurveImport.ToCurve(unsorted, CurveImport.SuggestSettings(unsorted), "X", "X", "1", "x", T0));
    }

    [Fact]
    public void COMTRADE_ASCII_und_binaer_mit_Skalierung_und_Abtastrate()
    {
        var cfg = string.Join("\r\n",
            "Netzanalysator,PP01,2013",
            "4,3A,1D",
            "1,UL1,A,,V,0.1,0,0,-32767,32767,1,1,P",
            "2,UL2,B,,V,0.1,0,0,-32767,32767,1,1,P",
            "3,UL3,C,,V,0.1,0,0,-32767,32767,1,1,P",
            "1,Trigger,,,0",
            "50",
            "1",
            "1000,4",
            "02/10/2026,10:00:00.000000",
            "02/10/2026,10:00:00.000000",
            "ASCII",
            "1");
        File.WriteAllText(P("aufz.cfg"), cfg);
        File.WriteAllText(P("aufz.dat"), "1,0,0,0,0,0\n2,1000,3250,-1625,-1625,0\n3,2000,0,2815,-2815,1\n4,3000,-3250,1625,1625,0\n");
        var t = CurveImport.Read(P("aufz.cfg"));
        Assert.StartsWith("COMTRADE 2013 (ASCII)", t.Format);
        Assert.Equal(1000, t.SampleRate);
        Assert.Equal(["Zeit", "UL1", "UL2", "UL3"], t.Columns.Select(c => c.Name));
        Assert.Equal([0, 325, 0, -325], t.Column("UL1").Select(v => Math.Round(v, 9)));
        Assert.Contains(t.Warnings, w => w.Contains("Digital"));
        var s = CurveImport.SuggestSettings(t) with { Mode = TimeBaseMode.FixedRate };
        var curve = CurveImport.ToCurve(t, s, "C", "C", "0.1", "x", T0);
        Assert.Equal((1000.0, false), (curve.SampleRate, curve.HasExplicitTimes));
        Assert.Equal(["L1", "L2", "L3"], curve.Channels.Select(c => c.Id));

        // gleiche Daten binär (BINARY, int16) und FLOAT32 → gleiche Werte
        foreach (var (ft, width) in new[] { ("BINARY", 2), ("FLOAT32", 4) })
        {
            File.WriteAllText(P($"bin_{ft}.cfg"), cfg.Replace("ASCII", ft, StringComparison.Ordinal));
            var raw = new[] { new short[] { 0, 0, 0 }, [3250, -1625, -1625], [0, 2815, -2815], [-3250, 1625, 1625] };
            var rec = 8 + 3 * width + 2;
            var dat = new byte[rec * 4];
            for (var r = 0; r < 4; r++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(dat.AsSpan(r * rec), r + 1);
                BinaryPrimitives.WriteInt32LittleEndian(dat.AsSpan(r * rec + 4), r * 1000);
                for (var c = 0; c < 3; c++)
                {
                    if (width == 2) BinaryPrimitives.WriteInt16LittleEndian(dat.AsSpan(r * rec + 8 + c * 2), raw[r][c]);
                    else BinaryPrimitives.WriteSingleLittleEndian(dat.AsSpan(r * rec + 8 + c * 4), raw[r][c]);
                }
            }
            File.WriteAllBytes(P($"bin_{ft}.dat"), dat);
            var bt = CurveImport.Read(P($"bin_{ft}.cfg"));
            Assert.Equal(t.Column("UL2").Select(v => Math.Round(v, 6)), bt.Column("UL2").Select(v => Math.Round(v, 6)));
        }
    }

    [Fact]
    public void Excel_Arbeitsmappe_mit_gemeinsamen_Zeichenfolgen()
    {
        File.WriteAllBytes(P("messung.xlsx"), MinimalXlsx(
            ["Zeit_s", "U_L1", "U_L2"],
            [[0, 0, 0], [0.001, 100.5, -50.25], [0.002, 200, -100]]));
        var t = CurveImport.Read(P("messung.xlsx"));
        Assert.Equal(("Excel (.xlsx)", 3, "Zeit_s"), (t.Format, t.Rows, t.TimeColumn));
        Assert.Equal(-50.25, t.Column("U_L2")[1]);
        var curve = CurveImport.ToCurve(t, CurveImport.SuggestSettings(t) with { Channels = [new("U_L1", "L1", "V"), new("U_L2", "L2", "V")] }, "X", "X", "0.1", "x", T0);
        Assert.Equal([0, 0.001, 0.002], curve.Times!);
        Assert.Equal(["Tabelle1"], Xlsx.SheetNames(File.ReadAllBytes(P("messung.xlsx"))));
    }

    // ------------------------------------------------------------------ Weg 3: Messkurve übernehmen

    [Fact]
    public async Task Messkurve_aus_Ergebnisdatei_als_neue_Ausgabekurve_Original_unveraendert()
    {
        var file = await RecordToFile();
        var before = File.ReadAllBytes(file);
        var content = ResultFile.Read(file);
        var source = Assert.Single(ResultFileSource.List(content));
        Assert.StartsWith("Netzkurven aufnehmen – Spannung_L1, Spannung_L2, Spannung_L3", source.Title);
        var t = ResultFileSource.ToTable(content, source, Path.GetFileName(file));
        Assert.Equal(5000, t.SampleRate);
        var s = CurveImport.SuggestSettings(t);
        Assert.Equal(["Spannung_L1→L1", "Spannung_L2→L2", "Spannung_L3→L3"], s.Channels.Select(m => $"{m.SourceColumn}→{m.TargetChannel}"));
        var curve = CurveImport.ToCurve(t, s, "AC_Profil_copy", "AC_Profil_copy", "0.1", "m.weber", T0);
        Assert.Equal(CurveOrigin.Measurement, curve.Origin);
        Assert.Equal(source.Waveforms[0].Data.Samples, curve.Channels[0].Samples);
        Assert.Contains("SN-W01", curve.SourceText);
        Assert.Equal(before, File.ReadAllBytes(file));                           // Originale Messung bleibt unverändert
    }

    // ------------------------------------------------------------------ Geräteeignung und Ausgabe

    private static OutputCapabilities Caps() => new("AC-Quelle (Simulation)", "V", -400, 400, ["L1", "L2", "L3"], [20000, 10000], false, 100000);

    [Fact]
    public void Geraeteeignung_ohne_automatische_Skalierung_oder_Abtastratenaenderung()
    {
        Assert.False(OutputSuitability.Check(Caps(), Sine3().ToOutput()).Single().IsError);
        Assert.Contains("Andere Abtastrate", OutputSuitability.Check(Caps(), Sine3(rate: 5000).ToOutput()).Single(i => i.IsError).Text);
        Assert.Contains("Skalierung", OutputSuitability.Check(Caps(), CurveOps.Scale(Sine3(), 1.5).ToOutput()).First(i => i.IsError).Text);
        var amps = CurveGenerator.Generate(new GeneratorSettings { Unit = "A", SampleRate = 20000 }, "I", "I", "1", "x", T0);
        Assert.Contains("Einheit A statt V", OutputSuitability.Check(Caps(), amps.ToOutput()).First(i => i.IsError).Text);
        var csv = Csv.Parse(Encoding.UTF8.GetBytes("Zeit_s;L1\n0;1\n0,001;2\n"), "x.csv");
        var explicitCurve = CurveImport.ToCurve(csv, CurveImport.SuggestSettings(csv) with { Channels = [new("L1", "L1", "V")] }, "E", "E", "1", "x", T0);
        Assert.Contains("expliziten Zeitpunkte", OutputSuitability.Check(Caps(), explicitCurve.ToOutput()).Single(i => i.IsError).Text);
        var made = CurveOps.Resample(explicitCurve, 10000);                    // ausdrückliche Anpassung → geeignet
        Assert.False(OutputSuitability.Check(Caps(), made.ToOutput()).Single().IsError);
    }

    [Fact]
    public async Task Ausgabe_Laden_Uebertragen_Starten_Stoppen_in_Reihenfolge_und_messbar()
    {
        var sim = new SimulationAdapter("AC");
        sim.SetSine("u1", 100);
        sim.ConfigureOutput("out", Caps(), new Dictionary<string, string> { ["L1"] = "u1" });
        var curve = Sine3().ToOutput();
        await Assert.ThrowsAsync<OutputSequenceException>(() => sim.StartAsync("out", default));
        await Assert.ThrowsAsync<OutputSequenceException>(() => sim.TransferAsync("out", default));
        await Assert.ThrowsAsync<OutputSequenceException>(() => sim.LoadAsync("out", Sine3(rate: 5000).ToOutput(), default));
        await sim.LoadAsync("out", curve, default);
        Assert.Equal(OutputState.Loaded, sim.GetOutputState("out"));
        await Assert.ThrowsAsync<OutputSequenceException>(() => sim.StartAsync("out", default));
        await sim.TransferAsync("out", default);
        await sim.StartAsync("out", default);
        var during = await sim.CaptureAsync(["u1"], 20000, 400, default);
        Assert.InRange(during.Channels[0].Rms(), 229, 231);                      // Ausgabe wirkt auf den Messkanal
        await Assert.ThrowsAsync<OutputSequenceException>(() => sim.LoadAsync("out", curve, default));
        await sim.StopAsync("out", default);
        var after = await sim.CaptureAsync(["u1"], 20000, 400, default);
        Assert.InRange(after.Channels[0].Rms(), 99, 101);                        // ursprüngliches Signal wieder aktiv
        await sim.StartAsync("out", default);                                   // erneut starten ohne neues Übertragen
        Assert.Equal(["laden AC_Profil@0.1", "übertragen AC_Profil@0.1", "starten AC_Profil@0.1", "stoppen", "starten AC_Profil@0.1"], sim.OutputLog.ToList());
    }

    // ------------------------------------------------------------------ W01

    /// <summary>
    /// W01: Drei Kurvenwege und drei Phasen – Zeitbasis, Kanalzuordnung und Version reproduzierbar.
    /// Generator → Ausgabe im Ablauf → Messung → Ergebnisdatei → Messkurve übernehmen → Datei-Rundreise (CSV).
    /// </summary>
    [Fact]
    public async Task W01_Drei_Wege_drei_Phasen_Zeitbasis_Kanalzuordnung_Version_reproduzierbar()
    {
        // Weg 1 (Generator, Version 0.4 im Beispielprojekt) wird im Ablauf ausgegeben und gemessen
        var project = SampleProjects.AcPruefung();
        var generated = project.Definition.Curves.Find("AC_Profil", "0.4")!;
        var file = await RecordToFile(project);
        var content = ResultFile.Read(file);
        Assert.Contains(content.Run.Events, e => e.NodeId == "netzkurven" && e.Rating == ResultRating.Pass);

        // Weg 3: gemessene Kurve übernehmen (5 kHz Aufnahme der 20-kHz-Ausgabe)
        var measured = CurveImport.ToCurve(ResultFileSource.ToTable(content, ResultFileSource.List(content)[0], "w01.tres"),
            CurveImport.SuggestSettings(ResultFileSource.ToTable(content, ResultFileSource.List(content)[0], "w01.tres")), "AC_Mess", "AC_Mess", "0.1", "m.weber", T0);
        Assert.Equal(["L1", "L2", "L3"], measured.Channels.Select(c => c.Id));
        Assert.Equal(5000, measured.SampleRate);
        for (var c = 0; c < 3; c++)                                               // gemessen = ausgegeben (gleiche Phase, gleicher Kanal)
            for (var i = 0; i < measured.Count; i += 13)
                Assert.Equal(generated.ValueAt(c, (i / 5000.0) % generated.Duration), measured.Channels[c].Samples[i], 1e-6);

        // Weg 2: als CSV exportieren und wieder importieren – Zeitbasis und Zuordnung bleiben erhalten
        var sb = new StringBuilder("Zeit_s;Phase_A [V];Phase_B [V];Phase_C [V]\n");
        for (var i = 0; i < measured.Count; i++)
            sb.Append(CultureInfo.GetCultureInfo("de-DE"), $"{measured.TimeOf(i):0.######};{measured.Channels[0].Samples[i]:R};{measured.Channels[1].Samples[i]:R};{measured.Channels[2].Samples[i]:R}\n");
        File.WriteAllText(P("export.csv"), sb.ToString());
        var t = CurveImport.Read(P("export.csv"));
        var settings = CurveImport.SuggestSettings(t) with
        {
            Mode = TimeBaseMode.FixedRate, SampleRate = 5000,
            Channels = [new("Phase_A", "L1", "V"), new("Phase_B", "L2", "V"), new("Phase_C", "L3", "V")],
        };
        var imported = CurveImport.ToCurve(t, settings, "AC_Mess", "AC_Mess", "0.2", "m.weber", T0);
        Assert.Equal(measured.Channels.Select(c => c.Samples), imported.Channels.Select(c => c.Samples));
        Assert.Equal(measured.SampleRate, imported.SampleRate);

        // Versionen: alle drei Wege im Projekt, Rundreise bitgleich, Hashes im Release-Paket
        project.Definition.Curves.Add(measured);
        project.Definition.Curves.Add(imported);
        var serializer = new ProjectSerializer();
        var reloaded = serializer.Deserialize(serializer.Serialize(project));
        Assert.Equal(project.Definition.Curves.Versions.Select(v => (v.Key, v.ContentHash(), v.Origin)),
            reloaded.Definition.Curves.Versions.Select(v => (v.Key, v.ContentHash(), v.Origin)));
        var manifest = ReleasePackage.Build(project, "m.weber", T0).Manifest;
        Assert.Equal(3, manifest.Entries.Count(e => e.Area == "Kurven"));
        Assert.Equal(manifest.Entries, ReleasePackage.Build(reloaded, "m.weber", T0).Manifest.Entries); // reproduzierbar
    }

    // ------------------------------------------------------------------ Hilfen

    private async Task<string> RecordToFile(TestProject? project = null)
    {
        project ??= SampleProjects.AcPruefung();
        var (sim, registry) = SampleProjects.CreateSimulation(project);
        var op = new ScriptedOperator()
            .Answer("Kennung erfassen", _ => "SN-W01").Answer("Sichtprüfung", _ => "i. O.")
            .Answer("Gehäusetemperatur erfassen", _ => "22").Answer("Prüfling anschließen", _ => true);
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = registry, Operator = op });
        var recorder = new ResultRecorder(run);
        var tcs = new TaskCompletionSource<ResultRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.RunRecorded += r => tcs.TrySetResult(r);
        var task = run.RunAsync();
        var recorded = await tcs.Task.Within();
        run.RequestAbort();
        await task.Within();
        Assert.Equal(["laden AC_Profil@0.4", "übertragen AC_Profil@0.4", "starten AC_Profil@0.4", "stoppen"], sim.OutputLog.ToList());
        Assert.All(run.Events.OfType<StepCompleted>().Where(e => e.NodeId.Contains("kurve")), e => Assert.NotEqual(ResultRating.Error, e.Rating));
        return new ResultFileStore(P("ergebnisse")).Save(recorded).Path!;
    }

    /// <summary>Minimale .xlsx (Kopfzeile als gemeinsame Zeichenfolgen, Zahlen als Werte).</summary>
    private static byte[] MinimalXlsx(string[] header, double[][] rows)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Add("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add("xl/workbook.xml", $"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Tabelle1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Add("xl/sharedStrings.xml", $"<sst xmlns=\"{ns}\">" + string.Concat(header.Select(h => $"<si><t>{h}</t></si>")) + "</sst>");
            var sb = new StringBuilder($"<worksheet xmlns=\"{ns}\"><sheetData><row r=\"1\">");
            for (var c = 0; c < header.Length; c++) sb.Append($"<c r=\"{(char)('A' + c)}1\" t=\"s\"><v>{c}</v></c>");
            sb.Append("</row>");
            for (var r = 0; r < rows.Length; r++)
            {
                sb.Append($"<row r=\"{r + 2}\">");
                for (var c = 0; c < rows[r].Length; c++) sb.Append($"<c r=\"{(char)('A' + c)}{r + 2}\"><v>{rows[r][c].ToString(CultureInfo.InvariantCulture)}</v></c>");
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            Add("xl/worksheets/sheet1.xml", sb.ToString());
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ Oberfläche (ViewModels)

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Nicht erreicht: " + what);
            await Task.Delay(5);
        }
    }

    [Fact]
    public void Kurvenanzeige_Zoom_Cursor_Effektivwert_und_Spitzen_erhaltende_Reduktion()
    {
        var plot = new WaveformPlotViewModel();
        Assert.False(plot.HasData);
        plot.SetData(Sine3());
        Assert.Equal(["L1", "L2", "L3"], plot.Channels.Select(c => c.Id));
        Assert.All(plot.Channels, c => Assert.StartsWith("229.8", c.RmsText));
        plot.SetCursorAtX(125);                                                  // 5 ms
        Assert.Equal("325 V", plot.Channels[0].CursorText);
        Assert.Equal("Cursor 5 ms", plot.CursorText);
        plot.ZoomToX(0, 500);                                                    // erste Periode
        Assert.Equal(0.02, plot.ViewEnd, 1e-9);
        Assert.True(plot.HasCursor);
        Assert.Equal(250, plot.CursorX, 1e-6);
        plot.Channels[1].IsVisible = false;
        Assert.Equal(["L1", "L3"], plot.Series.Select(x => x.Channel));
        plot.ZoomAllCommand.Execute(null);
        Assert.Equal(0.04, plot.ViewEnd, 1e-9);

        // 1 Mio. Samples: höchstens 2 Punkte je Spalte, Spitze bleibt sichtbar
        var big = new double[1_000_000];
        big[654_321] = 1000;
        plot.SetData([new PlotSource("X", "V", big)], 1_000_000, null);
        var pts = Assert.Single(plot.Series).Points;
        Assert.InRange(pts.Count, 1000, 2000);
        Assert.Contains(pts, p => p.Y < 20);                                     // Spitze oben im Bild
    }

    [Fact]
    public void Kurvenseite_Editor_Bearbeitung_Eignung_und_unveraenderliche_Versionen()
    {
        var editor = new EditorViewModel(SampleProjects.AcPruefung());
        var vm = new CurvesViewModel(editor, null, () => "m.weber@simuliert.local", () => null, ImmediateDispatcher.Instance);
        var item = Assert.Single(vm.Curves);
        Assert.Equal("AC_Profil  v0.4", item.Title);
        Assert.Equal("AC-Profil laden", item.Usage);
        Assert.Equal("AC_Kurvenquelle", vm.SelectedOutput);

        vm.Tab = CurveTab.Generator;
        (vm.CurveId, vm.CurveName, vm.CurveVersion) = ("AC_Profil", "AC_Profil", "0.5");
        (vm.GenAmplitude, vm.GenRate, vm.GenDurationMs) = ("300", "10000", "40");
        vm.GenerateCommand.Execute(null);
        Assert.Null(vm.Error);
        Assert.NotNull(vm.Working);
        Assert.Equal(400, vm.Working!.Count);
        Assert.True(vm.Plot.HasData);
        Assert.True(vm.IsSuitable);                                              // 10 kHz wird vom Gerät unterstützt
        Assert.StartsWith("Geräteeignung geprüft", vm.Suitability.Single().Text);

        vm.ScaleFactor = "2";
        vm.ScaleCommand.Execute(null);                                           // 600 V > 400 V
        Assert.False(vm.IsSuitable);
        Assert.Contains(vm.Suitability, i => i.IsError && i.Text.Contains("keine automatische Skalierung"));
        Assert.Contains(vm.WorkingDetails, d => d.Contains("Skalierung ×2"));
        vm.UndoCommand.Execute(null);
        Assert.True(vm.IsSuitable);
        vm.ResampleHz = "5000";
        vm.ResampleCommand.Execute(null);
        Assert.Contains(vm.Suitability, i => i.IsError && i.Text.Contains("Andere Abtastrate"));
        vm.UndoCommand.Execute(null);
        vm.ShiftMs = "1";
        vm.ShiftCommand.Execute(null);
        Assert.Equal(410, vm.Working!.Count);
        vm.GenFormulas = "sin(";                                                 // Formelfehler wird angezeigt, Arbeitskopie bleibt
        vm.GenKind = GeneratorKind.Formula;
        vm.GenerateCommand.Execute(null);
        Assert.NotNull(vm.Error);
        Assert.Equal(410, vm.Working!.Count);

        Assert.False(editor.IsDirty);
        vm.SaveVersion();
        Assert.Null(vm.Error);
        Assert.True(editor.IsDirty);
        Assert.Equal(["0.4", "0.5"], editor.Project.Definition.Curves.VersionsOf("AC_Profil").Select(c => c.Version));
        Assert.Equal("m.weber@simuliert.local", editor.Project.Definition.Curves.Find("AC_Profil", "0.5")!.CreatedBy);
        Assert.Equal(CurveTab.Library, vm.Tab);
        Assert.Equal("AC_Profil@0.5", vm.SelectedCurve!.Curve.Key);
        Assert.Equal("0.6", vm.CurveVersion);
        Assert.False(vm.IsEditing);

        // Gespeicherte Version ist unveränderlich: gleiche Versionsnummer wird abgelehnt
        vm.SelectedCurve = vm.Curves.First(c => c.Curve.Version == "0.4");
        vm.NewFromSelectedCommand.Execute(null);
        Assert.True(vm.IsEditing);
        vm.CurveVersion = "0.4";
        vm.SaveVersion();
        Assert.Contains("unveränderlich", vm.Error);
        Assert.Equal(2, editor.Project.Definition.Curves.Versions.Count);

        // Projekt speichern/laden: beide Versionen mit Historie
        var loaded = new ProjectSerializer().Deserialize(new ProjectSerializer().Serialize(editor.Project));
        Assert.Contains("Zeitverschiebung 1 ms", string.Join("|", loaded.Definition.Curves.Find("AC_Profil", "0.5")!.History));
    }

    [Fact]
    public async Task Kurvenseite_Dateiimport_Messkurve_und_Testausgabe_in_vier_Phasen()
    {
        File.WriteAllText(P("netz.csv"), "Zeit [ms];U1 [V];U2 [V];Status\n0;0;0;0\n10;100;-100;0\n20;0;0;1\n30;-100;100;0\n", Encoding.UTF8);
        var file = await RecordToFile();
        var editor = new EditorViewModel(SampleProjects.AcPruefung());
        var vm = new CurvesViewModel(editor, null, () => "m.weber", () => new ResultFileStore(Path.GetDirectoryName(file)!), ImmediateDispatcher.Instance);

        // Weg 1: Datei
        vm.ImportFile(P("netz.csv"));
        Assert.Null(vm.Error);
        Assert.Equal(CurveTab.File, vm.Tab);
        Assert.True(vm.IsImportTab && vm.HasTable);
        Assert.Equal(TimeBaseMode.ExplicitTimes, vm.TimeMode);
        Assert.Equal("0.001", vm.TimeFactor);
        Assert.Equal("Status", vm.QualityColumn);
        Assert.Equal(["U1", "U2"], vm.Mapping.Select(m => m.Source));
        Assert.Equal(4, vm.Preview!.Count);
        vm.Mapping[0].Target = "L1";
        vm.Mapping[1].Target = "L2";
        (vm.CurveId, vm.CurveName, vm.CurveVersion) = ("Netz", "Netz", "1.0");
        vm.ApplyImportCommand.Execute(null);
        Assert.Null(vm.Error);
        Assert.Equal(CurveOrigin.FileImport, vm.Working!.Origin);
        Assert.True(vm.Working.HasExplicitTimes);
        Assert.Equal(["L1", "L2"], vm.Working.Channels.Select(c => c.Id));
        Assert.Contains(vm.Suitability, i => i.IsError && i.Text.Contains("expliziten Zeitpunkte"));
        vm.ResampleHz = "10000";
        vm.ResampleCommand.Execute(null);
        Assert.True(vm.IsSuitable);
        Assert.Contains(vm.WorkingDetails, d => d.Contains("Abtastrate ausdrücklich geändert"));

        // Weg 2: Messkurve aus der Ergebnisdatei (Datei bleibt unverändert)
        var before = File.ReadAllBytes(file);
        vm.Tab = CurveTab.Measurement;
        var rf = Assert.Single(vm.ResultFiles);
        vm.SelectedResultFile = rf;
        Assert.Single(vm.ResultSources);
        Assert.Equal(["Spannung_L1", "Spannung_L2", "Spannung_L3"], vm.Mapping.Select(m => m.Source));
        Assert.Equal(["L1", "L2", "L3"], vm.Mapping.Select(m => m.Target));
        vm.CurveId = "Neue_Kurve";
        vm.ApplyImportCommand.Execute(null);
        Assert.Null(vm.Error);
        Assert.Equal(CurveOrigin.Measurement, vm.Working!.Origin);
        Assert.StartsWith("Messkurve_", vm.CurveId);
        Assert.Equal(5000, vm.Working.SampleRate);
        Assert.Equal(before, File.ReadAllBytes(file));

        // Testausgabe der freigabefähigen Version: Reihenfolge wird erzwungen
        vm.Tab = CurveTab.Library;
        vm.SelectedCurve = vm.Curves.Single(c => c.Curve.Key == "AC_Profil@0.4");
        Assert.True(vm.IsSuitable);
        vm.StartCommand.Execute(null);
        await Until(() => vm.OutputLog.Count == 1, "abgelehnt");
        Assert.StartsWith("Abgelehnt", vm.OutputLog[0][10..]);
        vm.LoadCommand.Execute(null);
        await Until(() => vm.OutputStateText.StartsWith("Geladen"), "geladen");
        vm.TransferCommand.Execute(null);
        await Until(() => vm.OutputStateText.StartsWith("Im Gerät"), "übertragen");
        vm.StartCommand.Execute(null);
        await Until(() => vm.OutputLog.Any(l => l.Contains("Rückmessung")), "Rückmessung");
        Assert.Contains("L1→u1 229.81 V eff.", vm.OutputLog.First(l => l.Contains("Rückmessung")));
        Assert.StartsWith("Ausgabe läuft (AC_Profil@0.4)", vm.OutputStateText);
        vm.StopCommand.Execute(null);
        await Until(() => vm.OutputStateText.StartsWith("Gestoppt"), "gestoppt");
    }
}
