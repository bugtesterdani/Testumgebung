using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Testumgebung.Engine.Tests;

/// <summary>
/// Minimaler ADO.NET-Provider über die native SQLite-Bibliothek (Linux: libsqlite3, Windows: winsqlite3.dll aus dem System).
/// Nur für Tests: So läuft der echte Datenbankadapter gegen eine echte SQL-Engine, ohne NuGet-Pakete.
/// Die Anwendung selbst verwendet Microsoft.Data.Sqlite.
/// </summary>
internal sealed class NativeSqliteFactory : DbProviderFactory
{
    public static readonly NativeSqliteFactory Instance = new();

    public override DbConnection CreateConnection() => new NativeSqliteConnection();
    public override DbCommand CreateCommand() => new NativeSqliteCommand();
    public override DbParameter CreateParameter() => new NativeSqliteParameter();

    public static bool IsAvailable
    {
        get
        {
            try { return Native.sqlite3_libversion_number() > 3_000_000; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }
}

internal static class Native
{
    private const string Lib = "sqlite3";

    static Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, assembly, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            foreach (var candidate in new[] { "winsqlite3", "e_sqlite3", "libsqlite3.so.0", "sqlite3", "libsqlite3" })
                if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) return handle;
            return IntPtr.Zero;
        });
    }

    public const int Ok = 0, Row = 100, Done = 101;
    public const int Integer = 1, Float = 2, Text = 3, Blob = 4, Null = 5;
    public static readonly IntPtr Transient = new(-1);

    [DllImport(Lib)] public static extern int sqlite3_libversion_number();
    [DllImport(Lib)] public static extern IntPtr sqlite3_libversion();
    [DllImport(Lib)] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib)] public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, out IntPtr tail);
    [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_changes(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_bind_parameter_index(IntPtr stmt, byte[] name);
    [DllImport(Lib)] public static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport(Lib)] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Lib)] public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);
    [DllImport(Lib)] public static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);
    [DllImport(Lib)] public static extern int sqlite3_bind_blob(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);
    [DllImport(Lib)] public static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_name(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern double sqlite3_column_double(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_column_bytes(IntPtr stmt, int col);

    public static byte[] Utf8Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    public static string Str(IntPtr p) => Marshal.PtrToStringUTF8(p) ?? "";
}

internal sealed class NativeSqliteConnection : DbConnection
{
    private IntPtr _db;
    private string _connectionString = "";

    internal IntPtr Handle => _db != IntPtr.Zero ? _db : throw new InvalidOperationException("Verbindung ist nicht geöffnet.");

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set => _connectionString = value ?? "";
    }

    public override string Database => "main";
    public override string DataSource => new DbConnectionStringBuilder { ConnectionString = _connectionString }["Data Source"]?.ToString() ?? "";
    public override string ServerVersion => Native.Str(Native.sqlite3_libversion());
    public override ConnectionState State => _db == IntPtr.Zero ? ConnectionState.Closed : ConnectionState.Open;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Open()
    {
        if (_db != IntPtr.Zero) return;
        const int readWriteCreate = 0x2 | 0x4, fullMutex = 0x10000;
        var rc = Native.sqlite3_open_v2(Native.Utf8Z(DataSource), out _db, readWriteCreate | fullMutex, IntPtr.Zero);
        if (rc != Native.Ok)
        {
            var msg = _db == IntPtr.Zero ? $"Fehler {rc}" : Native.Str(Native.sqlite3_errmsg(_db));
            Close();
            throw new NativeSqliteException($"SQLite-Datei '{DataSource}' nicht zu öffnen: {msg}");
        }
        Native.sqlite3_busy_timeout(_db, 5000);
        using var fk = CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys = ON";
        fk.ExecuteNonQuery();
    }

    public override void Close()
    {
        if (_db == IntPtr.Zero) return;
        Native.sqlite3_close_v2(_db);
        _db = IntPtr.Zero;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        using (var cmd = CreateCommand())
        {
            cmd.CommandText = "BEGIN";
            cmd.ExecuteNonQuery();
        }
        return new NativeSqliteTransaction(this);
    }

    protected override DbCommand CreateDbCommand() => new NativeSqliteCommand { Connection = this };

    protected override void Dispose(bool disposing)
    {
        Close();
        base.Dispose(disposing);
    }
}

internal sealed class NativeSqliteException(string message) : DbException(message);

internal sealed class NativeSqliteTransaction(NativeSqliteConnection connection) : DbTransaction
{
    private bool _done;

    public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
    protected override DbConnection DbConnection => connection;

