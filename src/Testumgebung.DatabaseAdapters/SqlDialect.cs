/**
 * @file
 * @brief Kapselt providerspezifisches SQL und Migrationen hinter einem gemeinsamen logischen Datenmodell.
 */

namespace Testumgebung.DatabaseAdapters;

public enum DatabaseProvider
{
    SqlServer,
    PostgreSql,
    MySql,
    Sqlite,
}

/// <summary>
/// Providerabhängige SQL-Bausteine und migrationsfähige Schema-Skripte. Gemeinsames logisches Modell
/// (Abschnitt 9.3): Runs, Definitions, StepExecutions, Measurements, WaveformChunks, OperatorInputs, DevelopmentEvents.
/// Parameter heißen in allen Dialekten <c>@name</c> (SqlClient, Npgsql, MySqlConnector und Microsoft.Data.Sqlite unterstützen das).
/// Zeiten werden portabel als UTC-Ticks (BIGINT) plus lesbarer ISO-8601-Text gespeichert.
/// </summary>
public abstract class SqlDialect
{
    public abstract DatabaseProvider Provider { get; }
    public abstract string DisplayName { get; }

    protected abstract string Text { get; }          // unbegrenzter Text
    protected abstract string Key { get; }           // indizierbarer Text (Ids)
    protected abstract string Short { get; }         // kurzer Text (Bewertung, Art, Einheit)
    protected abstract string Big { get; }
    protected abstract string Real { get; }
    protected abstract string Blob { get; }
    protected virtual string TableSuffix => "";

