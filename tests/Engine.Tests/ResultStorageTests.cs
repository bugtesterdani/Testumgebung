using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Storage;
using Testumgebung.Engine;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.ViewModels;
using Xunit;

namespace Testumgebung.Engine.Tests;

/// <summary>Etappe 5: Ergebnisdatei, Reader, Filter, Kurvenchunks, lokale Outbox (Abschnitt 9, Tests P01–P05).</summary>
public sealed class ResultStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tu-ergebnis-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string Dir(string name) => Path.Combine(_dir, name);

    /// <summary>Einen Prüflingstest des Beispielprojekts ausführen und die Aufzeichnung liefern.</summary>
    private static async Task<(ResultRun Run, TestDefinition Definition)> RecordOnce(TestProject? project = null, string serial = "SN-1")
    {
        project ??= SampleProjects.AcPruefung();
        var (_, registry) = SampleProjects.CreateSimulation(project);
        var op = new ScriptedOperator()
            .Answer("Kennung erfassen", _ => serial).Answer("Sichtprüfung", _ => "i. O.")
            .Answer("Gehäusetemperatur erfassen", _ => "22").Answer("Prüfling anschließen", _ => true);
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = registry, Operator = op, StationName = "Prüfplatz 07" });
        var recorder = new ResultRecorder(run);
        var recorded = new TaskCompletionSource<ResultRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.RunRecorded += r => recorded.TrySetResult(r);
        var task = run.RunAsync();
        var result = await recorded.Task.Within();
        run.RequestAbort();
        await task.Within();
        return (result, project.Definition);
    }

    // ------------------------------------------------------------------ Ergebnisdatei und Reader

    [Fact]
    public void Grosse_Kurve_bleibt_beim_Datei_Rundlauf_dateibasiert_und_Index_wird_geprueft()
    {
        const int count = SampleBuffer.MemoryLimit + 123;
        using var writer = new SampleBufferWriter(count);
        for (var offset = 0; offset < count; offset += SampleBuffer.BlockSize)
            writer.Append(Enumerable.Range(offset, Math.Min(SampleBuffer.BlockSize, count - offset)).Select(i => i * 0.125).ToArray());
        var values = writer.Complete();
        Assert.True(values.IsFileBacked);
        var wave = new Waveform("A", "V", 1000, DateTimeOffset.UnixEpoch, values) { TimeValues = values };
        Directory.CreateDirectory(_dir);
        var path = Dir("large.tres");
        using (var file = File.Create(path)) ResultFile.Write(file, SyntheticRun(wave));
        var read = ResultFile.Read(path);
        Assert.True(read.IsIntact, string.Join("; ", read.Problems));
        var back = Assert.Single(read.Run.Waveforms).Data.Values;
        Assert.True(back.IsFileBacked);
        Assert.Equal(count, back.Count);
        Assert.Equal(values, back);
        var backTimes = Assert.Single(read.Run.Waveforms).Data.TimeValues;
        Assert.NotNull(backTimes);
        Assert.True(backTimes.IsFileBacked);
        Assert.Equal(values[count - 1], backTimes[count - 1]);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            file.Position = file.Length - 12;
            file.Write(new byte[8]); // ungültiger Indexzeiger bei sonst intakten Chunks
        }
        Assert.False(ResultFile.Read(path).IsIntact);
    }

    [Fact]
    public async Task Ergebnisdatei_Rundreise_ist_verlustfrei_und_ohne_Testversion_lesbar()
    {
        var (run, _) = await RecordOnce(serial: "SN-4711");
        Assert.Equal("SN-4711", run.DutId);
        Assert.Equal("Prüfplatz 07", run.Station);
        Assert.Equal(ResultRating.Pass, run.Rating);

        var store = new ResultFileStore(Dir("ergebnisse"));
        var saved = store.Save(run);
        Assert.True(saved.Success, saved.Error);
        Assert.True(saved.Path!.EndsWith($"_SN-4711_{run.RunId}.tres", StringComparison.Ordinal), saved.Path);
        Assert.Empty(Directory.GetFiles(Dir("ergebnisse"), "*.tmp", SearchOption.AllDirectories));

        // Lesen nur aus der Datei – keine Projektdefinition nötig
        var read = ResultFile.Read(saved.Path!);
        Assert.True(read.IsIntact, string.Join("; ", read.Problems));
        var r = read.Run;
        Assert.Equal((run.RunId, run.DutId, run.Rating, run.TestName, run.TestVersion), (r.RunId, r.DutId, r.Rating, r.TestName, r.TestVersion));
        Assert.Equal(run.Started, r.Started);
        Assert.Equal(run.Ended, r.Ended);
        Assert.Equal(run.Structure, r.Structure);
        Assert.Equal(run.Events.Count, r.Events.Count);
        for (var i = 0; i < run.Events.Count; i++)
        {
            var (a, b) = (run.Events[i], r.Events[i]);
            Assert.Equal((a.Sequence, a.Kind, a.NodeId, a.Path, a.Iteration, a.Time, a.Rating, a.Unit, a.LowLimit, a.HighLimit, a.Message, a.Cancelled),
                (b.Sequence, b.Kind, b.NodeId, b.Path, b.Iteration, b.Time, b.Rating, b.Unit, b.LowLimit, b.HighLimit, b.Message, b.Cancelled));
            Assert.Equal(a.Value, b.Value);
            Assert.Equal(a.WaveformIds ?? [], b.WaveformIds ?? []);
        }

        var spannung = r.Events.Single(e => e.NodeId == "spannung");
        Assert.IsType<double>(spannung.Value);
        Assert.Equal((220.0, 240.0, "V"), (spannung.LowLimit!.Value, spannung.HighLimit!.Value, spannung.Unit));
        Assert.Equal(3, r.Events.Count(e => e.NodeId == "belastung.messung"));
        Assert.Equal("SN-4711", r.Events.Single(e => e.Kind == ResultEventKind.OperatorAnswer && e.NodeId == "kennung").Value);

        // Kurven bitgenau
        Assert.Equal(3, r.Waveforms.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(run.Waveforms[i].Data.Samples, r.Waveforms[i].Data.Samples);
            Assert.Equal(run.Waveforms[i].Data with { Samples = [] }, r.Waveforms[i].Data with { Samples = [] });
        }
    }

    [Fact]
    public async Task Dreiphasige_Kurven_haben_gemeinsame_Zeitbasis_und_Kanalzuordnung()
    {
        var (run, _) = await RecordOnce();
        var step = run.Events.Single(e => e.NodeId == "netzkurven");
        Assert.Equal(ResultRating.Pass, step.Rating);
        var waves = run.Waveforms.Where(w => w.EventSequence == step.Sequence).Select(w => w.Data).ToList();
        Assert.Equal(["Spannung_L1", "Spannung_L2", "Spannung_L3"], waves.Select(w => w.Channel));
        Assert.Single(waves.Select(w => (w.SyncGroup, w.SampleRate, w.Count, w.Start)).Distinct());
        Assert.Equal(200, waves[0].Count);                                    // 5 kHz × 40 ms
        Assert.InRange(waves[0].Rms(), 229, 231);
        Assert.InRange(waves[1].Rms(), 228.5, 230.5);
        // L2 eilt L1 um 120° nach: Nulldurchgang 6,67 ms später
        var zeroL1 = Enumerable.Range(1, 199).First(i => waves[0].Samples[i - 1] <= 0 && waves[0].Samples[i] > 0);
        var zeroL2 = Enumerable.Range(1, 199).First(i => waves[1].Samples[i - 1] <= 0 && waves[1].Samples[i] > 0);
        var diffMs = (zeroL2 - zeroL1) / 5.0;
        Assert.InRange(diffMs < 0 ? diffMs + 20 : diffMs, 6.2, 7.2);
    }

    [Fact]
    public async Task Kurvenaufnahme_ohne_Waveform_Geraet_oder_ueber_mehrere_Geraete_ergibt_ERROR()
    {
        var c = BlockCatalog.Default;
        var step = c.Create(BlockCatalog.Capture, "k", "Kurve", new Dictionary<string, string> { ["Ressourcen"] = "A, B" });
        var a = new Testumgebung.Hardware.SimulationAdapter("Gerät A");
        var b = new Testumgebung.Hardware.SimulationAdapter("Gerät B");
        a.SetSine("x", 1);
        b.SetSine("y", 1);
        var registry = new Testumgebung.Hardware.HardwareRegistry().Bind("A", a, "x").Bind("B", b, "y");
        var dut = Kit.Group("dut").Add(step);
        var run = new TestRun(Kit.Test(dut), new TestRunOptions { Clock = new VirtualClock(), Hardware = registry });
        await run.RunAsync().Within();
        var sc = run.Steps("k").Single();
        Assert.Equal(ResultRating.Error, sc.Rating);
        Assert.Contains("selben Gerät", sc.Message);
    }

    [Fact]
    public void Grosse_Kurven_werden_blockweise_geschrieben_und_vollstaendig_gelesen()
    {
        var samples = Enumerable.Range(0, 3 * ResultFile.SamplesPerChunk + 123).Select(i => Math.Sin(i * 0.001) * 325 + i * 1e-9).ToArray();
        var times = samples.Select((_, i) => i / 10000.0 + (i % 7) * 1e-7).ToArray(); // explizite Zeitpunkte
        var w = new Waveform("Strom_L1", "A", 10000, DateTimeOffset.UnixEpoch, samples, times, "gut", "sync-1");
        var run = SyntheticRun(w);
        var bytes = ResultFile.ToBytes(run);
        Assert.Equal(4, CountChunks(bytes, "WAVE"));
        var read = ResultFile.Read(bytes);
        Assert.True(read.IsIntact);
        var back = Assert.Single(read.Run.Waveforms).Data;
        Assert.Equal(samples, back.Samples);
        Assert.Equal(times, back.Times);
        Assert.Equal(("Strom_L1", "A", 10000.0, "gut", "sync-1"), (back.Channel, back.Unit, back.SampleRate, back.Quality, back.SyncGroup));
    }

    [Fact]
    public async Task Beschaedigter_Chunk_wird_erkannt_gemeldet_und_uebersprungen()
    {
        var (run, _) = await RecordOnce();
        var bytes = ResultFile.ToBytes(run);
        var wave = FindChunk(bytes, "WAVE", occurrence: 1);
        bytes[wave + ResultFile.ChunkHeaderSize + 10] ^= 0x5a;

        var read = ResultFile.Read(bytes);
        Assert.False(read.IsIntact);
        Assert.Contains(read.Problems, p => p.Contains("Prüfsumme"));
        Assert.Equal(run.DutId, read.Run.DutId);                        // Kopf und Ereignisse weiter lesbar
        Assert.Equal(run.Events.Count, read.Run.Events.Count);
        Assert.Equal(2, read.Run.Waveforms.Count);                       // beschädigte Kurve fehlt
    }

    [Fact]
    public async Task Abgeschnittene_Datei_wird_sequenziell_gelesen_fremde_und_neuere_abgelehnt()
    {
        var (run, _) = await RecordOnce();
        var bytes = ResultFile.ToBytes(run);
        var cut = bytes[..FindChunk(bytes, "INDX", 0)];
        var read = ResultFile.Read(cut);
        Assert.Contains(read.Problems, p => p.Contains("sequenziell"));
        Assert.Equal(run.Events.Count, read.Run.Events.Count);
        Assert.Equal(3, read.Run.Waveforms.Count);

        Assert.Throws<ResultFileException>(() => ResultFile.Read("PK\x03\x04 kein Ergebnis"u8.ToArray()));
        var newer = (byte[])bytes.Clone();
        newer[4] = 99;
        Assert.Contains("neuer", Assert.Throws<ResultFileException>(() => ResultFile.Read(newer)).Message);
    }

    [Fact]
    public async Task Speichern_ist_atomar_kollisionsfrei_und_nutzt_bei_Fehler_die_Wiederherstellung()
    {
        var (run, _) = await RecordOnce();
        var store = new ResultFileStore(Dir("ok"));
        var a = store.Save(run);
        var b = store.Save(run);                                        // gleiche Run-ID: nie überschreiben
        Assert.NotEqual(a.Path, b.Path);
        Assert.Equal(2, store.List().Count);
        Assert.Empty(Directory.GetFiles(Dir("ok"), ".*"));

        Directory.CreateDirectory(_dir);
        var blocker = Dir("ist-eine-datei");
        File.WriteAllText(blocker, "x");                                // Zielordner nicht anlegbar
        var broken = new ResultFileStore(blocker, Dir("wiederherstellung"));
        var result = broken.Save(run);
        Assert.False(result.Success);
        Assert.True(result.Recovered);
        Assert.Contains("nicht", result.Error);
        Assert.StartsWith(Dir("wiederherstellung"), result.Path);
        Assert.Equal(run.RunId, ResultFile.Read(result.Path!).Run.RunId);
    }

    [Fact]
    public async Task Anhaenge_werden_einmalig_eingebettet()
    {
        Directory.CreateDirectory(_dir);
        var img1 = Dir("bild1.png");
        var img2 = Dir("kopie.png");
        File.WriteAllBytes(img1, [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4]);
        File.Copy(img1, img2);
        var c = BlockCatalog.Default;
        StepDefinition Prompt(string id, string path) => c.Create(BlockCatalog.Operator, id, id,
            new Dictionary<string, string> { ["Art"] = "Bild mit Bestätigung", ["Text"] = "Stecker prüfen", ["Bild"] = path, ["Warten"] = "false" });
        var dut = new GroupDefinition("dut", "Prüfling") { IsDutRun = true }.Var("Kennung", VarType.String, "SN-9")
            .Add(Prompt("a", img1), Prompt("b", img1), Prompt("c", img2));
        var run = new TestRun(Kit.Test(dut), new TestRunOptions { Clock = new VirtualClock() });
        var recorder = new ResultRecorder(run);
        ResultRun? recorded = null;
        recorder.RunRecorded += r => recorded = r;
        await run.RunAsync().Within();

        Assert.NotNull(recorded);
        var att = Assert.Single(recorded!.Attachments);                 // gleicher Inhalt → ein Anhang
        Assert.Equal(3, recorded.Events.Count(e => e.AttachmentId == att.Id));
        var read = ResultFile.Read(ResultFile.ToBytes(recorded)).Run;
        Assert.Equal(att.Data, Assert.Single(read.Attachments).Data);
    }

    [Fact]
    public void MessagePack_Teilmenge_ist_standardkonform()
    {
        var w = new MsgPackWriter().Map(3).Key("a").Int(-1).Key("bb").Array(2).Int(300).Double(1.5).Key("c").Str(new string('x', 40));
        var bytes = w.ToArray();
        Assert.Equal(0x83, bytes[0]);                                   // fixmap(3)
        Assert.Equal(0xa1, bytes[1]);                                   // fixstr(1)
        Assert.Equal(0xff, bytes[3]);                                   // negative fixint -1
        var parsed = (Dictionary<string, object?>)MsgPackReader.Parse(bytes)!;
        Assert.Equal(-1L, parsed["a"]);
        Assert.Equal(new object?[] { 300L, 1.5 }, (object?[])parsed["bb"]!);
        Assert.Equal(40, ((string)parsed["c"]!).Length);
        Assert.Equal(0x78B31ED5u, Crc32.Compute("Hallo"u8));                  // wie zlib.crc32
    }

    // ------------------------------------------------------------------ Filter (P01)

    [Fact]
    public async Task P01_Anzeige_aus_Datei_an_DB_aus_nur_gewaehlte_Ziele_enthalten_Daten()
    {
        var project = SampleProjects.AcPruefung();
        var spannung = project.Definition.Find("spannung")!;
        spannung.Storage = new StorageSettings(Display: false, File: true, Database: false);
        var (run, def) = await RecordOnce(project);
        var resolver = new StorageResolver(def);

        var file = ResultFilter.ForFile(run, resolver);
        var db = ResultFilter.ForDatabase(run, resolver);
        Assert.Contains(file.Events, e => e.NodeId == "spannung");
        Assert.DoesNotContain(db.Events, e => e.NodeId == "spannung");
        Assert.Contains(db.Events, e => e.NodeId == "belastung.messung");   // übrige Daten unverändert
        // Ablauf und Bewertung unverändert, technische Mindestmenge erhalten
        Assert.Equal(run.Rating, db.Rating);
        Assert.Equal(run.Structure, db.Structure);
        Assert.Contains(db.Structure, n => n.Id == "spannung");
        Assert.Equal((ResultFilter.FileTarget, ResultFilter.DatabaseTarget), (file.FilteredFor, db.FilteredFor));

        // Gefilterte Inhalte stehen auch nicht als Rohdaten in der Datei
        var dbBytes = ResultFile.ToBytes(db);
        var value = (double)run.Events.Single(e => e.NodeId == "spannung").Value!;
        Assert.False(ContainsDouble(ResultFile.Read(dbBytes).Run, value));

        // Anzeige: Prüfplatz zeigt den Schritt nicht, Gesamtbewertung bleibt PASS
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, clock: new VirtualClock(),
            results: new ResultPipeline(new ResultFileStore(Dir("ergebnisse")), null));
        var s = shell.SelectedStation;
        _ = s.StartAsync();
        await CompleteDut(ui, s, "SN-77");
        await ui.Until(() => s.StorageReports.Count == 1, "Ergebnisdatei");
        Assert.DoesNotContain(s.Protocol, p => p.Text == "Messung Spannung");
        Assert.Contains(s.Protocol, p => p.Text == "Messung unter Last");
        Assert.Contains(s.Protocol, p => p.Text.Contains("SN-77 abgeschlossen") && p.Rating == ResultRating.Pass);
        Assert.Null(s.StorageReports[0].Database);                       // keine DB konfiguriert
        var saved = ResultFile.Read(s.LastResultFile!).Run;
        Assert.Contains(saved.Events, e => e.NodeId == "spannung");
        s.RequestAbort();
        await ui.Until(() => !s.IsRunning, "Ende");
    }

    [Fact]
    public async Task Gruppendefaults_vererben_Overrides_gewinnen_Kurven_separat_waehlbar()
    {
        var project = SampleProjects.AcPruefung();
        var d = project.Definition;
        d.Find("belastung")!.Storage = new StorageSettings(File: false);
        d.Find("belastung.messung")!.Storage = new StorageSettings(File: true);
        d.Find("netzkurven")!.Storage = new StorageSettings(FileWaveforms: false, Database: false);
        var (run, _) = await RecordOnce(project);
        var resolver = new StorageResolver(d);

        Assert.Equal((false, "Messgruppe – Belastung"), resolver.Explain("belastung.warten", StorageTarget.File));
        Assert.Equal((true, null), resolver.Explain("belastung.messung", StorageTarget.File));
        Assert.Equal((true, "Standard"), resolver.Explain("spannung", StorageTarget.File));

        var file = ResultFilter.ForFile(run, resolver);
        Assert.Equal(["belastung.messung"], file.Events.Where(e => e.NodeId?.StartsWith("belastung") == true).Select(e => e.NodeId).Distinct());
        var kurve = file.Events.Single(e => e.NodeId == "netzkurven");
        Assert.Empty(kurve.WaveformIds!);                               // Wert/Bewertung ja, Kurven nein
        Assert.Empty(file.Waveforms);

        var db = ResultFilter.ForDatabase(run, resolver);
        Assert.DoesNotContain(db.Events, e => e.NodeId == "netzkurven"); // Werte nicht in DB …
        Assert.Equal(3, db.Waveforms.Count);                             // … Kurven aber schon (separat gewählt)

        // Projektdatei erhält die Einstellungen
        var serializer = new ProjectSerializer();
        var loaded = serializer.Deserialize(serializer.Serialize(project));
        Assert.Equal(new StorageSettings(FileWaveforms: false, Database: false), loaded.Definition.Find("netzkurven")!.Storage);
        Assert.Equal(StorageSettings.Inherit, loaded.Definition.Find("spannung")!.Storage);
    }

    [Fact]
    public void Editor_zeigt_Filter_mit_Herkunft_und_speichert_Aenderungen()
    {
        var shell = new ShellViewModel(new QueueDispatcher(), project: SampleProjects.AcPruefung(), clock: new VirtualClock());
        shell.Login();
        var e = shell.Editor;
        e.SelectedNode = e.FindNode("belastung");
        var group = ((GroupPropertiesViewModel)e.Properties!).Storage;
        Assert.True(group.IsGroup);
        group.Rows.Single(r => r.Target == StorageTarget.Database).Selected = StorageRowViewModel.OffText;

        e.SelectedNode = e.FindNode("belastung.messung");
        var step = ((StepPropertiesViewModel)e.Properties!).Storage;
        var dbRow = step.Rows.Single(r => r.Target == StorageTarget.Database);
        Assert.Equal(StorageRowViewModel.InheritText, dbRow.Selected);
        Assert.Equal("→ aus (von Messgruppe – Belastung)", dbRow.EffectiveText);
        dbRow.Selected = StorageRowViewModel.OnText;
        Assert.Equal("→ an", dbRow.EffectiveText);
        Assert.True(step.ResetCommand.CanExecute(null));
        Assert.Contains("Mindestmenge", step.MinimalSet);
        Assert.Equal(new StorageSettings(Database: true), e.Project.Definition.Find("belastung.messung")!.Storage);
    }

    // ------------------------------------------------------------------ Outbox (P02–P05)

    [Fact]
    public async Task P02_DB_Ausfall_am_Testende_Outbox_dauerhaft_10s_Warnung_und_Fehlermeldung()
    {
        var db = new InMemoryRunDatabase { Offline = true };
        var notifier = new CollectingNotifier();
        var outbox = new OutboxService(Dir("outbox"), db, notifier: notifier);
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: SampleProjects.AcPruefung(), clock: new VirtualClock(),
            results: new ResultPipeline(new ResultFileStore(Dir("ergebnisse")), outbox));
        var s = shell.SelectedStation;
        var hide = new TaskCompletionSource();
        TimeSpan? requested = null;
        s.WarningDelay = d => { requested = d; return hide.Task; };

        _ = s.StartAsync();
        await CompleteDut(ui, s, "SN-500");
        await ui.Until(() => s.StorageWarnings.Count == 1, "Werkerwarnung");
        var warning = s.StorageWarnings[0];
        Assert.Equal("SN-500", warning.DutId);
        Assert.Contains("Outbox", warning.Text);
        Assert.Equal(TimeSpan.FromSeconds(10), requested);
        Assert.True(s.StorageReports[0].File!.Success);                  // Datei unabhängig gespeichert
        Assert.Contains(s.Protocol, p => p.Text.Contains("SN-500 abgeschlossen") && p.Rating == ResultRating.Pass); // Übertragungsstatus ≠ Bewertung

        // Dauerhaft auf dem Datenträger
        var entry = Assert.Single(outbox.Pending);
        Assert.Equal((1, "SN-500"), (entry.Attempts, entry.DutId));
        Assert.Single(Directory.GetFiles(Dir("outbox"), "*.tres"));
        Assert.Equal(entry.RunId, ResultFile.Read(Directory.GetFiles(Dir("outbox"), "*.tres")[0]).Run.RunId);
        // Externe Fehlermeldung je Run
        var n = Assert.Single(notifier.All);
        Assert.Equal((RunNotificationKind.StoreFailed, entry.RunId), (n.Kind, n.RunId));

        hide.SetResult();                                                 // nach 10 s ausgeblendet
        await ui.Until(() => s.StorageWarnings.Count == 0, "Warnung ausgeblendet");
        s.RequestAbort();
        await ui.Until(() => !s.IsRunning, "Ende");
    }

    [Fact]
    public async Task P03_Hintergrund_Retry_scheitert_ohne_Werkerwarnung_mit_Meldung_je_Versuch()
    {
        var db = new InMemoryRunDatabase { Offline = true };
        var notifier = new CollectingNotifier();
        var clock = new VirtualClock();
        var outbox = new OutboxService(Dir("outbox"), db, clock, new OutboxOptions { RetryInterval = TimeSpan.FromSeconds(30) }, notifier);
        var (run, _) = await RecordOnce();
        var warnings = 0;
        outbox.Notified += x => { if (x.Kind == RunNotificationKind.StoreFailed) warnings++; };

        Assert.False((await outbox.SubmitAsync(run)).Stored);
        await outbox.RetryDueAsync();                                     // noch nicht fällig
        Assert.Equal(1, db.StoreCalls);

        clock.Advance(TimeSpan.FromSeconds(31));
        await outbox.RetryDueAsync();
        clock.Advance(TimeSpan.FromSeconds(61));                          // Backoff: 30 s, 60 s, …
        await outbox.RetryDueAsync();

        Assert.Equal(1, warnings);                                        // Werkerwarnung nur beim initialen Versuch
        Assert.Equal([RunNotificationKind.StoreFailed, RunNotificationKind.RetryFailed, RunNotificationKind.RetryFailed], notifier.All.Select(x => x.Kind));
        Assert.Equal([1, 2, 3], notifier.All.Select(x => x.Attempt));
        Assert.All(notifier.All, x => Assert.Equal(run.RunId, x.RunId));
        Assert.Equal(3, outbox.Pending.Single().Attempts);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(120), outbox.Pending.Single().NextAttempt);
    }

    [Fact]
    public async Task P04_Neustart_mit_offenen_Runs_naechstes_Oeffnen_setzt_Uebertragung_fort()
    {
        var folder = Dir("outbox");
        var (run1, _) = await RecordOnce(serial: "SN-1");
        var (run2, _) = await RecordOnce(serial: "SN-2");
        var offline = new InMemoryRunDatabase { Offline = true };
        await using (var before = new OutboxService(folder, offline))
        {
            Assert.False((await before.SubmitAsync(run1)).Stored);
            Assert.False((await before.SubmitAsync(run2)).Stored);
        } // „Rechner aus“: nichts wartet auf die Übertragung

        var db = new InMemoryRunDatabase();
        var notifier = new CollectingNotifier();
        var after = new OutboxService(folder, db, options: new OutboxOptions { RetryInterval = TimeSpan.FromMilliseconds(50) }, notifier: notifier);
        Assert.Empty(after.Pending);                                       // erst beim Öffnen geladen
        after.Start();                                                    // manuelles Öffnen der Anwendung
        await WaitUntil(() => db.Runs.Count == 2);
        await WaitUntil(() => after.Pending.Count == 0);
        Assert.Equal(["SN-1", "SN-2"], db.Runs.Select(r => r.DutId).Order().ToList());
        Assert.All(notifier.All, n => Assert.Equal(RunNotificationKind.StoredLater, n.Kind));
        Assert.Empty(Directory.GetFiles(folder, "*.tres"));
        Assert.Empty(Directory.GetFiles(folder, "*.json"));
        await after.StopAsync();
    }

    [Fact]
    public async Task P05_Commit_erfolgreich_Antwort_verloren_Wiederholung_ohne_Duplikat()
    {
        var db = new InMemoryRunDatabase { LoseAnswers = 1 };
        var notifier = new CollectingNotifier();
        var outbox = new OutboxService(Dir("outbox"), db, notifier: notifier);
        var (run, _) = await RecordOnce();

        var first = await outbox.SubmitAsync(run);
        Assert.False(first.Stored);                                      // Antwort unklar → als Fehler behandelt
        Assert.Contains("verloren", first.Error);
        await outbox.RetryNowAsync();

        Assert.Single(db.Runs);                                          // Unique-Key Run-ID: kein Duplikat
        Assert.Equal(2, db.StoreCalls);
        Assert.Empty(outbox.Pending);
        Assert.Equal([RunNotificationKind.StoreFailed, RunNotificationKind.StoredLater], notifier.All.Select(n => n.Kind));
    }

    [Fact]
    public async Task Outbox_enthaelt_genau_die_DB_Auswahl_und_defekte_Eintraege_werden_aussortiert()
    {
        var project = SampleProjects.AcPruefung();
        project.Definition.Find("spannung")!.Storage = new StorageSettings(File: false);   // nur DB
        project.Definition.Find("sicht.bewertung")!.Storage = new StorageSettings(Database: false);
        var (run, def) = await RecordOnce(project);
        var db = new InMemoryRunDatabase { Offline = true };
        var outbox = new OutboxService(Dir("outbox"), db);
        var pipeline = new ResultPipeline(new ResultFileStore(Dir("ergebnisse")), outbox);
        var report = await pipeline.ProcessAsync(run, new StorageResolver(def));
        Assert.True(report.DatabaseFailed);

        var queued = ResultFile.Read(Directory.GetFiles(Dir("outbox"), "*.tres")[0]).Run;
        Assert.Contains(queued.Events, e => e.NodeId == "spannung");      // nicht in Datei, aber in DB-Auswahl
        Assert.DoesNotContain(queued.Events, e => e.NodeId == "sicht.bewertung");
        var file = ResultFile.Read(report.File!.Path!).Run;
        Assert.DoesNotContain(file.Events, e => e.NodeId == "spannung");

        // Defekter Eintrag nach „Neustart“
        File.WriteAllBytes(Path.Combine(Dir("outbox"), "kaputt.tres"), [1, 2, 3]);
        var notifier = new CollectingNotifier();
        var reopened = new OutboxService(Dir("outbox"), new InMemoryRunDatabase(), notifier: notifier);
        reopened.EnsureLoaded();
        Assert.Single(reopened.Pending);
        Assert.Contains(notifier.All, n => n.Kind == RunNotificationKind.OutboxDamaged);
        Assert.True(File.Exists(Path.Combine(Dir("outbox"), "defekt", "kaputt.tres")));
    }

    [Fact]
    public async Task Ergebnisseite_listet_Dateien_zeigt_Inhalt_Kurven_und_Outbox()
    {
        var (run, _) = await RecordOnce(serial: "SN-321");
        var store = new ResultFileStore(Dir("ergebnisse"));
        store.Save(run);
        var db = new InMemoryRunDatabase { Offline = true };
        var outbox = new OutboxService(Dir("outbox"), db);
        await outbox.SubmitAsync(run);
        var ui = new QueueDispatcher();
        var vm = new ResultsViewModel(new ResultPipeline(store, outbox), ui, null);
        ui.Pump();

        var info = Assert.Single(vm.ResultFiles);
        vm.SelectedFile = info;
        var doc = vm.Document!;
        Assert.True(doc.IsIntact);
        Assert.Contains(doc.Header, h => h.Label == "Prüfling" && h.Value == "SN-321");
        Assert.Contains(doc.Events, e => e.Path.EndsWith("Messung Spannung") && e.Kind == "Messung" && e.Unit == "V");
        var group = Assert.Single(doc.WaveformGroups);
        Assert.Equal(group, doc.SelectedGroup);
        Assert.Equal(3, doc.Plot.Count);
        Assert.All(doc.Plot.SelectMany(p => p.Points), p =>
        {
            Assert.InRange(p.X, 0, ResultDocumentViewModel.PlotWidth);
            Assert.InRange(p.Y, 0, ResultDocumentViewModel.PlotHeight);
        });

        Assert.Single(vm.Pending);
        Assert.True(vm.CanSimulateOutage);
        Assert.True(vm.DatabaseOffline);
        vm.DatabaseOffline = false;                                       // DB wieder da → sofort nachreichen
        await WaitUntil(() => db.Runs.Count == 1);
        await WaitUntil(() => { ui.Pump(); return vm.Pending.Count == 0; });
        Assert.Contains(vm.Notifications, n => n.Kind == RunNotificationKind.StoredLater);
    }

    // ------------------------------------------------------------------ Hilfen

    private sealed class CollectingNotifier : IRunNotifier
    {
        private readonly List<RunNotification> _all = [];

        public IReadOnlyList<RunNotification> All
        {
            get
            {
                lock (_all) return _all.ToList();
            }
        }

        public void Notify(RunNotification notification)
        {
            lock (_all) _all.Add(notification);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Kit.Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Bedingung nicht erreicht.");
            await Task.Delay(10);
        }
    }

    private static async Task CompleteDut(QueueDispatcher ui, StationViewModel s, string serial)
    {
        async Task Answer(string step, Action<OperatorContentViewModel> act)
        {
            await ui.Until(() => s.ActiveContent is { IsAwaiting: true } c && c.Title == step, step);
            act(s.ActiveContent!);
            ui.Pump();
        }
        await Answer("Kennung erfassen", c => { c.Input = serial; c.Confirm(); });
        await Answer("Sichtprüfung", c => c.ChooseCommand.Execute("i. O."));
        await Answer("Gehäusetemperatur erfassen", c => { c.Input = "22"; c.Confirm(); });
        await Answer("Prüfling anschließen", c => c.ConfirmCommand.Execute(null));
    }

    private static ResultRun SyntheticRun(params Waveform[] waves) => new()
    {
        RunId = "PT-synthetisch",
        DutId = "SN-X",
        Started = DateTimeOffset.UnixEpoch,
        Ended = DateTimeOffset.UnixEpoch.AddSeconds(1),
        Rating = ResultRating.Pass,
        Structure = [new ResultNode("k", null, "Kurve", "schritt", BlockCatalog.Capture, null)],
        Events = [new ResultEvent(1, ResultEventKind.Step, "k", "Kurve", 1, DateTimeOffset.UnixEpoch, WaveformIds: waves.Select(w => "k/" + w.Channel).ToList())],
        Waveforms = waves.Select(w => new ResultWaveform("k/" + w.Channel, 1, "k", w)).ToList(),
    };

    private static int CountChunks(byte[] bytes, string type)
    {
        var n = 0;
        for (var i = 0; FindChunkFrom(bytes, type, ref i); i++) n++;
        return n;
    }

    private static int FindChunk(byte[] bytes, string type, int occurrence)
    {
        var i = 0;
        for (var k = 0; ; k++, i++)
        {
            if (!FindChunkFrom(bytes, type, ref i)) throw new InvalidOperationException($"Chunk {type} nicht gefunden.");
            if (k == occurrence) return i;
        }
    }

    /// <summary>Chunks entlang der Kette ablaufen (nicht per Bytesuche – Daten könnten den Typ enthalten).</summary>
    private static bool FindChunkFrom(byte[] bytes, string type, ref int start)
    {
        var pos = 8;
        while (pos + ResultFile.ChunkHeaderSize <= bytes.Length)
        {
            var t = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            if (t == "TEND") return false;
            var len = BitConverter.ToInt32(bytes, pos + 5);
            if (pos >= start && t == type)
            {
                start = pos;
                return true;
            }
            pos += ResultFile.ChunkHeaderSize + len;
            if (t == "INDX") return false;
        }
        return false;
    }

    private static bool ContainsDouble(ResultRun run, double value)
        => run.Events.Any(e => e.Value is double d && d == value) || run.Events.Any(e => e.LowLimit == value);
}
