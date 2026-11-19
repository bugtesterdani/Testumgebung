using System.Buffers.Binary;
using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class TraceRawStorageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static TraceEvaluation Record(bool slope = false)
    {
        var recordings = new EnvelopeRecordings();
        var curve = new CurveDefinition { Id = "band", Version = "7", Name = "Band", Times = [0, 1, 2],
            Channels = [new("Untergrenze", "V", [0, 0, 0]), new("Obergrenze", "V", [10, 10, 10])] };
        recordings.Start("owner", "trace", [new("U", "V", slope ? null : curve), new("I", "A", slope ? null :
            new CurveDefinition { Id = "current", Version = "1", Name = "Current", Times = [0, 1, 2], Channels = [new("Untergrenze", "A", [0, 0, 0]), new("Obergrenze", "A", [10, 10, 10])] })],
            Now, maxGap: 3, slope: slope ? new(SlopeWindowMode.Consecutive, 2, -10, 10) : null);
        recordings.Add("owner", "trace", 0, [2, 1], Now);
        recordings.Add("owner", "trace", 2, [6, 3], Now);
        return recordings.Stop("owner", "trace");
    }
    private static ResultRun Run(TraceEvaluation trace)
    {
        var run = new ResultRun { RunId = "raw", Started = Now, Ended = Now.AddSeconds(2), Rating = ResultRating.Pass };
        for (var i = 0; i < trace.Data.Channels.Count; i++)
            run.Waveforms.Add(new("wave" + i, 1, "trace", trace.Data.Channels[i]) { Recording = i == 0 ? trace.Data.Recording : null });
        return run;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Storage_keeps_actual_samples_and_one_axis_and_reconstructs_frozen_assessment(bool slope)
    {
        var trace = Record(slope); var run = Run(trace);
        var stored = TraceResultStorage.Collapse(run.Waveforms);
        Assert.Equal(2, stored.Count);
        Assert.Single(stored, wave => wave.Data.TimeValues is not null);
        Assert.Equal(new double[] { 0, 2 }, stored[0].Data.Times);
        Assert.Equal(new double[] { 2, 6 }, stored[0].Data.Samples);
        Assert.StartsWith(TraceResultStorage.Prefix, stored[0].Data.Quality);
        Assert.Null(stored[1].Data.Quality);
        Assert.Null(TraceResultStorage.QualityForPart(stored[0].Data, 1));
        var problems = new List<string>();
        var restored = TraceResultStorage.Expand(stored, problems);
        Assert.Empty(problems);
        Assert.All(restored, wave => Assert.Null(wave.Data.DisplayWarning));
        var main = restored.Single(wave => wave.Data.Channel == "U");
        Assert.Equal(EnvelopeTraceMetadata.Decode(trace.Data.Channels[0].Quality)!.Details,
            EnvelopeTraceMetadata.Decode(main.Data.Quality)!.Details);
        if (slope) Assert.Contains(restored, wave => wave.Data.Channel == "U · Messwert" && wave.Data.Count == 2);
        Assert.Equal(2, TraceResultStorage.Collapse(restored).Count);
    }
    [Fact]
    public void File_version_two_roundtrips_and_version_one_legacy_arrays_remain_readable()
    {
        using var stream = new MemoryStream();
        ResultFile.Write(stream, Run(Record()));
        var bytes = stream.ToArray();
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        var file = ResultFile.Read(bytes);
        Assert.Empty(file.Problems);
        Assert.Contains(file.Run.Waveforms, wave => wave.Data.Channel == "U");
        var legacy = new ResultRun { RunId = "legacy", Waveforms = [new("one", 1, null, new("U", "V", 1, Now, new double[] { 3, 4 }))] };
        using var old = new MemoryStream(); ResultFile.Write(old, legacy);
        var oldBytes = old.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(oldBytes.AsSpan(4), 1);
        Assert.Equal(new double[] { 3, 4 }, Assert.Single(ResultFile.Read(oldBytes).Run.Waveforms).Data.Samples);
    }
    [Theory]
    [InlineData("safety-first")]
    [InlineData("safety-later")]
    [InlineData("abort")]
    [InlineData("target")]
    [InlineData("formula")]
    public void Frozen_replay_preserves_terminal_conditions_without_false_warning(string mode)
    {
        var band = new CurveDefinition { Id = "band", Name = "Band", Version = "1", Times = [0, 1, 3],
            Channels = [new("Untergrenze", "V", [0, 0, 0]), new("Obergrenze", "V", [10, 10, 10])] };
        var abort = new CurveDefinition { Id = "abort", Name = "Abort", Version = "1", Times = [0, 1, 3],
            Channels = [new("Untergrenze", "V", [0, 0, 0]), new("Obergrenze", "V", [8, 8, 8])] };
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace",
            [new("U", "V", mode == "formula" ? null : band) {
                SafetyMaximum = mode.StartsWith("safety") ? 10 : null, AbortLimits = mode == "abort" ? abort : null }],
            Now, maxGap: 2, duration: mode == "target" ? 3 : null,
            formula: mode == "formula" ? new FormulaLimits("0", "10") : null,
            target: mode == "target" ? new TraceTargetBand(4, 6, 2) : null);
        recordings.Add("owner", "trace", 0, [mode == "safety-first" ? 12 : 5], Now);
        if (!recordings.IsEnded("owner", "trace"))
            recordings.Add("owner", "trace", 1, [mode == "safety-later" ? 12 : mode == "abort" ? 9 : 5], Now);
        var trace = recordings.Stop("owner", "trace");
        var live = new WaveformPlotViewModel(); live.SetData(trace.Data);
        Assert.Equal(live.Channels.Count, live.Channels.Select(channel => channel.Id).Distinct().Count());
        var source = trace.Data.Recording!;
        var replay = TraceRecordingSnapshot.Parse(source.Snapshot).Reconstruct(source.RawChannels);
        Assert.All(replay.Channels, wave => Assert.Null(wave.DisplayWarning));
        Assert.Contains(replay.Channels, wave => wave.Channel == "U");
        var metadata = EnvelopeTraceMetadata.Decode(replay.Channels.First(wave => wave.Channel == "U").Quality)!;
        Assert.Equal(trace.Channels[0].Complete, metadata.Complete);
        Assert.Equal(trace.Channels[0].EarlyStop, metadata.EarlyStop);
    }
    [Fact]
    public void Corrupt_snapshot_keeps_raw_values_with_shared_times_and_a_warning()
    {
        var stored = TraceResultStorage.Collapse(Run(Record(true)).Waveforms).ToArray();
        stored[0] = stored[0] with { Data = stored[0].Data with { Quality = TraceResultStorage.Prefix + "invalid" } };
        var problems = new List<string>(); var restored = TraceResultStorage.Expand(stored, problems);
        Assert.Single(problems);
        Assert.All(restored, wave => { Assert.NotNull(wave.Data.DisplayWarning); Assert.Equal(new double[] { 0, 2 }, wave.Data.Times); });
        Assert.Throws<FormatException>(() => TraceRecordingSnapshot.Parse("{}"));
    }
    [SqliteFact]
    public async Task Database_stores_raw_channels_and_shared_axis()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "raw.db")}";
            var db = new Testumgebung.DatabaseAdapters.SqlRunDatabase(NativeSqliteFactory.Instance, connectionString,
                new Testumgebung.DatabaseAdapters.SqliteDialect());
            await db.StoreAsync(Run(Record(true)), default);
            using var connection = NativeSqliteFactory.Instance.CreateConnection()!;
            connection.ConnectionString = connectionString; await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*),count(times),count(quality),sum(length(samples)) FROM tu_waveform_chunks";
            using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt64(0)); Assert.Equal(1, reader.GetInt64(1));
            Assert.Equal(1, reader.GetInt64(2)); Assert.Equal(32, reader.GetInt64(3));
            var restored = (await db.LoadRunAsync("raw", default))!;
            Assert.Contains(restored.Waveforms, wave => wave.Data.Unit == "V/s");
            Assert.Contains(restored.Waveforms, wave => wave.Data.Channel == "U · Messwert");
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void Large_recording_reports_physical_file_sizes()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now, maxPoints: 10000, maxGap: 1,
            slope: new(SlopeWindowMode.Consecutive, 2, -100, 100));
        for (var i = 0; i < 10000; i++) recordings.Add("owner", "trace", i * .01, [5 + Math.Sin(i * .013)], Now);
        var trace = recordings.Stop("owner", "trace");
        var run = Run(trace);
        var rawBytes = ResultFile.ToBytes(run);
        var legacy = run.CopyWith("", run.Events, run.Waveforms.Select(wave => wave with { Recording = null }).ToList(), run.Attachments);
        var legacyBytes = ResultFile.ToBytes(legacy);
        Console.WriteLine($"RAW_SIZE raw={rawBytes.Length}; derived={legacyBytes.Length}");
        Assert.True(TraceResultStorage.Collapse(run.Waveforms).Sum(wave => wave.Data.Count) <
            legacy.Waveforms.Sum(wave => wave.Data.Count));
        Assert.Empty(ResultFile.Read(rawBytes).Problems);
    }
    [Fact]
    public void Algorithm_change_warns_without_replacing_saved_assessment()
    {
        var trace = Record();
        var source = trace.Data.Recording!;
        var snapshot = TraceRecordingSnapshot.Parse(source.Snapshot);
        var restored = (snapshot with { AlgorithmVersion = 999 }).Reconstruct(source.RawChannels);
        Assert.Contains(restored.Channels, wave => wave.DisplayWarning is not null);
        Assert.Equal(snapshot.Assessments[0].Details, EnvelopeTraceMetadata.Decode(restored.Channels[0].Quality)!.Details);
        Assert.Equal("band@7", snapshot.Channels[0].Limits!.Key);
        Assert.NotEmpty(snapshot.Channels[0].Limits!.Hash);
    }
    [Fact]
    public void Saved_exclusion_audit_survives_raw_replay_and_shared_plot_filters()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now,
            slope: new(SlopeWindowMode.Consecutive, 2, -5, 5)
            { InitialExceptionCount = 1, InitialExceptionAbove = 20 });
        recordings.Add("owner", "trace", 0, [0], Now);
        recordings.Add("owner", "trace", 1, [100], Now);
        recordings.Add("owner", "trace", 2, [101], Now);
        var trace = recordings.Stop("owner", "trace");
        var stored = ResultFile.Read(ResultFile.ToBytes(Run(trace)));
        Assert.Empty(stored.Problems);
        var document = new ResultDocumentViewModel(stored.Run);
        var audit = Assert.Single(document.SuppressedSlopes);
        Assert.Contains("Anlaufausnahme", audit.Description);
        var plot = document.WaveformPlot;
        var slopes = Assert.Single(plot.Panes, pane => pane.PaneTitle == "V/s");
        var values = Assert.Single(plot.Panes, pane => pane.PaneTitle == "V");
        Assert.Empty(values.SuppressionMarkers);
        Assert.Equal(audit.Description, Assert.Single(slopes.SuppressionMarkers).Description);
        plot.ShowSuppressed = false;
        Assert.Empty(slopes.SuppressionMarkers);
        Assert.Single(document.SuppressedSlopes);
        plot.ShowSuppressed = true;
        Assert.Single(slopes.SuppressionMarkers);
        plot.ShowSlopes = false;
        Assert.Empty(slopes.SuppressionMarkers);
        plot.ShowSlopes = true;
        Assert.Single(slopes.SuppressionMarkers);
        slopes.Channels.Single(channel => channel.Id == "U").IsVisible = false;
        Assert.Empty(slopes.SuppressionMarkers);
        Assert.Single(document.SuppressedSlopes);
    }

    [Fact]
    public void Unit_panes_share_cursor_and_zoom_but_keep_independent_units_and_filters()
    {
        var plot = new WaveformPlotViewModel(); plot.SetData(Record(true).Data);
        Assert.Equal(4, plot.Panes.Count);
        Assert.Contains(plot.Panes, pane => pane.PaneTitle == "V");
        Assert.Contains(plot.Panes, pane => pane.PaneTitle == "V/s");
        plot.Panes[0].CursorTime = 1;
        Assert.All(plot.Panes, pane => Assert.Equal(1, pane.CursorTime));
        plot.Panes[1].SetView(0.5, 1.5);
        Assert.All(plot.Panes, pane => { Assert.Equal(plot.ViewStart, pane.ViewStart); Assert.Equal(plot.ViewEnd, pane.ViewEnd); });
        plot.ShowSlopes = false;
        Assert.All(plot.Panes.Where(pane => pane.PaneTitle.EndsWith("/s")), pane => Assert.Empty(pane.Series));
        plot.ShowSlopes = true;
        plot.ShowLimits = false;
        Assert.All(plot.Panes.Where(pane => pane.PaneTitle.EndsWith("/s")), pane => Assert.Single(pane.Series));
        plot.Clear(); Assert.Empty(plot.Panes); Assert.False(plot.ShowContentFilters);
    }
}