    public override void Commit() => Finish("COMMIT");
    public override void Rollback() => Finish("ROLLBACK");

    private void Finish(string sql)
    {
        if (_done) return;
        _done = true;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_done && connection.State == ConnectionState.Open) Rollback();
        base.Dispose(disposing);
    }
}

internal sealed class NativeSqliteParameter : DbParameter
{
    public override DbType DbType { get; set; } = DbType.String;
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; } = true;
    [AllowNull] public override string ParameterName { get; set; } = "";
    public override int Size { get; set; }
    [AllowNull] public override string SourceColumn { get; set; } = "";
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }

    public override void ResetDbType() => DbType = DbType.String;
}

internal sealed class NativeSqliteParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;
    public override object SyncRoot => _items;

    public override int Add(object value)
    {
        _items.Add((DbParameter)value);
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var v in values) Add(v!);
    }

    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => _items.Contains((DbParameter)value);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
    public override void Remove(object value) => _items.Remove((DbParameter)value);
    public override void RemoveAt(int index) => _items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
}

internal sealed class NativeSqliteCommand : DbCommand
{
    private readonly NativeSqliteParameterCollection _parameters = new();

    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; } = 30;
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    private NativeSqliteConnection Conn => (NativeSqliteConnection?)DbConnection ?? throw new InvalidOperationException("Keine Verbindung.");

    public override void Cancel() { }
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new NativeSqliteParameter();

    public override int ExecuteNonQuery()
    {
        Run(out _);
        return Native.sqlite3_changes(Conn.Handle);
    }

    public override object? ExecuteScalar()
    {
        var rows = Run(out _);
        return rows.Count == 0 || rows[0].Length == 0 ? null : rows[0][0] ?? DBNull.Value;
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var rows = Run(out var names);
        return new NativeSqliteReader(names, rows);
    }

    /// <summary>Anweisung vorbereiten, Parameter binden, alle Zeilen lesen (für Tests ausreichend).</summary>
    private List<object?[]> Run(out string[] names)
    {
        var db = Conn.Handle;
        var sql = Encoding.UTF8.GetBytes(CommandText);
        if (Native.sqlite3_prepare_v2(db, sql, sql.Length, out var stmt, out _) != Native.Ok)
            throw new NativeSqliteException($"{Native.Str(Native.sqlite3_errmsg(db))} in: {CommandText}");
        try
        {
            foreach (DbParameter p in _parameters)
            {
                var index = Native.sqlite3_bind_parameter_index(stmt, Native.Utf8Z(p.ParameterName.StartsWith('@') ? p.ParameterName : "@" + p.ParameterName));
                if (index == 0) continue;
                var rc = p.Value switch
                {
                    null or DBNull => Native.sqlite3_bind_null(stmt, index),
                    long l => Native.sqlite3_bind_int64(stmt, index, l),
                    int i => Native.sqlite3_bind_int64(stmt, index, i),
                    bool b => Native.sqlite3_bind_int64(stmt, index, b ? 1 : 0),
                    double d => Native.sqlite3_bind_double(stmt, index, d),
                    byte[] bytes => Native.sqlite3_bind_blob(stmt, index, bytes, bytes.Length, Native.Transient),
                    _ => BindText(stmt, index, Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
                };
                if (rc != Native.Ok) throw new NativeSqliteException($"Parameter {p.ParameterName}: {Native.Str(Native.sqlite3_errmsg(db))}");
            }

            var count = Native.sqlite3_column_count(stmt);
            names = Enumerable.Range(0, count).Select(i => Native.Str(Native.sqlite3_column_name(stmt, i))).ToArray();
            var rows = new List<object?[]>();
            while (true)
            {
                var rc = Native.sqlite3_step(stmt);
                if (rc == Native.Done) break;
                if (rc != Native.Row) throw new NativeSqliteException($"{Native.Str(Native.sqlite3_errmsg(db))} in: {CommandText}");
                var row = new object?[count];
                for (var c = 0; c < count; c++)
                {
                    row[c] = Native.sqlite3_column_type(stmt, c) switch
                    {
                        Native.Integer => Native.sqlite3_column_int64(stmt, c),
                        Native.Float => Native.sqlite3_column_double(stmt, c),
                        Native.Text => Native.Str(Native.sqlite3_column_text(stmt, c)),
                        Native.Blob => ReadBlob(stmt, c),
                        _ => DBNull.Value,
                    };
                }
                rows.Add(row);
            }
            return rows;
        }
        finally
        {
            Native.sqlite3_finalize(stmt);
        }
    }

    private static int BindText(IntPtr stmt, int index, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Native.sqlite3_bind_text(stmt, index, bytes, bytes.Length, Native.Transient);
    }

    private static byte[] ReadBlob(IntPtr stmt, int c)
    {
        var n = Native.sqlite3_column_bytes(stmt, c);
        var data = new byte[n];
        if (n > 0) Marshal.Copy(Native.sqlite3_column_blob(stmt, c), data, 0, n);
        return data;
    }
}

