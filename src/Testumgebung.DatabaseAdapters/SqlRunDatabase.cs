/**
 * @file
 * @brief Schreibt einen Prüflingslauf transaktional; Run-ID, Messwerte und Kurven gehören zu derselben Speicherung.
 */

using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;

namespace Testumgebung.DatabaseAdapters;

/// <summary>Zeilenzahlen eines Runs je Tabelle (Vertragsprüfung, Diagnose).</summary>
public sealed record StoredRunCounts(int Runs, int Steps, int Measurements, int WaveformChunks, int OperatorInputs, int DevelopmentEvents);

/// <summary>
/// Datenbankadapter über ADO.NET für alle vier Provider. Vertrag (siehe <see cref="IRunDatabase"/>):
/// eine Transaktion je Run, Run-ID als Primärschlüssel, idempotent – ein bereits gespeicherter Run gilt als Erfolg.
/// Das Schema wird beim ersten Zugriff migriert (<c>tu_schema_version</c>).
/// </summary>
public sealed class SqlRunDatabase : IRunDatabase, IRunQuerySource
{
    public const int SamplesPerChunk = 65536;

    private readonly DbProviderFactory _factory;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _migrationGate = new(1, 1);
    private volatile bool _migrated;

    public SqlRunDatabase(DbProviderFactory factory, string connectionString, SqlDialect dialect)
    {
        _factory = factory;
        _connectionString = connectionString;
        Dialect = dialect;
    }

    public SqlDialect Dialect { get; }

    public string Name => $"{Dialect.DisplayName} ({DescribeTarget(_connectionString)})";

    /// <summary>Wird nach erfolgreichem Commit vor der Rückkehr aufgerufen (Tests: „Antwort nach Commit verloren“).</summary>
    public Action<string>? AfterCommit { get; set; }

    public async Task StoreAsync(ResultRun run, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

        // Die Existenzprüfung gehört zur selben Transaktion wie der vollständige Lauf.
        // So bleibt eine erneute Übertragung nach verlorener Commit-Antwort idempotent.
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await ExistsAsync(connection, tx, run.RunId, cancellationToken).ConfigureAwait(false))
        {
            // Idempotent: bereits gespeichert (z. B. Commit erfolgt, Antwort verloren) → Erfolg ohne Duplikat.
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var writer = new Writer(connection, tx, cancellationToken);
        await StoreRunHeaderAsync(writer, run).ConfigureAwait(false);

        await StoreEventsAsync(writer, run).ConfigureAwait(false);

        await StoreWaveformsAsync(writer, run).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        AfterCommit?.Invoke(run.RunId);
    }

    private static async Task StoreRunHeaderAsync(Writer writer, ResultRun run)
    {
        var definitionId = DefinitionId(run);
        if (await writer.ScalarAsync("SELECT COUNT(*) FROM tu_definitions WHERE definition_id = @id", ("id", definitionId)).ConfigureAwait(false) == 0)
        {
            await writer.ExecAsync(
                "INSERT INTO tu_definitions (definition_id, test_name, test_version, structure_json, created_utc) VALUES (@id, @name, @version, @structure, @created)",
                ("id", definitionId), ("name", run.TestName), ("version", run.TestVersion), ("structure", StructureJson(run)),
                ("created", DateTimeOffset.UtcNow.UtcTicks)).ConfigureAwait(false);
        }

        await writer.ExecAsync(
            """
            INSERT INTO tu_runs (run_id, station_run_id, station, dut_id, definition_id, test_name, test_version, started_utc, started_text,
                ended_utc, ended_text, rating, werror, development_run, application, stored_utc, order_id)
            VALUES (@run, @stationRun, @station, @dut, @def, @name, @version, @started, @startedText,
                @ended, @endedText, @rating, @werror, @dev, @app, @stored, @order)
            """,
            ("run", run.RunId), ("stationRun", run.StationRunId), ("station", run.Station), ("dut", run.DutId), ("order", run.Order), ("def", definitionId),
            ("name", run.TestName), ("version", run.TestVersion), ("started", run.Started.UtcTicks), ("startedText", Iso(run.Started)),
            ("ended", run.Ended.UtcTicks), ("endedText", Iso(run.Ended)), ("rating", RatingText(run.Rating)), ("werror", run.OperatorAbort ? 1L : 0L),
            ("dev", run.IsDevelopmentRun ? 1L : 0L), ("app", run.Application), ("stored", DateTimeOffset.UtcNow.UtcTicks)).ConfigureAwait(false);
    }

