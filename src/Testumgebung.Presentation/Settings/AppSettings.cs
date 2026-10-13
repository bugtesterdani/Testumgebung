using System.Text.Json;
using System.Text.Json.Serialization;
using Testumgebung.DatabaseAdapters;

namespace Testumgebung.Presentation.Settings;

/// <summary>Datenbankwahl in den Einstellungen.</summary>
public enum DatabaseChoice
{
    /// <summary>Keine Datenbank: nur Ergebnisdateien.</summary>
    None,
    /// <summary>Simulierte Datenbank (Ordner) für Vorführung ohne Server.</summary>
    Simulated,
    SqlServer,
    PostgreSql,
    MySql,
    Sqlite,
}

public sealed class DatabaseSettings
{
    public DatabaseChoice Provider { get; set; } = DatabaseChoice.Simulated;

    /// <summary>Verbindungsstring – in der Datei geschützt gespeichert (<see cref="ISecretProtector"/>).</summary>
    [JsonIgnore] public string ConnectionString { get; set; } = "";

    [JsonPropertyName("connectionString")] public string? ProtectedConnectionString { get; set; }

    public static DatabaseProvider? ProviderOf(DatabaseChoice c) => c switch
    {
        DatabaseChoice.SqlServer => DatabaseProvider.SqlServer,
        DatabaseChoice.PostgreSql => DatabaseProvider.PostgreSql,
        DatabaseChoice.MySql => DatabaseProvider.MySql,
        DatabaseChoice.Sqlite => DatabaseProvider.Sqlite,
        _ => null,
    };
}

public sealed class OutboxSettings
{
    public double RetryIntervalSeconds { get; set; } = 30;
    public double BackoffFactor { get; set; } = 2;
    public double MaxRetryMinutes { get; set; } = 10;
    public double AttemptTimeoutSeconds { get; set; } = 10;
}

public sealed class MqttSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8883;
    public bool UseTls { get; set; } = true;
    public string? Username { get; set; }

    [JsonIgnore] public string? Password { get; set; }

    [JsonPropertyName("password")] public string? ProtectedPassword { get; set; }

    public string ClientId { get; set; } = "testumgebung-" + Environment.MachineName.ToLowerInvariant();
    public string Topic { get; set; } = "testumgebung/{station}/runs";
    public string? CaCertificatePath { get; set; }
    public double TimeoutSeconds { get; set; } = 5;
}

/// <summary>Meldeweg HTTP (Webhook, POST mit JSON).</summary>
public sealed class HttpNotifySettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string? HeaderName { get; set; }

    [JsonIgnore] public string? HeaderValue { get; set; }

    [JsonPropertyName("headerValue")] public string? ProtectedHeaderValue { get; set; }

    public double TimeoutSeconds { get; set; } = 5;
}

/// <summary>Meldeweg OPC UA (Schreiben in Knoten eines OPC-UA-Servers).</summary>
public sealed class OpcUaSettings
{
    public bool Enabled { get; set; }
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:4840";
    public string NodePrefix { get; set; } = "ns=2;s=Testumgebung.";
    public bool UseSecurity { get; set; }
    public string? Username { get; set; }

    [JsonIgnore] public string? Password { get; set; }

    [JsonPropertyName("password")] public string? ProtectedPassword { get; set; }

    public double TimeoutSeconds { get; set; } = 5;
}

/// <summary>Einstellungen der Anwendung (<c>%LOCALAPPDATA%\Testumgebung\einstellungen.json</c>).</summary>
public sealed class AppSettings
{
    public int Format { get; set; } = 1;
    public bool ShowPairedStations { get; set; }
    public string? FirstPairedStation { get; set; }
    public string? SecondPairedStation { get; set; }
    public string? ResultFolder { get; set; }
    public DatabaseSettings Database { get; set; } = new();
    public OutboxSettings Outbox { get; set; } = new();
    public MqttSettings Mqtt { get; set; } = new();
    public HttpNotifySettings Http { get; set; } = new();
    public OpcUaSettings OpcUa { get; set; } = new();

