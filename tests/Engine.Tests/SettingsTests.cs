using System.Data.Common;
using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.Settings;
using Testumgebung.Presentation.ViewModels;
using Xunit;

namespace Testumgebung.Engine.Tests;

/// <summary>Etappe 6: Einstellungen (Datei + Seite), Live-Übernahme, Ende-zu-Ende Prüfplatz → Datenbank → MQTT.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tu-set-" + Guid.NewGuid().ToString("N")[..8]);

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private sealed class TestRegistry : IDatabaseProviderRegistry
    {
        public DbProviderFactory? Factory(DatabaseProvider provider) => provider == DatabaseProvider.Sqlite ? NativeSqliteFactory.Instance : null;
    }

    /// <summary>Simulierter Plattformschutz: Klartext darf in der Datei nicht vorkommen.</summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string plain) => "rev:" + new string(plain.Reverse().ToArray());
        public string? Unprotect(string stored) => stored.StartsWith("rev:", StringComparison.Ordinal) ? new string(stored[4..].Reverse().ToArray()) : null;
    }

    [Fact]
    public void Einstellungsdatei_speichert_Geheimnisse_geschuetzt_und_meldet_Unlesbares()
    {
        var store = new SettingsStore(P("einstellungen.json"), new ReversingProtector());
        var s = new AppSettings
        {
            ResultFolder = @"D:\Ergebnisse",
            Database = new DatabaseSettings { Provider = DatabaseChoice.PostgreSql, ConnectionString = "Host=db;Password=Geheim123" },
            Mqtt = new MqttSettings { Enabled = true, Host = "broker.firma.local", Username = "tu", Password = "MqttPass!", CaCertificatePath = @"C:\ca.crt" },
        };
        store.Save(s);
        var json = File.ReadAllText(P("einstellungen.json"));
        Assert.DoesNotContain("Geheim123", json);
        Assert.DoesNotContain("MqttPass!", json);
        Assert.Contains("\"PostgreSql\"", json);

        var loaded = store.Load();
        Assert.Equal(("Host=db;Password=Geheim123", "MqttPass!", "broker.firma.local", @"D:\Ergebnisse"),
            (loaded.Database.ConnectionString, loaded.Mqtt.Password, loaded.Mqtt.Host, loaded.ResultFolder));
        Assert.Empty(store.Warnings);

        // Anderer Benutzer: Schutz nicht aufhebbar → Hinweis, kein Absturz
        var foreign = new SettingsStore(P("einstellungen.json"), new FailingProtector());
        var f = foreign.Load();
        Assert.Equal("", f.Database.ConnectionString);
        Assert.Equal(2, foreign.Warnings.Count);

        File.WriteAllText(P("kaputt.json"), "{ nicht json");
        var broken = new SettingsStore(P("kaputt.json"), new ReversingProtector());
        Assert.Equal(DatabaseChoice.Simulated, broken.Load().Database.Provider);
        Assert.Single(broken.Warnings);
    }

    private sealed class FailingProtector : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string? Unprotect(string stored) => null;
    }

    [SqliteFact]
    public async Task Ende_zu_Ende_Pruefplatz_Datenbank_MQTT_Ausfall_live_umschalten_und_nachreichen()
    {

        await using var broker = await TestBroker.StartAsync();
        var settings = new AppSettings
        {
            ResultFolder = P("ergebnisse"),
            Database = new DatabaseSettings { Provider = DatabaseChoice.Sqlite, ConnectionString = $"Data Source={P("pruef.db")}" },
            Mqtt = new MqttSettings { Enabled = true, Host = "127.0.0.1", Port = broker.Port, UseTls = false, Topic = "werk/{station}/{event}" },
        };
        await using var services = new ResultServices(settings, new TestRegistry(), P("outbox"), P("simdb"), recoveryFolder: P("wiederherstellung"));
        var store = new SettingsStore(P("einstellungen.json"), new ReversingProtector());
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: SampleProjects.AcPruefung(), clock: new VirtualClock(), services: services, settingsStore: store);
        Assert.True(await shell.Session.SignInAsync());
        Assert.True(shell.IsDeveloper);
        var page = shell.SettingsPage!;
        var s = shell.SelectedStation;
        s.WarningDelay = _ => Task.Delay(Timeout.Infinite);

        // 1) Datenbank erreichbar: gespeichert, MQTT „stored“
        _ = s.StartAsync();
        await CompleteDut(ui, s, "SN-1");
        await ui.Until(() => s.StorageReports.Count == 1, "erster Run gespeichert");
        Assert.True(s.StorageReports[0].Database!.Stored, s.StorageReports[0].Database!.Error);
        var db = new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={P("pruef.db")}", new SqliteDialect());
        Assert.Equal(1, (await db.CountAsync(s.StorageReports[0].RunId)).Runs);
        await WaitUntil(() => broker.Published.Count == 1);
        Assert.Equal("werk/" + Environment.MachineName + "/stored", broker.Published.Single().Topic);

        // 2) Datenbank fällt aus (Einstellung live geändert): 10-s-Warnung, MQTT „store_failed“, Run in der Outbox
        page.ConnectionString = $"Data Source={P(Path.Combine("fehlt", "x.db"))}";
        page.Save();
        Assert.StartsWith("Gespeichert und übernommen", page.SaveStatus);
        await CompleteDut(ui, s, "SN-2");
        await ui.Until(() => s.StorageReports.Count == 2, "zweiter Run");
        Assert.False(s.StorageReports[1].Database!.Stored);
        await ui.Until(() => s.StorageWarnings.Count == 1, "Werkerwarnung");
        await WaitUntil(() => broker.Published.Count == 2);
        Assert.EndsWith("/store_failed", broker.Published.Last().Topic, StringComparison.Ordinal);
        Assert.Single(services.Outbox.Pending);

        // 3) Datenbank wieder da → Outbox reicht nach, MQTT „stored_later“, kein Duplikat
        page.ConnectionString = $"Data Source={P("pruef.db")}";
        page.Save();
        await services.Outbox.RetryNowAsync();
        Assert.Empty(services.Outbox.Pending);
        Assert.Equal(1, (await db.CountAsync(s.StorageReports[1].RunId)).Runs);
        await WaitUntil(() => broker.Published.Count == 3);
        using (var json = JsonDocument.Parse(broker.Published.Last().Payload))
            Assert.Equal(("stored_later", "SN-2"), (json.RootElement.GetProperty("event").GetString(), json.RootElement.GetProperty("dutId").GetString()));

        // Einstellungsdatei geschrieben, Verbindungsstring geschützt
        var file = File.ReadAllText(P("einstellungen.json"));
        Assert.Contains("rev:", file);
        Assert.DoesNotContain("pruef.db", file);
        ui.Pump();
        Assert.Contains(page.MqttLog, r => r.Name.StartsWith("stored_later") && r.Passed);

        s.RequestAbort();
        await ui.Until(() => !s.IsRunning, "Ende");
    }

    [SqliteFact]
    public async Task Einstellungsseite_Verbindungstest_Vertragspruefung_Verwerfen_und_ohne_Datenbank()
    {

        await using var services = new ResultServices(new AppSettings(), new TestRegistry(), P("outbox"), P("simdb"), recoveryFolder: P("w"));
        var ui = new QueueDispatcher();
        var page = new SettingsViewModel(services, new SettingsStore(P("e.json"), PlainSecretProtector.Instance), ui);
        Assert.Equal(DatabaseChoice.Simulated, page.SelectedProvider.Value);
        Assert.False(page.TestDatabaseCommand.CanExecute(null));            // simulierte DB: nichts zu prüfen

        page.SelectedProvider = page.Providers.Single(p => p.Value == DatabaseChoice.Sqlite);
        Assert.Contains("Data Source", page.ConnectionExample);
        page.ConnectionString = $"Data Source={P("check.db")}";
        await page.TestDatabaseAsync();
        ui.Pump();
        Assert.StartsWith("Verbunden: SQLite", page.DatabaseStatus);

        await page.ContractCheckAsync();
        ui.Pump();
        Assert.Equal("Vertragsprüfung bestanden.", page.DatabaseStatus);
        Assert.Equal(6, page.ContractResult.Count);
        Assert.All(page.ContractResult, r => Assert.True(r.Passed, r.Name + ": " + r.Detail));

        page.Revert();                                                       // nichts gespeichert → zurück
        Assert.Equal(DatabaseChoice.Simulated, page.SelectedProvider.Value);

        // Fehlender Treiber: Hinweis statt Absturz
        page.SelectedProvider = page.Providers.Single(p => p.Value == DatabaseChoice.MySql);
        page.ConnectionString = "Server=x";
        page.Save();
        Assert.Contains("Treiber", page.SaveStatus);

        // Keine Datenbank: keine Übertragung, keine Warnung
        page.SelectedProvider = page.Providers.Single(p => p.Value == DatabaseChoice.None);
        page.ResultFolder = P("erg");
        page.Save();
        Assert.False(services.Pipeline.DatabaseEnabled);
        var run = DatabaseContractCheck.SampleRun("R-ohne-DB");
        var report = await services.Pipeline.ProcessAsync(run, new Testumgebung.Domain.Storage.StorageResolver(SampleProjects.AcPruefung().Definition));
        Assert.Null(report.Database);
        Assert.True(report.File!.Success);
        Assert.True(report.File.Path!.StartsWith(P("erg"), StringComparison.Ordinal), report.File.Path);
    }

    [Fact]
    public async Task MQTT_Testmeldung_von_der_Einstellungsseite()
    {
        await using var broker = await TestBroker.StartAsync(user: "tu", password: "pw");
        await using var services = new ResultServices(new AppSettings(), new TestRegistry(), P("outbox"), P("simdb"), recoveryFolder: P("w"));
        var ui = new QueueDispatcher();
        var page = new SettingsViewModel(services, new SettingsStore(P("e.json"), PlainSecretProtector.Instance), ui);
        page.MqttUseTls = false;
        Assert.Equal(1883, page.MqttPort);
        page.MqttHost = "127.0.0.1";
        page.MqttPort = broker.Port;
        page.MqttUsername = "tu";
        page.MqttPassword = "falsch";
        await page.TestMqttAsync();
        ui.Pump();
        Assert.Contains("Passwort", page.MqttStatus);

        page.MqttPassword = "pw";
        await page.TestMqttAsync();
        ui.Pump();
        Assert.StartsWith("Testmeldung an 127.0.0.1", page.MqttStatus);
        await WaitUntil(() => broker.Published.Count == 1);
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
}