    private static async Task StoreEventsAsync(Writer writer, ResultRun run)
    {
        var prompts = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var resultEvent in run.Events)
        {
            switch (resultEvent.Kind)
            {
                case ResultEventKind.OperatorShown:
                    if (resultEvent.NodeId is not null) prompts[resultEvent.NodeId] = resultEvent.Message;
                    break;
                case ResultEventKind.OperatorAnswer:
                    var (type, _, _, text) = Typed(resultEvent.Value);
                    await writer.ExecAsync(
                        "INSERT INTO tu_operator_inputs (run_id, seq, node_id, prompt, value_type, value_text, time_utc) VALUES (@run, @seq, @node, @prompt, @type, @text, @time)",
                        ("run", run.RunId), ("seq", resultEvent.Sequence), ("node", resultEvent.NodeId), ("prompt", resultEvent.NodeId is null ? null : prompts.GetValueOrDefault(resultEvent.NodeId)),
                        ("type", type), ("text", text), ("time", resultEvent.Time.UtcTicks)).ConfigureAwait(false);
                    continue;
                case ResultEventKind.Development:
                    await writer.ExecAsync(
                        "INSERT INTO tu_development_events (run_id, seq, user_name, details, time_utc) VALUES (@run, @seq, @user, @details, @time)",
                        ("run", run.RunId), ("seq", resultEvent.Sequence), ("user", resultEvent.Value as string), ("details", resultEvent.Message), ("time", resultEvent.Time.UtcTicks)).ConfigureAwait(false);
                    continue;
            }

            await writer.ExecAsync(
                """
                INSERT INTO tu_step_executions (run_id, seq, kind, node_id, path, iteration, time_utc, time_text, rating, message, cancelled)
                VALUES (@run, @seq, @kind, @node, @path, @iter, @time, @timeText, @rating, @message, @cancelled)
                """,
                ("run", run.RunId), ("seq", resultEvent.Sequence), ("kind", KindText(resultEvent.Kind)), ("node", resultEvent.NodeId), ("path", resultEvent.Path), ("iter", (long)resultEvent.Iteration),
                ("time", resultEvent.Time.UtcTicks), ("timeText", Iso(resultEvent.Time)), ("rating", resultEvent.Rating is { } rating ? RatingText(rating) : null), ("message", resultEvent.Message),
                ("cancelled", resultEvent.Cancelled ? 1L : 0L)).ConfigureAwait(false);

            if (resultEvent.Kind == ResultEventKind.Step && (resultEvent.Value is not null || resultEvent.LowLimit is not null || resultEvent.HighLimit is not null))
            {
                var (type, number, index, text) = Typed(resultEvent.Value);
                await writer.ExecAsync(
                    """
                    INSERT INTO tu_measurements (run_id, seq, node_id, value_type, value_double, value_int, value_text, unit, low_limit, high_limit, rating)
                    VALUES (@run, @seq, @node, @type, @d, @i, @text, @unit, @low, @high, @rating)
                    """,
                    ("run", run.RunId), ("seq", resultEvent.Sequence), ("node", resultEvent.NodeId), ("type", type), ("d", Dbl(number)), ("i", Lng(index)), ("text", text),
                    ("unit", resultEvent.Unit), ("low", Dbl(resultEvent.LowLimit)), ("high", Dbl(resultEvent.HighLimit)), ("rating", resultEvent.Rating is { } r2 ? RatingText(r2) : null)).ConfigureAwait(false);
            }
        }
    }

    private static async Task StoreWaveformsAsync(Writer writer, ResultRun run)
    {
        var no = 0L;
        foreach (var rw in run.Waveforms)
        {
            no++;
            var wave = rw.Data;
            var parts = Math.Max(1, (wave.Count + SamplesPerChunk - 1) / SamplesPerChunk);
            for (var index = 0; index < parts; index++)
            {
                var offset = index * SamplesPerChunk;
                var count = Math.Max(0, Math.Min(SamplesPerChunk, wave.Count - offset));
                await writer.ExecAsync(
                    """
                    INSERT INTO tu_waveform_chunks (run_id, waveform_no, part, parts, waveform_id, event_seq, node_id, channel, unit, sample_rate, start_utc,
                        sample_offset, sample_count, total_samples, sample_type, samples, times, quality, sync_group)
                    VALUES (@run, @no, @part, @parts, @id, @event, @node, @channel, @unit, @rate, @start,
                        @offset, @count, @total, @type, @samples, @times, @quality, @sync)
                    """,
                    ("run", run.RunId), ("no", no), ("part", (long)index), ("parts", (long)parts), ("id", rw.Id), ("event", rw.EventSequence), ("node", rw.NodeId),
                    ("channel", wave.Channel), ("unit", wave.Unit), ("rate", wave.SampleRate), ("start", wave.Start.UtcTicks), ("offset", (long)offset),
                    ("count", (long)count), ("total", (long)wave.Count), ("type", "f64le"), ("samples", ToBytes(wave.Values.ReadBlock(offset, count))),
                    ("times", wave.TimeValues is { } sampleBuffer ? ToBytes(sampleBuffer.ReadBlock(offset, count)) : new TypedNull(DbType.Binary)), ("quality", wave.Quality), ("sync", wave.SyncGroup)).ConfigureAwait(false);
            }
        }

    }

    /// <summary>Verbindung öffnen und Schema migrieren (Bedienknopf „Verbindung testen“).</summary>
    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var version = connection.ServerVersion;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tu_runs";
        var runs = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return $"Verbunden: {Dialect.DisplayName} {version}, Schema v{Dialect.LatestVersion}, {runs} Run(s) gespeichert.";
    }

    public async Task<StoredRunCounts> CountAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var writer = new Writer(connection, null, cancellationToken);
        async Task<int> C(string table) => (int)await writer.ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE run_id = @run", ("run", runId)).ConfigureAwait(false);
        return new StoredRunCounts(await C("tu_runs"), await C("tu_step_executions"), await C("tu_measurements"),
            await C("tu_waveform_chunks"), await C("tu_operator_inputs"), await C("tu_development_events"));
    }

    /// <summary>Samples einer Kurve aus den Chunks zusammensetzen (Rundreise-Prüfung).</summary>
    public async Task<double[]> LoadSamplesAsync(string runId, long waveformNo, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sample_offset, samples, total_samples FROM tu_waveform_chunks WHERE run_id = @run AND waveform_no = @no ORDER BY part";
        Writer.Add(cmd, "run", runId);
        Writer.Add(cmd, "no", waveformNo);
        double[]? result = null;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var offset = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            var bytes = (byte[])reader.GetValue(1);
            result ??= new double[Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture)];
            for (var index = 0; index < bytes.Length / 8; index++) result[offset + index] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(index * 8));
        }
        return result ?? [];
    }

    /// <summary>Run vollständig entfernen – nur für die Vertragsprüfung mit eigener Test-Run-ID.</summary>
    public async Task DeleteRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var writer = new Writer(connection, tx, cancellationToken);
        foreach (var table in SqlDialect.RunTables)
            await writer.ExecAsync($"DELETE FROM {table} WHERE run_id = @run", ("run", runId)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ Lesen (Auswertung, Berichte)

    private static (string Where, (string, object?)[] Parameters) RunFilter(RunQuery runQuery, string alias)
    {
        var clauses = new List<string>();
        var parameters = new List<(string, object?)>();
        if (runQuery.From is { } timestamp) { clauses.Add($"{alias}.started_utc >= @qfrom"); parameters.Add(("qfrom", timestamp.UtcTicks)); }
        if (runQuery.To is { } timestamp2) { clauses.Add($"{alias}.started_utc <= @qto"); parameters.Add(("qto", timestamp2.UtcTicks)); }
        if (!string.IsNullOrWhiteSpace(runQuery.Test)) { clauses.Add($"{alias}.test_name = @qtest"); parameters.Add(("qtest", runQuery.Test)); }
        if (!string.IsNullOrWhiteSpace(runQuery.Station)) { clauses.Add($"{alias}.station = @qstation"); parameters.Add(("qstation", runQuery.Station)); }
        if (!string.IsNullOrWhiteSpace(runQuery.DutId)) { clauses.Add($"{alias}.dut_id = @qdut"); parameters.Add(("qdut", runQuery.DutId)); }
        if (!runQuery.IncludeDevelopment) clauses.Add($"{alias}.development_run = 0");
        return (clauses.Count == 0 ? "1 = 1" : string.Join(" AND ", clauses), [.. parameters]);
    }

    private static string? Str(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    private static long Long(DbDataReader reader, int ordinal) => Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    private static long? OptLong(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Long(reader, ordinal);
    private static double? OptDouble(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(long utcTicks, string? text)
        => text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
            ? timestamp : new DateTimeOffset(utcTicks, TimeSpan.Zero);

    private static ResultRating? Rating(string? text) => text switch
    {
        "PASS" => ResultRating.Pass,
        "FAIL" => ResultRating.Fail,
        "ERROR" => ResultRating.Error,
        "NRESULT" => ResultRating.NResult,
        _ => null,
    };

    private static ResultEventKind Kind(string text) => text switch
    {
        "step" => ResultEventKind.Step,
        "group" => ResultEventKind.Group,
        "group_not_entered" => ResultEventKind.GroupNotEntered,
        "operator_shown" => ResultEventKind.OperatorShown,
        "diagnostic" => ResultEventKind.Diagnostic,
        "operator_abort" => ResultEventKind.Abort,
        _ => Enum.TryParse<ResultEventKind>(text, ignoreCase: true, out var resultEventKind) ? resultEventKind : ResultEventKind.Diagnostic,
    };

    private static object? Value(string? type, double? decimalValue, long? integerValue, string? text) => type switch
    {
        "double" => decimalValue ?? (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var xValue) ? xValue : null),
        "int" => integerValue,
        "bool" => integerValue is { } bit ? bit != 0 : text == "true",
        "time" => integerValue is { } ticks ? new DateTime(ticks, DateTimeKind.Utc) : null,
        "text" => text,
        _ => null,
    };

    private static List<ResultNode> Structure(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray().Select(element => new ResultNode(
                element.GetProperty("id").GetString() ?? "", element.TryGetProperty("parent", out var element2) ? element2.GetString() : null, element.GetProperty("name").GetString() ?? "",
                element.GetProperty("kind").GetString() ?? "", element.TryGetProperty("block", out var element3) ? element3.GetString() : null, null)).ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Läufe mit Struktur, Schritten, Messwerten und Werkereingaben lesen – ohne Kurven (diese bei Bedarf über
    /// <see cref="LoadSamplesAsync"/>). Drei Abfragen insgesamt, unabhängig von der Anzahl der Läufe.
    /// </summary>
    public async Task<IReadOnlyList<ResultRun>> QueryRunsAsync(RunQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var writer = new Writer(connection, null, cancellationToken);
        var (where, parameters) = RunFilter(query, "r");

        var runs = new Dictionary<string, (ResultRun Run, List<ResultEvent> Events)>(StringComparer.Ordinal);
        var order = new List<string>();
        await using (var cmd = writer.Command(
            $"""
            SELECT r.run_id, r.station_run_id, r.station, r.dut_id, r.test_name, r.test_version, r.started_utc, r.started_text, r.ended_utc, r.ended_text,
                r.rating, r.werror, r.development_run, r.application, d.structure_json, r.order_id
            FROM tu_runs r LEFT JOIN tu_definitions d ON d.definition_id = r.definition_id
            WHERE {where} ORDER BY r.started_utc
            """, parameters))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (order.Count >= query.Limit) break;
                var events = new List<ResultEvent>();
                var run = ReadRun(reader, events);
                runs[run.RunId] = (run, events);
                order.Add(run.RunId);
            }
        }
        if (order.Count == 0) return [];

        await using (var cmd = writer.Command(
            $"""
            SELECT s.run_id, s.seq, s.kind, s.node_id, s.path, s.iteration, s.time_utc, s.time_text, s.rating, s.message, s.cancelled,
                m.value_type, m.value_double, m.value_int, m.value_text, m.unit, m.low_limit, m.high_limit
            FROM tu_step_executions s LEFT JOIN tu_measurements m ON m.run_id = s.run_id AND m.seq = s.seq
            WHERE s.run_id IN (SELECT r.run_id FROM tu_runs r WHERE {where})
            ORDER BY s.run_id, s.seq
            """, parameters))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!runs.TryGetValue(Str(reader, 0)!, out var entry)) continue;
                entry.Events.Add(new ResultEvent(Long(reader, 1), Kind(Str(reader, 2) ?? ""), Str(reader, 3), Str(reader, 4) ?? "", (int)Long(reader, 5),
                    Time(Long(reader, 6), Str(reader, 7)), Rating(Str(reader, 8)),
                    Value(Str(reader, 11), OptDouble(reader, 12), OptLong(reader, 13), Str(reader, 14)), Str(reader, 15), OptDouble(reader, 16), OptDouble(reader, 17),
                    Str(reader, 9), Long(reader, 10) != 0));
            }
        }

        await using (var cmd = writer.Command(
            $"""
            SELECT o.run_id, o.seq, o.node_id, o.value_type, o.value_text, o.time_utc
            FROM tu_operator_inputs o WHERE o.run_id IN (SELECT r.run_id FROM tu_runs r WHERE {where})
            """, parameters))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!runs.TryGetValue(Str(reader, 0)!, out var entry)) continue;
                var type = Str(reader, 3);
                var text = Str(reader, 4);
                var value = OperatorValue(type, text);
                entry.Events.Add(new ResultEvent(Long(reader, 1), ResultEventKind.OperatorAnswer, Str(reader, 2), "", 0,
                    new DateTimeOffset(Long(reader, 5), TimeSpan.Zero), Value: value));
            }
        }

        foreach (var (_, events) in runs.Values) events.Sort((resultEvent, resultEvent2) => resultEvent.Sequence.CompareTo(resultEvent2.Sequence));
        return order.Select(id => runs[id].Run).ToList();
    }

    private static object? OperatorValue(string? type, string? text)
    {
        return type switch
        {
            "double" => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : text,
            "int" => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ? length : text,
            "bool" => text == "true",
            "none" => null,
            _ => text,
        };
    }

    private static ResultRun ReadRun(DbDataReader reader, List<ResultEvent> events)
    {
        return new ResultRun
        {
            RunId = Str(reader, 0)!,
            StationRunId = Str(reader, 1) ?? "",
            Station = Str(reader, 2) ?? "",
            DutId = Str(reader, 3) ?? "",
            Order = Str(reader, 15) ?? "",
            TestName = Str(reader, 4) ?? "",
            TestVersion = Str(reader, 5) ?? "",
            Started = Time(Long(reader, 6), Str(reader, 7)),
            Ended = Time(Long(reader, 8), Str(reader, 9)),
            Rating = Rating(Str(reader, 10)) ?? ResultRating.NResult,
            OperatorAbort = Long(reader, 11) != 0,
            IsDevelopmentRun = Long(reader, 12) != 0,
            Application = Str(reader, 13) ?? "Testumgebung",
            FilteredFor = "Datenbank",
            Structure = Structure(Str(reader, 14)),
            Events = events,
        };
    }

    /// <summary>Einen Lauf vollständig lesen – mit Kurven (Chunks zu <see cref="ResultWaveform"/> zusammengesetzt).</summary>
    public async Task<ResultRun?> LoadRunAsync(string runId, CancellationToken cancellationToken)
    {
        var single = (await QueryRunsByIdAsync(runId, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (single is null) return null;
        var waveforms = await LoadWaveformsAsync(runId, cancellationToken).ConfigureAwait(false);
        if (waveforms.Count == 0) return single;
        return single.CopyWith("Datenbank", single.Events.ToList(), waveforms, single.Attachments.ToList());
    }

    private async Task<IReadOnlyList<ResultRun>> QueryRunsByIdAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        // QueryRunsAsync filtert nicht nach Run-ID; hier gezielt über den vorhandenen Weg mit Nachfilter.
        var all = await QueryRunsAsync(new RunQuery(Limit: int.MaxValue), cancellationToken).ConfigureAwait(false);
        return all.Where(run => run.RunId == runId).ToList();
    }

    private async Task<List<ResultWaveform>> LoadWaveformsAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var writer = new Writer(connection, null, cancellationToken);
        var waves = new List<ResultWaveform>();
        await using (var cmd = writer.Command(
            """
            SELECT waveform_no, waveform_id, event_seq, node_id, channel, unit, sample_rate, start_utc, total_samples, times, quality, sync_group
            FROM tu_waveform_chunks WHERE run_id = @run AND part = 0 ORDER BY waveform_no
            """, ("run", runId)))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var no = Long(reader, 0);
                var samples = await LoadSamplesAsync(runId, no, cancellationToken).ConfigureAwait(false);
                double[]? times = null;
                if (!reader.IsDBNull(9)) times = await LoadTimesAsync(runId, no, cancellationToken).ConfigureAwait(false);
                var wave = new Waveform(Str(reader, 4) ?? "", Str(reader, 5) ?? "", Convert.ToDouble(reader.GetValue(6), CultureInfo.InvariantCulture),
                    new DateTimeOffset(Long(reader, 7), TimeSpan.Zero), samples, times, Str(reader, 10), Str(reader, 11));
                waves.Add(new ResultWaveform(Str(reader, 1) ?? "", Long(reader, 2), Str(reader, 3), wave));
            }
        }
        return waves;
    }

    private async Task<double[]?> LoadTimesAsync(string runId, long waveformNo, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sample_offset, times, total_samples FROM tu_waveform_chunks WHERE run_id = @run AND waveform_no = @no ORDER BY part";
        Writer.Add(cmd, "run", runId);
        Writer.Add(cmd, "no", waveformNo);
        double[]? result = null;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(1)) return null;
            var offset = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            var bytes = (byte[])reader.GetValue(1);
            result ??= new double[Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture)];
            for (var index = 0; index < bytes.Length / 8; index++) result[offset + index] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(index * 8));
        }
        return result;
    }

    /// <summary>Verbindung öffnen und Schema sicherstellen (auch für die Release-Ablage).</summary>
    internal async Task<DbConnection> OpenMigratedAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = _factory.CreateConnection() ?? throw new InvalidOperationException("Provider liefert keine Verbindung.");
        connection.ConnectionString = _connectionString;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return connection;
    }

    private async Task EnsureSchemaAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (_migrated) return;
        await _migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_migrated) return;
            await new SchemaMigrator(Dialect).MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
            _migrated = true;
        }
        finally
        {
            _migrationGate.Release();
        }
    }

    private static async Task<bool> ExistsAsync(DbConnection connection, DbTransaction tx, string runId, CancellationToken ct)
        => await new Writer(connection, tx, ct).ScalarAsync("SELECT COUNT(*) FROM tu_runs WHERE run_id = @run", ("run", runId)).ConfigureAwait(false) > 0;

    // ------------------------------------------------------------------ Abbildung

    internal static string DefinitionId(ResultRun run)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{run.TestName}\n{run.TestVersion}\n{StructureJson(run)}")))[..40];

    internal static string StructureJson(ResultRun run)
        => JsonSerializer.Serialize(run.Structure.Select(resultNode => new { id = resultNode.Id, parent = resultNode.ParentId, name = resultNode.Name, kind = resultNode.Kind, block = resultNode.BlockType }));

    private static string Iso(DateTimeOffset timestamp) => timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    private static string RatingText(ResultRating rating) => rating switch
    {
        ResultRating.Pass => "PASS",
        ResultRating.Fail => "FAIL",
        ResultRating.Error => "ERROR",
        _ => "NRESULT",
    };

    private static string KindText(ResultEventKind resultEventKind) => resultEventKind switch
    {
        ResultEventKind.Step => "step",
        ResultEventKind.Group => "group",
        ResultEventKind.GroupNotEntered => "group_not_entered",
        ResultEventKind.OperatorShown => "operator_shown",
        ResultEventKind.Diagnostic => "diagnostic",
        ResultEventKind.Abort => "operator_abort",
        _ => resultEventKind.ToString().ToLowerInvariant(),
    };

    /// <summary>Typisierte Ablage: double → value_double, long/bool → value_int, Text/Zeit → value_text.</summary>
    private static (string Type, double? D, long? I, string? Text) Typed(object? value) => value switch
    {
        null => ("none", null, null, null),
        double number => ("double", number, null, number.ToString("R", CultureInfo.InvariantCulture)),
        long integerValue => ("int", null, integerValue, integerValue.ToString(CultureInfo.InvariantCulture)),
        bool flag => ("bool", null, flag ? 1 : 0, flag ? "true" : "false"),
        DateTime dateTime => ("time", null, dateTime.ToUniversalTime().Ticks, dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        string text => ("text", null, null, text),
        _ => ("text", null, null, Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    /// <summary>NULL mit Spaltentyp – Npgsql und SqlClient lehnen untypisiertes NULL für Zahlen-/Binärspalten ab.</summary>
    internal sealed record TypedNull(DbType Type);

    internal static object Dbl(double? v) => v is { } number ? number : new TypedNull(DbType.Double);

    internal static object Lng(long? v) => v is { } length ? length : new TypedNull(DbType.Int64);

    private static byte[] ToBytes(ReadOnlySpan<double> values)
    {
        var bytes = new byte[values.Length * 8];
        for (var index = 0; index < values.Length; index++) BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(index * 8), values[index]);
        return bytes;
    }

    /// <summary>Server/Datei aus dem Verbindungsstring für die Anzeige – ohne Passwort.</summary>
    internal static string DescribeTarget(string connectionString)
    {
        try
        {
            var dbConnectionStringBuilder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (var key in new[] { "Data Source", "Server", "Host", "Filename" })
                if (dbConnectionStringBuilder.TryGetValue(key, out var value) && value is not null)
                {
                    var db = dbConnectionStringBuilder.TryGetValue("Database", out var definition) || dbConnectionStringBuilder.TryGetValue("Initial Catalog", out definition) ? $"/{definition}" : "";
                    return $"{value}{db}";
                }
        }
        catch (ArgumentException) { }
        return "Verbindung";
    }

    /// <summary>Kleine Hilfe für parametrisierte Befehle mit expliziten Typen (Npgsql ist streng typisiert).</summary>
    internal sealed class Writer(DbConnection connection, DbTransaction? tx, CancellationToken ct)
    {
        public async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var cmd = Create(sql, parameters);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<int> ExecCountAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var cmd = Create(sql, parameters);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<object?> ValueAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var cmd = Create(sql, parameters);
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is DBNull ? null : value;
        }

        public DbCommand Command(string sql, params (string Name, object? Value)[] parameters) => Create(sql, parameters);

        public async Task<long> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var cmd = Create(sql, parameters);
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private DbCommand Create(string sql, (string Name, object? Value)[] parameters)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = tx;
            foreach (var (name, value) in parameters) Add(cmd, name, value);
            return cmd;
        }

        public static void Add(DbCommand cmd, string name, object? value)
        {
            var dbParameter = cmd.CreateParameter();
            dbParameter.ParameterName = "@" + name;
            (dbParameter.DbType, dbParameter.Value) = value switch
            {
                null => (DbType.String, DBNull.Value),
                TypedNull typedNull => (typedNull.Type, DBNull.Value),
                string text => (DbType.String, text),
                long integerValue => (DbType.Int64, integerValue),
                int index => (DbType.Int64, (long)index),
                double number => (DbType.Double, number),
                byte[] bytes => (DbType.Binary, bytes),
                _ => (DbType.String, (object)Convert.ToString(value, CultureInfo.InvariantCulture)!),
            };
            cmd.Parameters.Add(dbParameter);
        }
    }
}

/// <summary>Wendet fehlende Migrationen an (je Version in einer Transaktion, soweit der Provider DDL transaktional kann).</summary>
public sealed class SchemaMigrator(SqlDialect dialect)
{
    public async Task<int> MigrateAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = dialect.CreateVersionTable;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        long current;
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = dialect.SelectVersion;
            var value = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            current = value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        var applied = 0;
        foreach (var (version, statements) in dialect.Migrations.Where(entry => entry.Version > current))
        {
            await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var writer = new SqlRunDatabase.Writer(connection, tx, cancellationToken);
            foreach (var sql in statements) await writer.ExecAsync(sql).ConfigureAwait(false);
            await writer.ExecAsync(dialect.InsertVersion, ("version", (long)version), ("applied", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            applied++;
        }
        return applied;
    }
}
