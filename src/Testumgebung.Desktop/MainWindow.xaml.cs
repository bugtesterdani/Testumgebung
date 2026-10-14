/**
 * @file
 * @brief Verdrahtet Anwendungsstart und Hauptfenster; Prüfablauf und Werkerzustand werden von ViewModels verwaltet.
 */

using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using Testumgebung.Hardware;
using Testumgebung.Identity;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.Settings;
using Testumgebung.Presentation.ViewModels;
using Testumgebung.Workers;

namespace Testumgebung.Desktop;

public partial class MainWindow : Window
{
    private readonly Func<string, MessageBoxButton, MessageBoxImage, MessageBoxResult> _showMessage;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CodeRuntime _codeRuntime;
    private readonly ResultServices _services;
    private readonly ReleaseHub _releases;
    private readonly DispatcherTimer _inactivity = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly StationCoordinator _coordinator;

    public MainWindow() : this(null) { }

    internal MainWindow(MainWindowStartup? startup)
    {
        InitializeComponent();
        _showMessage = startup?.ShowMessage ?? ((text, buttons, icon) => MessageBox.Show(text, "Testumgebung", buttons, icon));
        // Explizite Umgebung, gespeicherte externe Kopie, sonst mitgelieferte Laufzeit.
        _codeRuntime = new CodeRuntime(new CodeRuntimeOptions
        {
            PythonExecutable = Environment.GetEnvironmentVariable("TESTUMGEBUNG_PYTHON") is { Length: > 0 } py ? py : new PythonEnvironments().LoadSelection(),
        });

        var (store, services) = LoadResultServices(startup);
        _services = services;

        // Zugang je Prüfplatz: %ProgramData%\Testumgebung\zugang.json (Entra-App, Rechte je Konto).
        // Entwicklungsmodus muss ausdrücklich beim Start eingeschaltet werden.
        var accessStore = startup?.AccessStore ?? new AccessConfigStore(AccessConfigStore.DefaultPath);
        var access = accessStore.Load();
        IIdentityProvider Provider(AccessConfig? c) => c is { UsesEntra: true }
            ? new MsalIdentityProvider(c.ClientId!, c.TenantId!, () => new WindowInteropHelper(this).Handle)
            : new SimulatedIdentityProvider(() => Shell?.LoginName ?? "");
        var arguments = startup?.Arguments ?? Environment.GetCommandLineArgs();
        var developmentMode = arguments.Contains("--development", StringComparer.OrdinalIgnoreCase);
        var exampleMode = developmentMode && arguments.Contains("--example-order", StringComparer.OrdinalIgnoreCase);
        var identity = new IdentityServices(new SessionService(Provider(access), access, allowDevelopmentMode: developmentMode), accessStore, Provider);

        // Release-Pakete und Audit-Log: in der Prüfdatenbank (falls SQL-Datenbank eingestellt), sonst lokal.
        _releases = new ReleaseHub(new ReleaseService(_services.ReleaseStore) { CodeRuntime = _codeRuntime, RequireRuntimeSnapshot = true });
        _services.Applied += () => _ = _releases.RefreshAsync();

        // Prüfplatzkonfiguration des Rechners: %ProgramData%\Testumgebung\pruefplaetze.json (Geräte, Prüfplätze, Zuordnung).
        // Ohne Datei: Vorschlag aus dem Projekt. Beschädigt: Anzeige, kein Prüfplatz startet bis zur Korrektur.
        var stationStore = startup?.StationStore ?? new StationSetupStore(StationSetupStore.DefaultPath);
        var (exampleProject, stationSetup, stationError) = LoadStationSetup(startup, stationStore, exampleMode);
        var coordinator = _coordinator = new StationCoordinator(stationSetup ?? StationSetup.Default(SampleProjects.AcPruefung()), new ResourceManager())
        {
            IsSuggestion = stationSetup is null && stationError is null,
            LoadError = stationError,
        };

        Shell = new ShellViewModel(SynchronizationContextDispatcher.FromCurrent(), new FileDialogService(), project: exampleProject, codeRuntime: _codeRuntime,
            services: _services, settingsStore: store, identity: identity, releases: _releases,
            stations: coordinator, stationStore: exampleMode ? null : stationStore);
        ConfigureExampleMode(exampleMode);
        DataContext = Shell;
        _ = _releases.RefreshAsync();
        _releases.StartPolling(TimeSpan.FromSeconds(30)); // Freigaben anderer Prüfplätze (gemeinsame Datenbank)

        // Optionale Inaktivitätssperre: Eingaben merken, regelmäßig prüfen. Ein laufender Test wird nie beeinflusst.
        PreviewMouseDown += (_, _) => Shell.NoteActivity();
        PreviewKeyDown += (_, _) => Shell.NoteActivity();
        _inactivity.Tick += (_, _) => Shell.CheckInactivity();
        _inactivity.Start();
        // Manuelles Öffnen der Anwendung: offene Runs aus der lokalen Outbox laden und Übertragung fortsetzen (P04).
        _services.Outbox.Start();
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();
    }

