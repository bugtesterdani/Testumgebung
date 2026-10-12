using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Notifications;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.Presentation.ViewModels;

public sealed record ChoiceItem<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>Zeile der Vertragsprüfung bzw. eines MQTT-Sendeversuchs.</summary>
public sealed record CheckRow(bool Passed, string Name, string Detail, string Time = "");

/// <summary>
/// Entwicklerseite „Einstellungen“: Ergebnisordner, Datenbank (vier Provider oder simuliert), Outbox-Parameter und MQTT.
/// Speichern übernimmt die Werte sofort (ohne Neustart) und schreibt die Einstellungsdatei; Geheimnisse werden geschützt abgelegt.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ResultServices _services;
    private readonly SettingsStore _store;
    private readonly IUiDispatcher _ui;
    private AppSettings _edit;
    private string? _databaseStatus;
    private string? _mqttStatus;
    private string? _saveStatus;
    private bool _isBusy;
    private readonly Func<AppSettings, string?>? _validate;
    private readonly Func<IReadOnlyList<string>> _stationNames;

    public SettingsViewModel(ResultServices services, SettingsStore store, IUiDispatcher ui,
        Func<AppSettings, string?>? validate = null, Func<IReadOnlyList<string>>? stationNames = null)
    {
        _services = services;
        _store = store;
        _ui = ui;
        _validate = validate;
        _stationNames = stationNames ?? (() => []);
        _edit = Copy(services.Current);
        SaveCommand = new RelayCommand(Save);
        RevertCommand = new RelayCommand(Revert);
        TestDatabaseCommand = new RelayCommand(() => _ = TestDatabaseAsync(), () => !IsBusy && IsSqlProvider);
        ContractCheckCommand = new RelayCommand(() => _ = ContractCheckAsync(), () => !IsBusy && IsSqlProvider);
        TestMqttCommand = new RelayCommand(() => _ = TestMqttAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(MqttHost));
        services.MqttDelivered += d => _ui.Post(() =>
        {
            MqttLog.Insert(0, new CheckRow(d.Sent, $"{MqttNotifier.EventName(d.Notification.Kind)} · {d.Notification.DutId}",
                d.Sent ? d.Topic : d.Error ?? "", d.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
            while (MqttLog.Count > 100) MqttLog.RemoveAt(MqttLog.Count - 1);
        });
        TestHttpCommand = new RelayCommand(() => _ = TestHttpAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(HttpUrl));
        TestOpcUaCommand = new RelayCommand(() => _ = TestOpcUaAsync(), () => !IsBusy && OpcUaAvailable && !string.IsNullOrWhiteSpace(OpcUaEndpoint));
        services.NotifyDelivered += d => _ui.Post(() =>
        {
            NotifyLog.Insert(0, new CheckRow(d.Sent, $"{d.Channel} · {MqttNotifier.EventName(d.Notification.Kind)} · {d.Notification.DutId}",
                d.Sent ? d.Target : d.Error ?? "", d.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
            while (NotifyLog.Count > 100) NotifyLog.RemoveAt(NotifyLog.Count - 1);
        });
        foreach (var w in store.Warnings) Warnings.Add(w);
    }

    public IReadOnlyList<string> StationNames => _stationNames();
    public void RefreshStationNames() => OnPropertyChanged(nameof(StationNames));
    public bool ShowPairedStations
    {
        get => _edit.ShowPairedStations;
        set { _edit.ShowPairedStations = value; OnPropertyChanged(); }
    }
    public string? FirstPairedStation
    {
        get => _edit.FirstPairedStation;
        set { _edit.FirstPairedStation = value; OnPropertyChanged(); }
    }
    public string? SecondPairedStation
    {
        get => _edit.SecondPairedStation;
        set { _edit.SecondPairedStation = value; OnPropertyChanged(); }
    }

    // ------------------------------------------------------------------ Meldeweg HTTP

    private string? _httpStatus;
    private string? _opcUaStatus;

    public RelayCommand TestHttpCommand { get; }
    public RelayCommand TestOpcUaCommand { get; }
    public ObservableCollection<CheckRow> NotifyLog { get; } = [];

    public bool HttpEnabled
    {
        get => _edit.Http.Enabled;
        set { _edit.Http.Enabled = value; OnPropertyChanged(); }
    }

    public string HttpUrl
    {
        get => _edit.Http.Url;
        set { _edit.Http.Url = value?.Trim() ?? ""; OnPropertyChanged(); TestHttpCommand.RaiseCanExecuteChanged(); }
    }

    public string HttpHeaderName
    {
        get => _edit.Http.HeaderName ?? "";
        set { _edit.Http.HeaderName = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); OnPropertyChanged(); }
    }

    /// <summary>Kopfwert (z. B. „Bearer …“) – geschützt gespeichert, nicht angezeigt.</summary>
    public string HttpHeaderValue
    {
        get => _edit.Http.HeaderValue ?? "";
        set { _edit.Http.HeaderValue = string.IsNullOrEmpty(value) ? null : value; OnPropertyChanged(); OnPropertyChanged(nameof(HasHttpHeaderValue)); }
    }

    public bool HasHttpHeaderValue => !string.IsNullOrEmpty(_edit.Http.HeaderValue);

    public double HttpTimeoutSeconds
    {
        get => _edit.Http.TimeoutSeconds;
        set { _edit.Http.TimeoutSeconds = Math.Clamp(value, 1, 60); OnPropertyChanged(); }
    }

    public string? HttpStatus
    {
        get => _httpStatus;
        private set => SetProperty(ref _httpStatus, value);
    }

    public async Task TestHttpAsync()
    {
        var options = ResultServices.HttpOptionsOf(_edit.Http);
        if (options.Validate() is { } error)
        {
            HttpStatus = error;
            return;
        }
        IsBusy = true;
        HttpStatus = "Sende Testmeldung …";
        var notifier = new HttpNotifier(options);
        try
        {
            var text = await notifier.SendTestAsync(CancellationToken.None).ConfigureAwait(false);
            _ui.Post(() => HttpStatus = text);
        }
        catch (Exception ex)
        {
            _ui.Post(() => HttpStatus = "Fehler: " + Describe(ex));
        }
        finally
        {
            await notifier.DisposeAsync().ConfigureAwait(false);
            _ui.Post(() => IsBusy = false);
        }
    }

    // ------------------------------------------------------------------ Meldeweg OPC UA

    public bool OpcUaAvailable => ResultServices.OpcUaFactory is not null;

    public bool OpcUaEnabled
    {
        get => _edit.OpcUa.Enabled;
        set { _edit.OpcUa.Enabled = value; OnPropertyChanged(); }
    }

    public string OpcUaEndpoint
    {
        get => _edit.OpcUa.EndpointUrl;
        set { _edit.OpcUa.EndpointUrl = value?.Trim() ?? ""; OnPropertyChanged(); TestOpcUaCommand.RaiseCanExecuteChanged(); }
    }

    public string OpcUaNodePrefix
    {
        get => _edit.OpcUa.NodePrefix;
        set { _edit.OpcUa.NodePrefix = value?.Trim() ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(OpcUaNodesHint)); }
    }

    public string OpcUaNodesHint => "Geschrieben werden (als Zeichenkette): " + string.Join(", ", OpcUaNotifyOptions.FieldNames.Select(f => _edit.OpcUa.NodePrefix + f));

    public bool OpcUaUseSecurity
    {
        get => _edit.OpcUa.UseSecurity;
        set { _edit.OpcUa.UseSecurity = value; OnPropertyChanged(); }
    }

    public string OpcUaUsername
    {
        get => _edit.OpcUa.Username ?? "";
        set { _edit.OpcUa.Username = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); }
    }

    public string OpcUaPassword
    {
        get => _edit.OpcUa.Password ?? "";
        set { _edit.OpcUa.Password = string.IsNullOrEmpty(value) ? null : value; OnPropertyChanged(); OnPropertyChanged(nameof(HasOpcUaPassword)); }
    }

    public bool HasOpcUaPassword => !string.IsNullOrEmpty(_edit.OpcUa.Password);

    public double OpcUaTimeoutSeconds
    {
        get => _edit.OpcUa.TimeoutSeconds;
        set { _edit.OpcUa.TimeoutSeconds = Math.Clamp(value, 1, 60); OnPropertyChanged(); }
    }

    public string? OpcUaStatus
    {
        get => _opcUaStatus;
        private set => SetProperty(ref _opcUaStatus, value);
    }

    public async Task TestOpcUaAsync()
    {
        var options = ResultServices.OpcUaOptionsOf(_edit.OpcUa);
        if (options.Validate() is { } error || ResultServices.OpcUaFactory is not { } factory)
        {
            OpcUaStatus = options.Validate() ?? "OPC UA ist in dieser Ausgabe nicht verfügbar.";
            return;
        }
        IsBusy = true;
        OpcUaStatus = "Sende Testmeldung …";
        var notifier = factory(options);
        try
        {
            var text = await notifier.SendTestAsync(CancellationToken.None).ConfigureAwait(false);
            _ui.Post(() => OpcUaStatus = text);
        }
        catch (Exception ex)
        {
            _ui.Post(() => OpcUaStatus = "Fehler: " + Describe(ex));
        }
        finally
        {
            await notifier.DisposeAsync().ConfigureAwait(false);
            _ui.Post(() => IsBusy = false);
        }
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand TestDatabaseCommand { get; }
    public RelayCommand ContractCheckCommand { get; }
    public RelayCommand TestMqttCommand { get; }

    public ObservableCollection<CheckRow> ContractResult { get; } = [];
    public ObservableCollection<CheckRow> MqttLog { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    public string SettingsPath => _store.Path;

    public static IReadOnlyList<ChoiceItem<DatabaseChoice>> ProviderChoices { get; } =
    [
        new(DatabaseChoice.None, "Keine Datenbank"),
        new(DatabaseChoice.Simulated, "Simuliert (Ordner, ohne Server)"),
        new(DatabaseChoice.SqlServer, "Microsoft SQL Server"),
        new(DatabaseChoice.PostgreSql, "PostgreSQL"),
        new(DatabaseChoice.MySql, "MySQL / MariaDB"),
        new(DatabaseChoice.Sqlite, "SQLite (Datei)"),
    ];

    /// <summary>Instanzzugriff für die Bindung.</summary>
    public IReadOnlyList<ChoiceItem<DatabaseChoice>> Providers => ProviderChoices;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            TestDatabaseCommand.RaiseCanExecuteChanged();
            ContractCheckCommand.RaiseCanExecuteChanged();
            TestMqttCommand.RaiseCanExecuteChanged();
            TestHttpCommand?.RaiseCanExecuteChanged();
            TestOpcUaCommand?.RaiseCanExecuteChanged();
        }
    }

    // ------------------------------------------------------------------ Ergebnisdateien

    public string ResultFolder
    {
        get => _edit.ResultFolder ?? "";
        set { _edit.ResultFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); OnPropertyChanged(); }
    }

    public string ResultFolderHint => $"Leer = {Testumgebung.Persistence.Results.ResultFileStore.DefaultFolder}";

    // ------------------------------------------------------------------ Datenbank

    public ChoiceItem<DatabaseChoice> SelectedProvider
    {
        get => ProviderChoices.First(c => c.Value == _edit.Database.Provider);
        set
        {
            if (value is null || value.Value == _edit.Database.Provider) return;
            _edit.Database.Provider = value.Value;
            OnPropertiesChanged(nameof(SelectedProvider), nameof(IsSqlProvider), nameof(ConnectionExample));
            TestDatabaseCommand.RaiseCanExecuteChanged();
            ContractCheckCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsSqlProvider => DatabaseSettings.ProviderOf(_edit.Database.Provider) is not null;

    public string ConnectionString
    {
        get => _edit.Database.ConnectionString;
        set { _edit.Database.ConnectionString = value ?? ""; OnPropertyChanged(); }
    }

    public string ConnectionExample => _edit.Database.Provider switch
    {
        DatabaseChoice.SqlServer => @"Server=sql01;Database=Pruefung;Integrated Security=true;TrustServerCertificate=true   (LocalDB: Server=(localdb)\MSSQLLocalDB;Database=Pruefung;Integrated Security=true)",
        DatabaseChoice.PostgreSql => "Host=db01;Port=5432;Database=pruefung;Username=testumgebung;Password=…",
        DatabaseChoice.MySql => "Server=db01;Port=3306;Database=pruefung;User ID=testumgebung;Password=…",
        DatabaseChoice.Sqlite => @"Data Source=C:\Pruefdaten\pruefung.db",
        DatabaseChoice.Simulated => "Ordner: " + _services.SimulatedDatabaseFolder,
        _ => "Ohne Datenbank werden nur Ergebnisdateien geschrieben.",
    };

    public string? DatabaseStatus
    {
        get => _databaseStatus;
        private set => SetProperty(ref _databaseStatus, value);
    }

    // ------------------------------------------------------------------ Outbox

    public double RetryIntervalSeconds
    {
        get => _edit.Outbox.RetryIntervalSeconds;
        set { _edit.Outbox.RetryIntervalSeconds = Math.Max(1, value); OnPropertyChanged(); }
    }

    public double BackoffFactor
    {
        get => _edit.Outbox.BackoffFactor;
        set { _edit.Outbox.BackoffFactor = Math.Max(1, value); OnPropertyChanged(); }
    }

    public double MaxRetryMinutes
    {
        get => _edit.Outbox.MaxRetryMinutes;
        set { _edit.Outbox.MaxRetryMinutes = Math.Max(0.1, value); OnPropertyChanged(); }
    }

    public double AttemptTimeoutSeconds
    {
        get => _edit.Outbox.AttemptTimeoutSeconds;
        set { _edit.Outbox.AttemptTimeoutSeconds = Math.Max(1, value); OnPropertyChanged(); }
    }

    // ------------------------------------------------------------------ MQTT

    public bool MqttEnabled
    {
        get => _edit.Mqtt.Enabled;
        set { _edit.Mqtt.Enabled = value; OnPropertyChanged(); }
    }

    public string MqttHost
    {
        get => _edit.Mqtt.Host;
        set { _edit.Mqtt.Host = value?.Trim() ?? ""; OnPropertyChanged(); TestMqttCommand.RaiseCanExecuteChanged(); }
    }

    public int MqttPort
    {
        get => _edit.Mqtt.Port;
        set { _edit.Mqtt.Port = Math.Clamp(value, 1, 65535); OnPropertyChanged(); }
    }

    public bool MqttUseTls
    {
        get => _edit.Mqtt.UseTls;
        set
        {
            if (_edit.Mqtt.UseTls == value) return;
            _edit.Mqtt.UseTls = value;
            if (value && _edit.Mqtt.Port == 1883) MqttPort = 8883;
            if (!value && _edit.Mqtt.Port == 8883) MqttPort = 1883;
            OnPropertyChanged();
        }
    }

    public string MqttUsername
    {
        get => _edit.Mqtt.Username ?? "";
        set { _edit.Mqtt.Username = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); }
    }

    /// <summary>Passwort (die Oberfläche nutzt eine PasswordBox; gespeichert geschützt).</summary>
    public string MqttPassword
    {
        get => _edit.Mqtt.Password ?? "";
        set { _edit.Mqtt.Password = string.IsNullOrEmpty(value) ? null : value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMqttPassword)); }
    }

    public bool HasMqttPassword => !string.IsNullOrEmpty(_edit.Mqtt.Password);

    public string MqttClientId
    {
        get => _edit.Mqtt.ClientId;
        set { _edit.Mqtt.ClientId = value ?? ""; OnPropertyChanged(); }
    }

    public string MqttTopic
    {
        get => _edit.Mqtt.Topic;
        set { _edit.Mqtt.Topic = value ?? ""; OnPropertyChanged(); }
    }

    public string MqttCaCertificatePath
    {
        get => _edit.Mqtt.CaCertificatePath ?? "";
        set { _edit.Mqtt.CaCertificatePath = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); OnPropertyChanged(); }
    }

    public double MqttTimeoutSeconds
    {
        get => _edit.Mqtt.TimeoutSeconds;
        set { _edit.Mqtt.TimeoutSeconds = Math.Clamp(value, 1, 60); OnPropertyChanged(); }
    }

    public string? MqttStatus
    {
        get => _mqttStatus;
        private set => SetProperty(ref _mqttStatus, value);
    }

    public string? SaveStatus
    {
        get => _saveStatus;
        private set => SetProperty(ref _saveStatus, value);
    }

    // ------------------------------------------------------------------ Aktionen

    public void Save()
    {
        try
        {
            var applied = Copy(_edit);
            if (_validate?.Invoke(applied) is { } refusal)
            {
                SaveStatus = "Nicht gespeichert: " + refusal;
                return;
            }
            _services.Apply(applied);
            _store.Save(Copy(applied));
            SaveStatus = _services.ApplyWarning is { } w
                ? $"Gespeichert, aber: {w}"
                : $"Gespeichert und übernommen ({DateTime.Now:HH:mm:ss}).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveStatus = "Speichern fehlgeschlagen: " + ex.Message;
        }
    }

    public void Revert()
    {
        _edit = Copy(_services.Current);
        SaveStatus = "Änderungen verworfen.";
        OnPropertyChanged(string.Empty);
    }

    public async Task TestDatabaseAsync()
    {
        if (CreateEditedDatabase(out var error) is not { } db)
        {
            DatabaseStatus = error;
            return;
        }
        IsBusy = true;
        DatabaseStatus = "Verbinde …";
        try
        {
            var text = await Task.Run(() => db.TestConnectionAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            _ui.Post(() => DatabaseStatus = text);
        }
        catch (Exception ex)
        {
            _ui.Post(() => DatabaseStatus = "Fehler: " + Describe(ex));
        }
        finally
        {
            _ui.Post(() => IsBusy = false);
        }
    }

    public async Task ContractCheckAsync()
    {
        if (CreateEditedDatabase(out var error) is not { } db)
        {
            DatabaseStatus = error;
            return;
        }
        IsBusy = true;
        ContractResult.Clear();
        DatabaseStatus = "Vertragsprüfung läuft …";
        try
        {
            var result = await Task.Run(() => DatabaseContractCheck.RunAsync(db).WaitAsync(TimeSpan.FromMinutes(2))).ConfigureAwait(false);
            _ui.Post(() =>
            {
                foreach (var s in result.Steps) ContractResult.Add(new CheckRow(s.Passed, s.Name, s.Detail));
                DatabaseStatus = result.Passed ? "Vertragsprüfung bestanden." : "Vertragsprüfung NICHT bestanden.";
            });
        }
        catch (Exception ex)
        {
            _ui.Post(() => DatabaseStatus = "Fehler: " + Describe(ex));
        }
        finally
        {
            _ui.Post(() => IsBusy = false);
        }
    }

    public async Task TestMqttAsync()
    {
        IsBusy = true;
        MqttStatus = "Sende Testmeldung …";
        var notifier = new MqttNotifier(ResultServices.MqttOptionsOf(_edit.Mqtt));
        try
        {
            var text = await notifier.SendTestAsync(CancellationToken.None).ConfigureAwait(false);
            _ui.Post(() => MqttStatus = text);
        }
        catch (Exception ex)
        {
            _ui.Post(() => MqttStatus = "Fehler: " + Describe(ex));
        }
        finally
        {
            await notifier.DisposeAsync().ConfigureAwait(false);
            _ui.Post(() => IsBusy = false);
        }
    }

    private SqlRunDatabase? CreateEditedDatabase(out string? error)
    {
        var db = _services.CreateDatabase(_edit.Database, out error);
        if (db is SqlRunDatabase sql) return sql;
        error ??= "Für diese Auswahl gibt es keine Verbindung zu prüfen.";
        return null;
    }

    private static string Describe(Exception ex)
    {
        var e = ex is AggregateException { InnerException: { } inner } ? inner : ex;
        return e is TimeoutException ? "Zeitüberschreitung." : e.Message;
    }

    private AppSettings Copy(AppSettings s) => s.Clone(PlainSecretProtector.Instance);
}