    public AppSettings Clone(ISecretProtector protector)
    {
        var store = new SettingsStore("", protector);
        return store.Deserialize(store.Serialize(this));
    }
}

/// <summary>Schutz für Passwörter und Verbindungsstrings (Windows: DPAPI, benutzergebunden).</summary>
public interface ISecretProtector
{
    string Protect(string plain);

    /// <summary>null, wenn der Wert nicht entschlüsselt werden kann (anderer Benutzer/Rechner).</summary>
    string? Unprotect(string stored);
}

/// <summary>Ohne Plattformschutz: Klartext mit Kennzeichnung (Tests, Linux).</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    public static readonly PlainSecretProtector Instance = new();
    public string Protect(string plain) => "klar:" + plain;
    public string? Unprotect(string stored) => stored.StartsWith("klar:", StringComparison.Ordinal) ? stored[5..] : stored;
}

public sealed class SettingsStore(string path, ISecretProtector protector)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "einstellungen.json");

    /// <summary>Hinweise beim Laden (z. B. nicht entschlüsselbares Passwort).</summary>
    public List<string> Warnings { get; } = [];

    public AppSettings Load(AppSettings? defaults = null)
    {
        Warnings.Clear();
        if (!File.Exists(Path)) return defaults ?? new AppSettings();
        try
        {
            return Deserialize(File.ReadAllText(Path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Warnings.Add($"Einstellungen nicht lesbar ({ex.Message}) – Standardwerte verwendet.");
            return defaults ?? new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, Serialize(settings));
        File.Move(temp, Path, overwrite: true);
    }

    public string Serialize(AppSettings s)
    {
        s.Database.ProtectedConnectionString = string.IsNullOrEmpty(s.Database.ConnectionString) ? null : protector.Protect(s.Database.ConnectionString);
        s.Mqtt.ProtectedPassword = string.IsNullOrEmpty(s.Mqtt.Password) ? null : protector.Protect(s.Mqtt.Password);
        s.Http.ProtectedHeaderValue = string.IsNullOrEmpty(s.Http.HeaderValue) ? null : protector.Protect(s.Http.HeaderValue);
        s.OpcUa.ProtectedPassword = string.IsNullOrEmpty(s.OpcUa.Password) ? null : protector.Protect(s.OpcUa.Password);
        return JsonSerializer.Serialize(s, Json);
    }

    public AppSettings Deserialize(string json)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
        s.Database ??= new DatabaseSettings();
        s.Outbox ??= new OutboxSettings();
        s.Mqtt ??= new MqttSettings();
        s.Http ??= new HttpNotifySettings();
        s.OpcUa ??= new OpcUaSettings();
        RestoreSecrets(s);
        return s;
    }
    private void RestoreSecrets(AppSettings s)
    {
        if (s.Http.ProtectedHeaderValue is { Length: > 0 } hv)
        {
            s.Http.HeaderValue = protector.Unprotect(hv);
            if (s.Http.HeaderValue is null) Warnings.Add("HTTP-Kopfwert nicht entschlüsselbar – bitte neu eingeben.");
        }
        if (s.OpcUa.ProtectedPassword is { Length: > 0 } opw)
        {
            s.OpcUa.Password = protector.Unprotect(opw);
            if (s.OpcUa.Password is null) Warnings.Add("OPC-UA-Passwort nicht entschlüsselbar – bitte neu eingeben.");
        }
        if (s.Database.ProtectedConnectionString is { Length: > 0 } cs)
        {
            s.Database.ConnectionString = protector.Unprotect(cs) ?? "";
            if (s.Database.ConnectionString.Length == 0) Warnings.Add("Verbindungsstring nicht entschlüsselbar (anderer Benutzer?) – bitte neu eingeben.");
        }
        if (s.Mqtt.ProtectedPassword is { Length: > 0 } pw)
        {
            s.Mqtt.Password = protector.Unprotect(pw);
            if (s.Mqtt.Password is null) Warnings.Add("MQTT-Passwort nicht entschlüsselbar – bitte neu eingeben.");
        }
    }

}