internal sealed class NativeSqliteReader(string[] names, List<object?[]> rows) : DbDataReader
{
    private int _index = -1;

    private object?[] Current => rows[_index];

    public override int FieldCount => names.Length;
    public override bool HasRows => rows.Count > 0;
    public override bool IsClosed => false;
    public override int RecordsAffected => -1;
    public override int Depth => 0;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => ++_index < rows.Count;
    public override bool NextResult() => false;
    public override object GetValue(int ordinal) => Current[ordinal] ?? DBNull.Value;
    public override bool IsDBNull(int ordinal) => Current[ordinal] is null or DBNull;
    public override string GetName(int ordinal) => names[ordinal];
    public override int GetOrdinal(string name) => Array.IndexOf(names, name);
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
    public override Type GetFieldType(int ordinal) => Current[ordinal]?.GetType() ?? typeof(object);
    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < n; i++) values[i] = GetValue(i);
        return n;
    }

    public override bool GetBoolean(int ordinal) => Convert.ToInt64(GetValue(ordinal)) != 0;
    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
    public override char GetChar(int ordinal) => GetString(ordinal)[0];
    public override DateTime GetDateTime(int ordinal) => DateTime.Parse(GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
    public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
    public override string GetString(int ordinal) => Convert.ToString(GetValue(ordinal)) ?? "";

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var data = (byte[])GetValue(ordinal);
        if (buffer is null) return data.Length;
        var n = (int)Math.Min(length, data.Length - dataOffset);
        Array.Copy(data, dataOffset, buffer, bufferOffset, n);
        return n;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var s = GetString(ordinal);
        if (buffer is null) return s.Length;
        var n = (int)Math.Min(length, s.Length - dataOffset);
        s.CopyTo((int)dataOffset, buffer, bufferOffset, n);
        return n;
    }

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}

/// <summary>Zeichnet SQL und Parameter auf, ohne eine Datenbank zu benötigen (für die Syntaxprüfung der Dialekte).</summary>
internal sealed class RecordingFactory : DbProviderFactory
{
    public List<(string Sql, Dictionary<string, object?> Parameters)> Log { get; } = [];

    /// <summary>Antwort für ExecuteScalar je SQL (Standard 0 = „nichts vorhanden“).</summary>
    public Func<string, object?>? Scalars { get; set; }

    /// <summary>Parametertypen je Anweisung (für die Wiedergabe gegen echte Server mit strenger Typisierung).</summary>
    public List<Dictionary<string, DbType>> Types { get; } = [];

    public override DbConnection CreateConnection() => new RecordingConnection(this);

    internal sealed class RecordingConnection(RecordingFactory owner) : DbConnection
    {
        private ConnectionState _state;
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "aufzeichnung";
        public override string DataSource => "aufzeichnung";
        public override string ServerVersion => "0";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) { }
        public override void Open() => _state = ConnectionState.Open;
        public override void Close() => _state = ConnectionState.Closed;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new RecordingTransaction(this);
        protected override DbCommand CreateDbCommand() => new RecordingCommand(owner) { Connection = this };
    }

    private sealed class RecordingTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class RecordingCommand(RecordingFactory owner) : DbCommand
    {
        private readonly NativeSqliteParameterCollection _parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new NativeSqliteParameter();

        public override int ExecuteNonQuery()
        {
            Record();
            return 1;
        }

        public override object? ExecuteScalar()
        {
            Record();
            return owner.Scalars?.Invoke(CommandText) ?? 0L; // Standard „nichts vorhanden“ → alle Einfügungen werden aufgezeichnet
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Record();
            return new NativeSqliteReader([], []);
        }

        private void Record()
        {
            owner.Log.Add((CommandText, _parameters.Cast<DbParameter>().ToDictionary(p => p.ParameterName.TrimStart('@'), p => p.Value is DBNull ? null : p.Value)));
            owner.Types.Add(_parameters.Cast<DbParameter>().ToDictionary(p => p.ParameterName.TrimStart('@'), p => p.DbType));
        }
    }
}
