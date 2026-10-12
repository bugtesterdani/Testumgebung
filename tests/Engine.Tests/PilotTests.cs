using System.Data.Common;
using System.IO.Compression;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Hardware;
using Testumgebung.Identity;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.Pilot;
using Testumgebung.Presentation.Settings;
using Testumgebung.Presentation.ViewModels;
using Xunit;

namespace Testumgebung.Engine.Tests;

/// <summary>Pilot: Systemprüfung und Sicherung/Wiederherstellung der Rechnerkonfiguration.</summary>
public sealed class PilotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tu-pilot-" + Guid.NewGuid().ToString("N")[..8]);

    public PilotTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private sealed class NoProviders : IDatabaseProviderRegistry
    {
        public DbProviderFactory? Factory(DatabaseProvider provider) => null;
    }

    private sealed class Dialogs(string open, string save) : IFileDialogService
    {
        public string? OpenProject() => null;
        public string? SaveProject(string suggestedName) => null;
        public void ShowError(string message) { }
        public void OpenExternal(string path) { }
        public string? OpenFile(string title, string filter) => open;
        public string? SaveFile(string title, string filter, string suggestedName) => save;
    }

    [Fact]
    public async Task Systempruefung_meldet_Ordner_Konfiguration_Zugang_Laufzeiten_und_Treiber()
    {
        await using var services = new ResultServices(new AppSettings { ResultFolder = P("ergebnisse") }, new NoProviders(), P("outbox"), P("simdb"), recoveryFolder: P("w"));
        var project = SampleProjects.AcPruefung();
        var setup = StationSetup.Default(project);
        setup.Devices.Add(new DeviceConfig { Id = "dmm", Name = "DMM 34465A", Adapter = "Keysight 3446x DMM", Model = "34465A", Resource = "USB0::0x2A8D::0x0101::MY1::INSTR" });
        setup.Devices.Add(new DeviceConfig { Id = "daq", Name = "NI 9215", Adapter = "NI-DAQmx", Resource = "" });
        var coordinator = new StationCoordinator(setup, new ResourceManager());
        var ctx = new SystemCheckContext
        {
            Services = services,
            Coordinator = coordinator,
            StationStore = new StationSetupStore(P("pd/pruefplaetze.json")),
            AccessStore = new AccessConfigStore(P("pd/zugang.json")),
            PythonExecutable = "python-test",
            RunProcess = (file, _) => file == "python-test" ? "Python 3.12.4" : file == "g++" ? "g++ (GCC) 14.2" : null,
            VisaAvailable = () => false,
            DaqmxAvailable = () => false,
            MinimumFreeBytes = 0,
        };
        var items = await SystemCheck.RunAsync(ctx).Within();
        SystemCheckItem Item(string name) => items.Where(i => i.Name == name).ToList() is { Count: 1 } one ? one[0] : throw new InvalidOperationException(name + ": " + string.Join(" | ", items.Select(i => i.Name)));

        Assert.Equal(CheckLevel.Ok, Item("Ergebnisordner").Level);
        Assert.Equal(CheckLevel.Ok, Item("Zwischenaufzeichnung").Level);
        Assert.Equal(CheckLevel.Ok, Item("Prüfplatzkonfiguration").Level);
        Assert.Equal(CheckLevel.Error, Item("NI 9215").Level);                     // ohne Ressource
        Assert.Equal(CheckLevel.Error, Item("VISA").Level);                        // USB-Gerät, aber kein VISA
        Assert.Equal(CheckLevel.Error, Item("NI-DAQmx").Level);
        Assert.Equal(CheckLevel.Warning, Item("Anmeldung").Level);                 // Entwicklungsmodus
        Assert.Equal(CheckLevel.Ok, Item("Python").Level);
        Assert.Contains("3.12.4", Item("Python").Detail);
        Assert.Equal(CheckLevel.Warning, Item("Prüfdatenbank").Level);             // simulierte Datenbank
        Assert.Equal(CheckLevel.Ok, Item("Outbox").Level);
        if (!OperatingSystem.IsWindows()) Assert.Contains("g++", Item("C++-Toolchain").Detail);

        // Ohne reale Geräte: VISA/DAQmx nur Hinweis; Zugang mit Entra → OK
        new AccessConfigStore(P("pd/zugang.json")).Save(new AccessConfig { ClientId = "c", TenantId = "t" });
        var plain = await SystemCheck.RunAsync(new SystemCheckContext
        {
            Coordinator = new StationCoordinator(StationSetup.Default(project), new ResourceManager()) { IsSuggestion = true },
            AccessStore = new AccessConfigStore(P("pd/zugang.json")),
            RunProcess = (_, _) => null, VisaAvailable = () => false, DaqmxAvailable = () => false,
        }).Within();
        Assert.Equal(CheckLevel.Info, plain.Single(i => i.Name == "VISA").Level);
        Assert.Equal(CheckLevel.Warning, plain.Single(i => i.Name == "Prüfplatzkonfiguration").Level);
        Assert.Equal(CheckLevel.Ok, plain.Single(i => i.Name == "Anmeldung").Level);
        Assert.Equal(CheckLevel.Warning, plain.Single(i => i.Name == "Python").Level);
    }

    [Fact]
    public void Sicherung_und_Wiederherstellung_mit_Pruefsummen_und_Vorstand()
    {
        var settings = P("user/einstellungen.json");
        var access = P("pd/zugang.json");
        var stations = P("pd/pruefplaetze.json");
        Directory.CreateDirectory(P("user"));
        Directory.CreateDirectory(P("pd"));
        File.WriteAllText(settings, "{\"format\":1,\"database\":{\"protectedConnectionString\":\"dpapi:xyz\"}}");
        new StationSetupStore(stations).Save(StationSetup.Default(SampleProjects.AcPruefung()));
        var sources = ConfigBackup.DefaultSources(settings, access, stations);   // zugang.json fehlt

        var ui = new QueueDispatcher();
        var zip = P("sicherung.zip");
        var restoredCount = 0;
        var page = new SystemPageViewModel(() => new SystemCheckContext(), sources, new Dialogs(zip, zip), () => false, ui, () => restoredCount++);
        page.BackupCommand.Execute(null);
        Assert.Contains("einstellungen.json, pruefplaetze.json", page.BackupStatus);
        Assert.Contains("nicht vorhanden: zugang.json", page.BackupStatus);

        var inspection = ConfigBackup.Inspect(zip);
        Assert.True(inspection.IsIntact);
        Assert.True(inspection.Manifest.Files.Single(f => f.Name == "einstellungen.json").ContainsProtectedSecrets);

        // Änderung nach der Sicherung → Wiederherstellen setzt zurück und bewahrt den Vorstand
        File.WriteAllText(stations, "{\"format\":1,\"devices\":[],\"stations\":[]}");
        page.OpenBackupCommand.Execute(null);
        Assert.Contains("Prüfsummen in Ordnung", page.InspectionText);
        Assert.Equal(2, page.BackupFiles.Count);
        page.BackupFiles.Single(f => f.Name == "einstellungen.json").Selected = false;
        Assert.True(page.RestoreCommand.CanExecute(null));
        page.RestoreCommand.Execute(null);
        ui.Pump();
        Assert.Equal(1, restoredCount);
        Assert.Contains("Wiederhergestellt: pruefplaetze.json", page.BackupStatus);
        Assert.Equal(2, new StationSetupStore(stations).Load()!.Stations.Count);
        Assert.Single(Directory.GetFiles(P("pd"), "pruefplaetze.json.vor-wiederherstellung-*"));

        // Manipulierte Sicherung wird erkannt und nicht wiederhergestellt
        var bad = P("manipuliert.zip");
        File.Copy(zip, bad);
        using (var archive = ZipFile.Open(bad, ZipArchiveMode.Update))
        {
            archive.GetEntry("pruefplaetze.json")!.Delete();
            using var w = new StreamWriter(archive.CreateEntry("pruefplaetze.json").Open());
            w.Write("{\"format\":1,\"devices\":[],\"stations\":[]}");
        }
        var badInspection = ConfigBackup.Inspect(bad);
        Assert.False(badInspection.IsIntact);
        Assert.Contains("Prüfsumme", badInspection.Problems.Single());
        Assert.Throws<InvalidDataException>(() => ConfigBackup.Restore(bad, new Dictionary<string, string> { ["pruefplaetze.json"] = stations }, DateTimeOffset.Now));
        Assert.False(ConfigBackup.Inspect(settings).IsIntact);   // keine ZIP-Datei
    }
}