    public static SqlDialect For(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => new SqlServerDialect(),
        DatabaseProvider.PostgreSql => new PostgreSqlDialect(),
        DatabaseProvider.MySql => new MySqlDialect(),
        DatabaseProvider.Sqlite => new SqliteDialect(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>Tabelle für die Schemaversion anlegen, falls nicht vorhanden.</summary>
    public virtual string CreateVersionTable =>
        $"CREATE TABLE IF NOT EXISTS tu_schema_version (version {Big} NOT NULL PRIMARY KEY, applied_utc {Short} NOT NULL){TableSuffix}";

    public string SelectVersion => "SELECT MAX(version) FROM tu_schema_version";

    public string InsertVersion => "INSERT INTO tu_schema_version (version, applied_utc) VALUES (@version, @applied)";

    /// <summary>Migrationen in aufsteigender Reihenfolge; jede Version besteht aus einzelnen Anweisungen.</summary>
    public IReadOnlyList<(int Version, IReadOnlyList<string> Statements)> Migrations =>
    [
        (1, Version1()),
        (2, Version2()),
        (3, [$"ALTER TABLE tu_runs ADD order_id {Key}", "CREATE INDEX ix_tu_runs_order ON tu_runs (order_id)"]),
    ];

    public int LatestVersion => Migrations[^1].Version;

    private IReadOnlyList<string> Version1() =>
    [
        $"""
        CREATE TABLE tu_definitions (
            definition_id {Key} NOT NULL PRIMARY KEY,
            test_name {Text} NOT NULL,
            test_version {Short} NOT NULL,
            structure_json {Text} NOT NULL,
            created_utc {Big} NOT NULL
        ){TableSuffix}
        """,
        $"""
        CREATE TABLE tu_runs (
            run_id {Key} NOT NULL PRIMARY KEY,
            station_run_id {Text},
            station {Text},
            dut_id {Key} NOT NULL,
            definition_id {Key} NOT NULL,
            test_name {Text} NOT NULL,
            test_version {Short} NOT NULL,
            started_utc {Big} NOT NULL,
            started_text {Short} NOT NULL,
            ended_utc {Big} NOT NULL,
            ended_text {Short} NOT NULL,
            rating {Short} NOT NULL,
            werror {Big} NOT NULL,
            development_run {Big} NOT NULL,
            application {Text},
            stored_utc {Big} NOT NULL,
            CONSTRAINT fk_tu_runs_definition FOREIGN KEY (definition_id) REFERENCES tu_definitions (definition_id)
        ){TableSuffix}
        """,
        "CREATE INDEX ix_tu_runs_dut ON tu_runs (dut_id)",
        "CREATE INDEX ix_tu_runs_started ON tu_runs (started_utc)",
        $"""
        CREATE TABLE tu_step_executions (
            run_id {Key} NOT NULL,
            seq {Big} NOT NULL,
            kind {Short} NOT NULL,
            node_id {Key},
            path {Text},
            iteration {Big} NOT NULL,
            time_utc {Big} NOT NULL,
            time_text {Short} NOT NULL,
            rating {Short},
            message {Text},
            cancelled {Big} NOT NULL,
            PRIMARY KEY (run_id, seq),
            CONSTRAINT fk_tu_steps_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
        ){TableSuffix}
        """,
        $"""
        CREATE TABLE tu_measurements (
            run_id {Key} NOT NULL,
            seq {Big} NOT NULL,
            node_id {Key},
            value_type {Short} NOT NULL,
            value_double {Real},
            value_int {Big},
            value_text {Text},
            unit {Short},
            low_limit {Real},
            high_limit {Real},
            rating {Short},
            PRIMARY KEY (run_id, seq),
            CONSTRAINT fk_tu_meas_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
        ){TableSuffix}
        """,
        $"""
        CREATE TABLE tu_waveform_chunks (
            run_id {Key} NOT NULL,
            waveform_no {Big} NOT NULL,
            part {Big} NOT NULL,
            parts {Big} NOT NULL,
            waveform_id {Text} NOT NULL,
            event_seq {Big} NOT NULL,
            node_id {Key},
            channel {Text} NOT NULL,
            unit {Short},
            sample_rate {Real} NOT NULL,
            start_utc {Big} NOT NULL,
            sample_offset {Big} NOT NULL,
            sample_count {Big} NOT NULL,
            total_samples {Big} NOT NULL,
            sample_type {Short} NOT NULL,
            samples {Blob} NOT NULL,
            times {Blob},
            quality {Text},
            sync_group {Text},
            PRIMARY KEY (run_id, waveform_no, part),
            CONSTRAINT fk_tu_wave_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
        ){TableSuffix}
        """,
        $"""
        CREATE TABLE tu_operator_inputs (
            run_id {Key} NOT NULL,
            seq {Big} NOT NULL,
            node_id {Key},
            prompt {Text},
            value_type {Short},
            value_text {Text},
            time_utc {Big} NOT NULL,
            PRIMARY KEY (run_id, seq),
            CONSTRAINT fk_tu_input_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
        ){TableSuffix}
        """,
        $"""
        CREATE TABLE tu_development_events (
            run_id {Key} NOT NULL,
            seq {Big} NOT NULL,
            user_name {Text},
            details {Text},
            time_utc {Big} NOT NULL,
            PRIMARY KEY (run_id, seq),
            CONSTRAINT fk_tu_dev_run FOREIGN KEY (run_id) REFERENCES tu_runs (run_id)
        ){TableSuffix}
        """,
    ];

    /// <summary>Etappe 7: unveränderliche Release-Pakete und Audit-Log (nur anhängen, Hash-Kette).</summary>
    private IReadOnlyList<string> Version2() =>
    [
        $"""
        CREATE TABLE tu_releases (
            release_id {Key} NOT NULL PRIMARY KEY,
            project_name {Key} NOT NULL,
            version {Short} NOT NULL,
            state {Short} NOT NULL,
            package {Blob} NOT NULL,
            package_sha256 {Short} NOT NULL,
            package_size {Big} NOT NULL,
            created_by {Text} NOT NULL,
            created_utc {Big} NOT NULL,
            reviewed_by {Text},
            reviewed_utc {Big},
            released_by {Text},
            released_utc {Big},
            archived_utc {Big},
            review_report {Text},
            CONSTRAINT uq_tu_releases_version UNIQUE (project_name, version)
        ){TableSuffix}
        """,
        $"""
        CREATE TABLE tu_audit_log (
            seq {Big} NOT NULL PRIMARY KEY,
            time_utc {Big} NOT NULL,
            time_text {Short} NOT NULL,
            user_name {Text} NOT NULL,
            action {Text} NOT NULL,
            details {Text},
            release_id {Key},
            prev_hash {Short} NOT NULL,
            entry_hash {Short} NOT NULL
        ){TableSuffix}
        """,
    ];

    /// <summary>Tabellen in Löschreihenfolge (Kinder zuerst).</summary>
    public static IReadOnlyList<string> RunTables { get; } =
        ["tu_development_events", "tu_operator_inputs", "tu_waveform_chunks", "tu_measurements", "tu_step_executions", "tu_runs"];

    public override string ToString() => DisplayName;
}

public sealed class SqlServerDialect : SqlDialect
{
    public override DatabaseProvider Provider => DatabaseProvider.SqlServer;
    public override string DisplayName => "Microsoft SQL Server";
    protected override string Text => "NVARCHAR(MAX)";
    protected override string Key => "NVARCHAR(128)";
    protected override string Short => "NVARCHAR(64)";
    protected override string Big => "BIGINT";
    protected override string Real => "FLOAT";
    protected override string Blob => "VARBINARY(MAX)";

    // SQL Server kennt kein CREATE TABLE IF NOT EXISTS.
    public override string CreateVersionTable =>
        "IF OBJECT_ID(N'tu_schema_version', N'U') IS NULL CREATE TABLE tu_schema_version (version BIGINT NOT NULL PRIMARY KEY, applied_utc NVARCHAR(64) NOT NULL)";
}

public sealed class PostgreSqlDialect : SqlDialect
{
    public override DatabaseProvider Provider => DatabaseProvider.PostgreSql;
    public override string DisplayName => "PostgreSQL";
    protected override string Text => "TEXT";
    protected override string Key => "VARCHAR(128)";
    protected override string Short => "VARCHAR(64)";
    protected override string Big => "BIGINT";
    protected override string Real => "DOUBLE PRECISION";
    protected override string Blob => "BYTEA";
}

public sealed class MySqlDialect : SqlDialect
{
    public override DatabaseProvider Provider => DatabaseProvider.MySql;
    public override string DisplayName => "MySQL / MariaDB";
    protected override string Text => "LONGTEXT";
    protected override string Key => "VARCHAR(128)";
    protected override string Short => "VARCHAR(64)";
    protected override string Big => "BIGINT";
    protected override string Real => "DOUBLE";
    protected override string Blob => "LONGBLOB";
    protected override string TableSuffix => " ENGINE=InnoDB DEFAULT CHARSET=utf8mb4";
}

public sealed class SqliteDialect : SqlDialect
{
    public override DatabaseProvider Provider => DatabaseProvider.Sqlite;
    public override string DisplayName => "SQLite";
    protected override string Text => "TEXT";
    protected override string Key => "TEXT";
    protected override string Short => "TEXT";
    protected override string Big => "INTEGER";
    protected override string Real => "REAL";
    protected override string Blob => "BLOB";
}

/// <summary>Vollständiges Referenzskript je Provider (für DBA-Prüfung und manuelle Einrichtung, siehe docs/sql).</summary>
public static class SchemaScript
{
    public static string For(SqlDialect dialect)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("-- Testumgebung – Datenbankschema für ").Append(dialect.DisplayName).Append('\n');
        sb.Append("-- Erzeugt aus SqlDialect; die Anwendung migriert automatisch (tu_schema_version).\n\n");
        sb.Append(dialect.CreateVersionTable).Append(";\n\n");
        foreach (var (version, statements) in dialect.Migrations)
        {
            sb.Append("-- Version ").Append(version).Append('\n');
            foreach (var s in statements) sb.Append(s.Trim()).Append(";\n\n");
            sb.Append($"INSERT INTO tu_schema_version (version, applied_utc) VALUES ({version}, 'manuell');\n\n");
        }
        return sb.ToString();
    }
}