    private static (TestProject? Project, StationSetup? Setup, string? Error) LoadStationSetup(MainWindowStartup? startup, StationSetupStore stationStore, bool exampleMode)
    {
        TestProject? exampleProject = null;
        StationSetup? stationSetup = null;
        string? stationError = null;
        try
        {
            if (exampleMode)
            {
                var example = OrderExample.Load(startup?.ExamplesDirectory ?? System.IO.Path.Combine(AppContext.BaseDirectory, "docs", "beispiele"));
                exampleProject = example.Project;
                stationSetup = example.Setup;
            }
            else stationSetup = stationStore.Load();
        }
        catch (StationSetupException ex)
        {
            stationError = ex.Message;
        }
        return (exampleProject, stationSetup, stationError);
    }

    private static (SettingsStore Store, ResultServices Services) LoadResultServices(MainWindowStartup? startup)
    {
        // Einstellungen: %LOCALAPPDATA%\Testumgebung\einstellungen.json (Geheimnisse per DPAPI geschützt).
        // Ohne Datei gelten die Standardwerte; TESTUMGEBUNG_ERGEBNISSE und TESTUMGEBUNG_DB=aus wirken nur dann.
        var store = startup?.SettingsStore ?? new SettingsStore(SettingsStore.DefaultPath, new DpapiSecretProtector());
        var defaults = new AppSettings { ResultFolder = Env("TESTUMGEBUNG_ERGEBNISSE") };
        if (string.Equals(Env("TESTUMGEBUNG_DB"), "aus", StringComparison.OrdinalIgnoreCase)) defaults.Database.Provider = DatabaseChoice.None;
        var services = startup?.CreateResultServices is { } createServices ? createServices(store.Load(defaults))
            : new ResultServices(store.Load(defaults), new DatabaseProviderRegistry());

        return (store, services);
    }

    private void ConfigureExampleMode(bool exampleMode)
    {
        if (exampleMode)
        {
            Title = "Testumgebung – Programme A, B, C und D (Simulation / Entwurf)";
            Shell.SelectedStation.UseDraft = true;
            Shell.SelectedStation.Selection.ScanText = "A123";
        }
    }

    public ShellViewModel Shell { get; }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private void UpdateClock()
        => ClockText.Text = DateTime.Now.ToString("ddd, dd.MM.yyyy   HH:mm", CultureInfo.GetCultureInfo("de-DE"));

    private void ShowLicenses(object sender, RoutedEventArgs e) => LicenseInformation.Show(this);

    private void ClosePopup(object sender, RoutedEventArgs e) => UserToggle.IsChecked = false;

    private bool _closeReady;
    private bool _closing;
    private bool _faultShutdown;

    public void CloseAfterFault()
    {
        _faultShutdown = true;
        IsEnabled = false;
        foreach (var station in Shell.Stations) station.RequestAbort();
        Close();
    }

    private bool CanClose()
    {
        // Alle abbrechbaren Dialoge vor der ersten Änderung am laufenden Prüfablauf.
        if (!_faultShutdown && Shell.Editor.IsDirty)
        {
            var save = _showMessage("Ungespeicherte Änderungen am Projekt speichern?",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (save == MessageBoxResult.Cancel) return false;
            if (save == MessageBoxResult.Yes && !Shell.Editor.Save(Shell.Editor.FilePath)) return false;
        }
        if (!_faultShutdown && Shell.Stations.Any(s => s.IsRunning))
        {
            var answer = _showMessage(
                "Auf mindestens einem Prüfplatz läuft eine Prüfung. Trotzdem beenden?\n\nDer Software-Abbruch ersetzt keine hardwareseitige Abschaltung.",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return false;
            }
        }
        return true;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeReady) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_closing) return;
        if (!CanClose()) return;
        _closing = true;
        IsEnabled = false;
        Title = "Testumgebung – Abschluss und Speicherung …";
        try
        {
            foreach (var s in Shell.Stations) s.RequestAbort();
            await Task.WhenAll(Shell.Stations.Select(s => s.WaitForCompletionAsync()));
            var safeStateErrors = await _coordinator.SafeStateAllAsync();
            if (safeStateErrors.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, safeStateErrors));
            _codeRuntime.Dispose();
            _coordinator.Pool.Reset();
            await SimulatedLabHost.StopAsync();
            await _services.DisposeAsync();
            await _releases.DisposeAsync();
            _clock.Stop();
            _inactivity.Stop();
            _closeReady = true;
            Close();
        }
        catch (Exception ex)
        {
            FaultLog.Write(ex);
            IsEnabled = !_faultShutdown;
            Title = "Testumgebung";
            _closing = false;
            _showMessage("Der Abschluss konnte nicht vollständig bestätigt werden: " + ex.Message,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

internal sealed class MainWindowStartup
{
    public SettingsStore? SettingsStore { get; init; }
    public AccessConfigStore? AccessStore { get; init; }
    public StationSetupStore? StationStore { get; init; }
    public Func<AppSettings, ResultServices>? CreateResultServices { get; init; }
    public IReadOnlyList<string>? Arguments { get; init; }
    public string? ExamplesDirectory { get; init; }
    public Func<string, MessageBoxButton, MessageBoxImage, MessageBoxResult>? ShowMessage { get; init; }
}
