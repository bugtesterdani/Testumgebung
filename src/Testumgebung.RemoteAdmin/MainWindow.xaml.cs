using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Desktop;
using Testumgebung.Identity;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.RemoteAdmin;

public partial class MainWindow : Window
{
    private const string ManageRole = "Testumgebung.Fernverwaltung";
    private readonly SettingsStore _preferences = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Testumgebung", "Fernverwaltung", "einstellungen.json"), new DpapiSecretProtector());
    private IIdentityProvider? _identityProvider;
    private UserIdentity? _user;
    private SqlRemoteSettingsStore? _store;
    private StationSnapshot? _selected;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        ProviderBox.ItemsSource = Enum.GetValues<DatabaseProvider>();
        var current = new SettingsStore(SettingsStore.DefaultPath, new DpapiSecretProtector()).Load();
        var settings = _preferences.Load(current).Database;
        ProviderBox.SelectedItem = DatabaseSettings.ProviderOf(settings.Provider) ?? DatabaseProvider.SqlServer;
        ConnectionBox.Password = settings.ConnectionString;
    }

    private async void Login(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var path = ApplicationOverrides.Value("TESTUMGEBUNG_REMOTE_ACCESS") ?? AccessConfigStore.DefaultPath;
        var config = new AccessConfigStore(path).Load();
        if (config is not { UsesEntra: true, LoadError: null })
        {
            StatusText.Text = "In der Zugangsdatei müssen Entra-Anwendungs-ID und Mandant eingerichtet sein.";
            return;
        }

        var provider = EntraIdentityProviderFactory.Create(config);
        var identity = await provider.SignInAsync(selectAccount: true, CancellationToken.None);
        if (!IsAllowed(identity, config.TenantId!))
        {
            await provider.SignOutAsync(identity, CancellationToken.None);
            StatusText.Text = $"Das Konto braucht die App-Rolle {ManageRole} im konfigurierten Entra-Mandanten.";
            return;
        }
        _identityProvider = provider;
        _user = identity;
        StatusText.Text = $"Angemeldet als {identity.DisplayName}.";
    });

    private static bool IsAllowed(UserIdentity identity, string tenant) => identity.IsConfirmed
        && string.Equals(identity.TenantId, tenant, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(identity.ObjectId)
        && identity.Roles?.Contains(ManageRole, StringComparer.OrdinalIgnoreCase) == true;

    private async void Logout(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_user is not { } identity) return;
        _user = null;
        _store = null;
        _selected = null;
        StationList.ItemsSource = null;
        HistoryList.ItemsSource = null;
        if (_identityProvider is { } provider) await provider.SignOutAsync(identity, CancellationToken.None);
        StatusText.Text = "Abgemeldet.";
    });

    private async void Refresh(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (ProviderBox.SelectedItem is not DatabaseProvider provider || string.IsNullOrWhiteSpace(ConnectionBox.Password))
        {
            StatusText.Text = "Datenbanktyp und Verbindungsstring eingeben.";
            return;
        }
        var factory = new DatabaseProviderRegistry().Factory(provider)!;
        var connectionString = ConnectionBox.Password;
        var store = new SqlRunDatabase(factory, connectionString, SqlDialect.For(provider)).RemoteSettings;
        _store = null;
        await LoadStationsAsync(store);
        _store = store;
        var choice = provider switch
        {
            DatabaseProvider.SqlServer => DatabaseChoice.SqlServer,
            DatabaseProvider.PostgreSql => DatabaseChoice.PostgreSql,
            DatabaseProvider.MySql => DatabaseChoice.MySql,
            _ => DatabaseChoice.Sqlite,
        };
        _preferences.Save(new AppSettings { Database = new DatabaseSettings { Provider = choice, ConnectionString = connectionString } });
    }, requireLogin: true);

    private async Task LoadStationsAsync(SqlRemoteSettingsStore store)
    {
        var list = await InDatabaseAsync(store.StationsAsync);
        var id = _selected?.Id;
        StationList.ItemsSource = list;
        var selected = list.FirstOrDefault(station => station.Id == id) ?? list.FirstOrDefault();
        StationList.SelectedItem = selected;
        await LoadStationAsync(store, selected);
        StatusText.Text = list.Count == 0 ? "In dieser Datenbank hat sich noch kein Prüfplatz gemeldet." : $"{list.Count} Prüfplatzrechner geladen.";
    }

    private async void SelectStation(object sender, SelectionChangedEventArgs e)
    {
        if (_busy || _store is not { } store) return;
        await RunAsync(() => LoadStationAsync(store, StationList.SelectedItem as StationSnapshot), requireLogin: true);
    }

    private async Task LoadStationAsync(SqlRemoteSettingsStore store, StationSnapshot? station)
    {
        _selected = station;
        if (station is null) { HistoryList.ItemsSource = null; return; }
        var settings = station.Settings;
        SeenText.Text = $"{station.Id} · Zuletzt gemeldet (UTC): {station.Seen}";
        PairedBox.IsChecked = settings.ShowPairedStations;
        var options = station.Stations.Prepend("").Distinct(StringComparer.Ordinal).ToArray();
        FirstBox.ItemsSource = options;
        SecondBox.ItemsSource = options;
        FirstBox.SelectedItem = settings.FirstPairedStation;
        SecondBox.SelectedItem = settings.SecondPairedStation;
        ErrorBox.Text = settings.ErrorStreak.ToString(CultureInfo.InvariantCulture);
        OutboxBox.Text = settings.OutboxThreshold.ToString(CultureInfo.InvariantCulture);
        FolderBox.IsChecked = settings.ResultFolderAlarm;
        CalibrationBox.IsChecked = settings.CalibrationAlarm;
        var history = await InDatabaseAsync(ct => store.HistoryAsync(station.Id, ct));
        HistoryList.ItemsSource = history.Select(Describe).ToList();
    }

    private async void Save(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_store is not { } store || _selected is not { } station || _user is not { } user) return;
        if (!int.TryParse(ErrorBox.Text, out var errors) || !int.TryParse(OutboxBox.Text, out var outbox))
        {
            StatusText.Text = "Für die Alarmschwellen ganze Zahlen eingeben.";
            return;
        }
        var desired = new RemoteStationSettings(PairedBox.IsChecked == true, FirstBox.SelectedItem as string ?? "",
            SecondBox.SelectedItem as string ?? "", errors, outbox, FolderBox.IsChecked == true, CalibrationBox.IsChecked == true);
        if (!desired.IsValid || !desired.MatchesStations(station.Stations))
        {
            StatusText.Text = "Alarmschwellen und Prüfplatzzuordnung prüfen.";
            return;
        }
        var actor = $"{user.Upn} ({user.ObjectId})";
        var queued = await InDatabaseAsync(ct => store.RequestAsync(station.Id, station.Version, actor, desired, ct));
        await LoadStationsAsync(store);
        StatusText.Text = queued ? "Änderung gespeichert. Der Prüfplatz übernimmt sie im Leerlauf."
            : "Keine Änderung angenommen. Der Stand wurde neu geladen.";
    }, requireLogin: true);

    private async Task RunAsync(Func<Task> action, bool requireLogin = false)
    {
        if (_busy || (requireLogin && _user is null)) return;
        _busy = true;
        UpdateControls();
        try { await action(); }
        catch (SignInCanceledException) { StatusText.Text = "Anmeldung abgebrochen."; }
        catch (SignInException ex) { StatusText.Text = ex.Message; }
        catch (Exception ex) when (ex is DbException or IOException or ArgumentException or OperationCanceledException or UnauthorizedAccessException)
        {
            StatusText.Text = "Verbindung oder Speicherung fehlgeschlagen. Datenbankzugang, Verbindung und Rechte prüfen.";
        }
        finally { _busy = false; UpdateControls(); }
    }

    private void UpdateControls()
    {
        LoginButton.IsEnabled = !_busy && _user is null;
        LogoutButton.IsEnabled = !_busy && _user is not null;
        Workspace.IsEnabled = !_busy && _user is not null;
        EditorPanel.IsEnabled = _store is not null && _selected is not null;
        SaveButton.IsEnabled = _selected is { Pending: false };
    }

    private static Task<T> InDatabaseAsync<T>(Func<CancellationToken, Task<T>> operation) => Task.Run(async () =>
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await operation(limit.Token);
    });

    private static string Describe(StationRevision revision)
    {
        var status = revision.Status switch
        {
            "applied" => "Übernommen",
            "conflict" => "Lokale Einstellungen geändert",
            "invalid" => "Am Prüfplatz ungültig",
            _ => "Wartet auf Übernahme",
        };
        return $"#{revision.Id} · {status} · {revision.Actor}\nAngefordert: {revision.Requested}\n"
            + $"Vorher: {DescribeSettings(revision.Before)}\nAngefordert: {DescribeSettings(revision.After)}"
            + (revision.Completed is null ? "" : $"\nRückmeldung: {revision.Completed}");
    }

    private static string DescribeSettings(RemoteStationSettings settings)
        => $"Anzeige {settings.ShowPairedStations}, {settings.FirstPairedStation} / {settings.SecondPairedStation} · "
         + $"ERROR {settings.ErrorStreak} · Outbox {settings.OutboxThreshold} · Ergebnisordner {settings.ResultFolderAlarm} · Kalibrierung {settings.CalibrationAlarm}";
}
