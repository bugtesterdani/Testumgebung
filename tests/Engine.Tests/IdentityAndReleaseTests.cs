using System.IO.Compression;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Identity;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;
using Xunit;

namespace Testumgebung.Engine.Tests;

/// <summary>Etappe 7: Anmeldung und Rechte (A01), unveränderliche Release-Pakete und Freigabe (V01), Audit-Log.</summary>
public sealed class IdentityAndReleaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tu-rel-" + Guid.NewGuid().ToString("N")[..8]);

    public IdentityAndReleaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static readonly Actor Dev = new("dev@firma.example", UserRights.Develop);
    private static readonly Actor Approver = new("qs@firma.example", UserRights.Release);
    private static readonly Actor Both = new("m.weber@firma.example", UserRights.Develop | UserRights.Release);

    [Fact]
    public async Task Lokale_Anmeldung_kann_bewusst_ohne_Startparameter_aktiviert_werden()
    {
        var session = new SessionService(new SimulatedIdentityProvider(() => "Daniel"), null);
        Assert.False(await session.SignInAsync());
        Assert.True(session.CanEnableDevelopmentMode);
        var shell = new ShellViewModel(identity: new IdentityServices(session, null, _ => new SimulatedIdentityProvider(() => "Daniel")));
        await shell.LoginDevelopmentAsync();
        Assert.True(shell.IsSignedIn);
        Assert.True(shell.IsDeveloper);
        Assert.True(session.IsDevelopmentMode);
        Assert.False(shell.CanEnableDevelopmentLogin);
        foreach (var config in new[] { new AccessConfig { ClientId = "client", TenantId = "tenant" }, new AccessConfig { LoadError = "beschädigt" }, new AccessConfig() })
        {
            var configured = new SessionService(new SimulatedIdentityProvider(() => "Daniel"), config);
            Assert.False(configured.CanEnableDevelopmentMode);
            Assert.Throws<InvalidOperationException>(configured.EnableDevelopmentMode);
        }
    }

    // ------------------------------------------------------------------ Pakete und Audit

    [Fact]
    public void Paket_enthaelt_alle_Bereiche_mit_Hashes_und_erkennt_Veraenderungen()
    {
        var project = SampleProjects.AcPruefung();
        var code = BlockCatalog.Default.Create(BlockCatalog.Code, "c1", "Prüfcode", new Dictionary<string, string> { ["Sprache"] = "Python", ["Quelltext"] = "return Pass()" });
        ((GroupDefinition)project.Definition.Find("pruefling")!).Children.Add(code);
        project.Definition.Version = "1.5";

        var (bytes, manifest) = ReleasePackage.Build(project, "m.weber@firma.example", DateTimeOffset.Now);
        Assert.Equal(["Ablauf", "Codeartefakte", "Kurven", "Definitionen"], manifest.Entries.Select(e => e.Area).Distinct());
        Assert.Contains(manifest.Entries, e => e.Area == "Codeartefakte" && e.Name.StartsWith("Python"));
        Assert.All(manifest.Entries, e => Assert.Equal(64, e.Sha256.Length));

        var opened = ReleasePackage.Open(bytes);
        Assert.True(opened.IsIntact, string.Join("; ", opened.Problems));
        Assert.Equal("1.5", opened.Project.Definition.Version);
        Assert.NotNull(opened.Project.Definition.Find("c1"));

        // Ablauf im Paket nachträglich verändert → Hash passt nicht mehr
        var tampered = Rewrite(bytes, "projekt.tproj.json", json => json.Replace("\"GrenzwertMax\"", "\"GrenzwertMin\"", StringComparison.Ordinal));
        var t = ReleasePackage.Open(tampered);
        Assert.False(t.IsIntact);
        Assert.Contains(t.Problems, p => p.StartsWith("Ablauf"));
        Assert.NotEqual(ReleasePackage.Sha256(bytes), ReleasePackage.Sha256(tampered));
    }

    [Fact]
    public void Audit_Hash_Kette_erkennt_Aenderung_und_Loeschung()
    {
        var t0 = DateTimeOffset.Now;
        var e1 = AuditChain.Create(1, t0, "a", "Entwurf erstellt", "v1", null, AuditChain.Genesis);
        var e2 = AuditChain.Create(2, t0, "b", "Version freigegeben", "v1", null, e1.Hash);
        var e3 = AuditChain.Create(3, t0, "c", "Abmeldung", "", null, e2.Hash);
        Assert.Null(AuditChain.Verify([e1, e2, e3]));
        Assert.Contains("verändert", AuditChain.Verify([e1, e2 with { User = "x" }, e3]));
        Assert.Contains("fehlt", AuditChain.Verify([e1, e3]));
    }

    [Fact]
    public async Task Rechte_getrennt_oder_kombiniert_und_Regeln_der_Freigabe_Ordnerablage()
        => await WorkflowRules(new FolderReleaseStore(P("releases")));

    [SqliteFact]
    public async Task Rechte_getrennt_oder_kombiniert_und_Regeln_der_Freigabe_SQLite()
    {

        var db = new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={P("rel.db")}", new SqliteDialect());
        await WorkflowRules(new SqlReleaseStore(db));
    }

    private static async Task WorkflowRules(IReleaseStore store)
    {
        var service = new ReleaseService(store);
        var project = SampleProjects.AcPruefung();

        // Nur Freigeben darf keinen Entwurf anlegen; nur Entwickeln darf nicht freigeben
        await Assert.ThrowsAsync<ReleaseRuleException>(() => service.CreateDraftAsync(project, "1.5", Approver));
        var draft = await service.CreateDraftAsync(project, "1.5", Dev);
        Assert.Equal((ReleaseState.Draft, "dev@firma.example"), (draft.State, draft.CreatedBy));
        await Assert.ThrowsAsync<ReleaseRuleException>(() => service.CreateDraftAsync(project, "1.5", Dev)); // Version vergeben
        await Assert.ThrowsAsync<ReleaseRuleException>(() => service.ReleaseAsync(draft, Both, confirmed: true)); // erst prüfen

        await Assert.ThrowsAsync<ReleaseRuleException>(() => service.ReviewAsync(draft, Approver));
        var checks = await service.ReviewAsync(draft, Dev);
        Assert.All(checks, c => Assert.True(c.Passed, c.Name + ": " + c.Detail));
        var reviewed = (await service.ListAsync()).Single(r => r.ReleaseId == draft.ReleaseId);
        Assert.Equal((ReleaseState.Review, "dev@firma.example"), (reviewed.State, reviewed.ReviewedBy));
        Assert.Contains("OK Paketintegrität", reviewed.ReviewReport);

        await Assert.ThrowsAsync<ReleaseRuleException>(() => service.ReleaseAsync(reviewed, Dev, confirmed: true));     // Recht fehlt
        await Assert.ThrowsAsync<ReleaseRuleException>(() => service.ReleaseAsync(reviewed, Approver, confirmed: false)); // nicht bestätigt
        await service.ReleaseAsync(reviewed, Approver, confirmed: true);
        var (info, released) = (await service.LoadReleasedAsync("AC-Prüfung"))!.Value;
        Assert.Equal(("1.5", "qs@firma.example"), (info.Version, info.ReleasedBy));
        Assert.Equal("1.5", released.Definition.Version);

        // Zweiter Zyklus mit kombinierten Rechten: Vorgänger wird archiviert, Pakete bleiben unverändert
        var v16 = await service.CreateDraftAsync(project, "1.6", Both);
        await service.ReviewAsync(v16, Both);
        await service.ReleaseAsync((await service.ListAsync()).Single(r => r.Version == "1.6"), Both, confirmed: true);
        var all = await service.ListAsync();
        Assert.Equal(ReleaseState.Archived, all.Single(r => r.Version == "1.5").State);
        Assert.Equal(ReleaseState.Released, all.Single(r => r.Version == "1.6").State);
        Assert.Equal(info.PackageSha256, ReleasePackage.Sha256(await store.GetPackageAsync(info.ReleaseId)));

        // Zustandswechsel nur aus dem erwarteten Zustand (z. B. zweiter Prüfplatz mit veraltetem Stand)
        Assert.False(await store.TransitionAsync(new ReleaseTransition(v16.ReleaseId, ReleaseState.Draft, ReleaseState.Review, "x", DateTimeOffset.Now),
            new AuditDraft(DateTimeOffset.Now, "x", "x", "x")));

        var audit = await store.ReadAuditAsync();
        Assert.Null(AuditChain.Verify(audit));
        Assert.Equal(["Entwurf erstellt", "Entwicklungsprüfung abgeschlossen", "Version freigegeben", "Entwurf erstellt", "Entwicklungsprüfung abgeschlossen", "Version freigegeben"],
            audit.Select(a => a.Action));
        Assert.Equal(["dev@firma.example", "dev@firma.example", "qs@firma.example"], audit.Take(3).Select(a => a.User));
    }

    [SqliteFact]
    public async Task Audit_Log_bleibt_bei_parallelen_Schreibern_lueckenlos_SQLite()
    {

        var a = new SqlReleaseStore(new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={P("audit.db")}", new SqliteDialect()));
        var b = new SqlReleaseStore(new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={P("audit.db")}", new SqliteDialect()));
        await a.AppendAuditAsync(new AuditDraft(DateTimeOffset.Now, "init", "start", ""));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            (i % 2 == 0 ? a : b).AppendAuditAsync(new AuditDraft(DateTimeOffset.Now, $"u{i}", "Eintrag", i.ToString()))));
        var audit = await a.ReadAuditAsync();
        Assert.Equal(21, audit.Count);
        Assert.Null(AuditChain.Verify(audit));
    }

    // ------------------------------------------------------------------ Anmeldung (A01)

    /// <summary>Anbieter, dessen Bestätigung der Test steuert (wie ein WAM-Dialog, der offen ist).</summary>
    private sealed class ControlledProvider : IIdentityProvider
    {
        public TaskCompletionSource<UserIdentity> Pending { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "Test-Entra";

        public Task<UserIdentity> SignInAsync(bool selectAccount, CancellationToken cancellationToken)
        {
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Pending.Task;
        }

        public Task SignOutAsync(UserIdentity identity, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static AccessConfig EntraConfig() => new()
    {
        ClientId = "11111111-2222-3333-4444-555555555555",
        TenantId = "tenant-firma",
        Users =
        [
            new RightsEntry { Upn = "m.weber@firma.example", Develop = true, Release = true },
            new RightsEntry { Upn = "qs@firma.example", Release = true },
        ],
    };

    private static UserIdentity Entra(string upn, string tenant = "tenant-firma")
        => new(upn, upn.Split('@')[0], "Microsoft Entra ID", true, tenant, Guid.NewGuid().ToString());

    [Fact]
    public async Task A01_Anmeldung_waehrend_Messung_Test_laeuft_weiter_Rechte_gehoeren_bestaetigter_Person()
    {
        var provider = new ControlledProvider();
        var identity = new IdentityServices(new SessionService(provider, EntraConfig()), null, _ => provider);
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: SampleProjects.AcPruefung(), clock: new VirtualClock(), identity: identity);
        var s = shell.SelectedStation;

        _ = s.StartAsync();
        await Answer(ui, s, "Kennung erfassen", c => { c.Input = "SN-A01"; c.Confirm(); });

        // Anmeldung beginnt während der Messung – noch keine Rechte, Prüfung läuft weiter
        var login = shell.LoginAsync(false);
        ui.Pump();
        Assert.True(shell.IsSigningIn);
        Assert.False(shell.IsDeveloper);
        Assert.False(shell.NavigateCommand.CanExecute(ShellPage.Editor));
        await Answer(ui, s, "Sichtprüfung", c => c.ChooseCommand.Execute("i. O."));
        Assert.True(s.IsRunning);

        // Bestätigung durch Entra: Rechte gehören genau dieser Person
        provider.Pending.SetResult(Entra("qs@firma.example"));
        await login;
        ui.Pump();
        Assert.Equal("qs@firma.example", shell.Session.User!.Upn);
        Assert.True(shell.CanRelease);
        Assert.False(shell.IsDeveloper);                                        // nur Freigeben
        Assert.False(shell.NavigateCommand.CanExecute(ShellPage.Editor));

        // Konto wechseln: alte Rechte sofort weg, bis die neue Person bestätigt ist
        var change = shell.LoginAsync(true);
        ui.Pump();
        Assert.False(shell.CanRelease);
        Assert.Null(shell.Session.User);
        provider.Pending.SetResult(Entra("m.weber@firma.example"));
        await change;
        ui.Pump();
        Assert.True(shell.IsDeveloper && shell.CanRelease);
        Assert.Equal(ShellPage.Editor, shell.Page);

        await Answer(ui, s, "Gehäusetemperatur erfassen", c => { c.Input = "22"; c.Confirm(); });
        await Answer(ui, s, "Prüfling anschließen", c => c.ConfirmCommand.Execute(null));
        await ui.Until(() => s.Protocol.Any(p => p.Text.Contains("SN-A01 abgeschlossen")), "Prüfling trotz Anmeldung abgeschlossen");

        // Abmelden: Rechte weg, Prüfplatz läuft weiter
        await shell.LogoutAsync();
        ui.Pump();
        Assert.False(shell.IsDeveloper);
        Assert.Equal(ShellPage.Operator, shell.Page);
        Assert.True(s.IsRunning);
        s.RequestAbort();
        await ui.Until(() => !s.IsRunning, "Ende");
    }

    [Fact]
    public async Task Entra_Modus_lehnt_unbestaetigte_fremde_und_rechtelose_Konten_ab()
    {
        var provider = new ControlledProvider();
        var session = new SessionService(provider, EntraConfig());

        async Task<bool> Try(UserIdentity id)
        {
            var t = session.SignInAsync();
            provider.Pending.SetResult(id);
            return await t;
        }

        Assert.False(await Try(new UserIdentity("m.weber@firma.example", "M. Weber", "Simuliert", IsConfirmed: false)));
        Assert.Contains("bestätigte", session.LastError);
        Assert.False(await Try(Entra("m.weber@firma.example", tenant: "fremd")));
        Assert.Contains("Mandanten", session.LastError);
        Assert.False(await Try(Entra("praktikant@firma.example")));
        Assert.Contains("keine Rechte", session.LastError);
        Assert.Equal(UserRights.None, session.Rights);
        Assert.True(await Try(Entra("M.Weber@FIRMA.example")));                      // UPN ohne Groß-/Kleinschreibung
        Assert.Equal(UserRights.Develop | UserRights.Release, session.Rights);

        // Entwicklungsmodus (keine Zugangsdatei): simulierte Anmeldung mit allen Rechten
        var dev = new SessionService(new SimulatedIdentityProvider(() => "M. Weber"), null, allowDevelopmentMode: true);
        Assert.True(await dev.SignInAsync());
        Assert.Equal(("m.weber@simuliert.local", false), (dev.User!.Upn, dev.User.IsConfirmed));
        Assert.True(dev.IsDevelopmentMode);
        var production = new SessionService(new SimulatedIdentityProvider(() => "M. Weber"), null);
        Assert.False(await production.SignInAsync());
        Assert.Equal(UserRights.None, production.Rights);
        Assert.False(production.IsDevelopmentMode);
        File.WriteAllText(P("broken-access.json"), "{defekt");
        dev.Reconfigure(new SimulatedIdentityProvider(() => "M. Weber"), new AccessConfigStore(P("broken-access.json")).Load());
        Assert.False(dev.IsSignedIn);
    }

    [Fact]
    public async Task Zugangsdatei_und_Inaktivitaetssperre()
    {
        var store = new AccessConfigStore(P("zugang.json"));
        Assert.Null(store.Load());                                                    // keine Datei → Entwicklungsmodus
        var config = EntraConfig();
        config.InactivityMinutes = 5;
        store.Save(config);
        var loaded = store.Load()!;
        Assert.True(loaded.UsesEntra);
        Assert.Equal(UserRights.Release, loaded.RightsFor("QS@firma.example"));

        File.WriteAllText(P("kaputt.json"), "{ kein json");
        var broken = new AccessConfigStore(P("kaputt.json")).Load()!;
        Assert.NotNull(broken.LoadError);
        var bs = new SessionService(new SimulatedIdentityProvider(() => "x"), broken);
        Assert.False(await bs.SignInAsync());                                          // beschädigt → keine Rechte

        var clock = new VirtualClock(autoAdvance: false);
        var provider = new ControlledProvider();
        var session = new SessionService(provider, loaded, clock);
        var login = session.SignInAsync();
        provider.Pending.SetResult(Entra("qs@firma.example"));
        Assert.True(await login);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(session.CheckInactivity());
        session.NoteActivity();
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(session.CheckInactivity());
        await Task.Delay(10);
        Assert.False(session.IsSignedIn);
    }

    // ------------------------------------------------------------------ Werkerbetrieb und V01

    private (ShellViewModel Shell, QueueDispatcher Ui, ReleaseHub Hub) CreateWithReleases()
    {
        var hub = new ReleaseHub(new ReleaseService(new FolderReleaseStore(P("releases"))));
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: SampleProjects.AcPruefung(), clock: new VirtualClock(), releases: hub);
        return (shell, ui, hub);
    }

    [Fact]
    public async Task Releases_zeigen_alle_Beispielprogramme_und_erstellen_Entwurf_des_gewaehlten_Programms()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        var example = Testumgebung.Persistence.Stations.OrderExample.Load(Path.Combine(root.FullName, "docs", "beispiele"));
        var hub = new ReleaseHub(new ReleaseService(new FolderReleaseStore(P("programs"))));
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: example.Project, releases: hub,
            stations: new Testumgebung.Persistence.Stations.StationCoordinator(example.Setup, new Testumgebung.Hardware.ResourceManager()));
        shell.Login();
        ui.Pump();
        var page = shell.ReleasesPage!;
        Assert.Equal(new[] { "Programm A", "Programm B", "Programm C", "Programm D" }, page.Programs.Select(p => p.Name));
        page.SelectedProgramName = "Programm D";
        page.NewVersion = "2.0";
        var draft = await page.CreateDraftAsync();
        ui.Pump();
        Assert.NotNull(draft);
        Assert.Equal("Programm D", draft.ProjectName);
        Assert.Equal("Programm D", (await hub.Service.OpenAsync(draft)).Project.Definition.Name);
        Assert.Equal("Programm A", shell.Editor.Project.Definition.Name);
        Assert.Single(page.Releases);
        page.SelectedProgramName = "Programm B";
        Assert.Empty(page.Releases);
        Assert.Null(page.Selected);
        Assert.Contains("kein Release", page.ProgramStatus);
        page.SelectedProgramName = "Programm D";
        Assert.Equal(draft.ReleaseId, page.Selected!.ReleaseId);
    }

    private static async Task Release(ShellViewModel shell, QueueDispatcher ui, string version)
    {
        var page = shell.ReleasesPage!;
        page.NewVersion = version;
        Assert.NotNull(await page.CreateDraftAsync());
        ui.Pump();
        Assert.True(await page.ReviewAsync(), page.Status);
        ui.Pump();
        page.Confirmed = true;
        Assert.True(await page.ReleaseAsync(), page.Status);
        ui.Pump();
    }

    [Fact]
    public async Task Werkerbetrieb_nur_mit_freigegebener_Version_Entwurf_nur_fuer_Entwickler()
    {
        var (shell, ui, _) = CreateWithReleases();
        var s = shell.SelectedStation;
        await s.StartAsync();
        Assert.False(s.IsRunning);
        Assert.Contains("Keine freigegebene Version", s.Error);

        shell.Login();
        ui.Pump();
        s.UseDraft = true;                                                        // Entwickler testet den Entwurf
        _ = s.StartAsync();
        await ui.Until(() => s.IsRunning, "Entwurf läuft");
        Assert.Equal("Entwurf v1.5 (nicht freigegeben)", s.RunningVersionText);
        s.RequestAbort();
        await ui.Until(() => !s.IsRunning, "Ende");

        await Release(shell, ui, "1.6");
        await shell.LogoutAsync();
        ui.Pump();
        Assert.False(s.UseDraft);                                                  // Abmelden setzt Entwurfsmodus zurück
        _ = s.StartAsync();
        await ui.Until(() => s.IsRunning, "Freigabe läuft");
        Assert.Equal("v1.6 · Freigegeben", s.RunningVersionText);
        Assert.Equal("v1.6 · Freigegeben", shell.ActiveSoftwareText);
        s.RequestAbort();
        await ui.Until(() => !s.IsRunning, "Ende");

        var page = shell.ReleasesPage!;
        await page.LoadAuditAsync();
        ui.Pump();
        Assert.StartsWith("Hash-Kette geprüft", page.AuditState);
        Assert.Contains(page.Audit, a => a.Action == "Anmeldung" && a.User == "m.weber@simuliert.local");
        Assert.Contains(page.Audit, a => a.Action == "Version freigegeben");
        Assert.Contains(page.Audit, a => a.Action == "Abmeldung");
    }

    [Fact]
    public async Task V01_Neuer_Release_bei_laufendem_Pruefling_gilt_erst_ab_dem_naechsten()
    {
        var (shell, ui, hub) = CreateWithReleases();
        shell.Login();
        ui.Pump();
        await Release(shell, ui, "1.6");
        var s = shell.SelectedStation;
        _ = s.StartAsync();
        await Answer(ui, s, "Kennung erfassen", c => { c.Input = "SN-1"; c.Confirm(); });

        // Während Prüfling 1 läuft: Grenzwert im Prüfling geändert und als 1.7 freigegeben
        var step = (StepDefinition)shell.Editor.Project.Definition.Find("spannung")!;
        step.Parameters["Untergrenze"] = "225";
        BlockCatalog.Default.Rebuild(step);
        await Release(shell, ui, "1.7");
        Assert.Equal("1.7", hub.CurrentInfo("AC-Prüfung")!.Version);

        await Rest(ui, s);
        await ui.Until(() => s.DutCount == 2, "zweiter Prüfling");
        await Answer(ui, s, "Kennung erfassen", c => { c.Input = "SN-2"; c.Confirm(); });
        await Rest(ui, s);
        await ui.Until(() => s.Run!.Events.OfType<DutRunCompleted>().Count() == 2, "zwei Prüflinge");

        var run = s.Run!;
        var duts = run.Events.OfType<DutRunCompleted>().ToList();
        Assert.Equal(["1.6", "1.7"], duts.Select(d => d.TestVersion));               // laufender Prüfling behielt 1.6
        var limits = run.Events.OfType<StepCompleted>().Where(e => e.NodeId == "spannung").Select(e => e.LowLimit).ToList();
        Assert.Equal([220.0, 225.0], limits);
        var activated = Assert.Single(run.Events.OfType<VersionActivated>());
        Assert.Equal(("1.6", "1.7"), (activated.FromVersion, activated.ToVersion));
        Assert.True(activated.Sequence > duts[0].Sequence);                         // nach Ende von Prüfling 1
        ui.Pump();
        Assert.Equal("v1.7 · Freigegeben", s.RunningVersionText);

        // Unverträgliche Änderung (Init): erst ab dem nächsten Hauptablaufstart
        var init = (StepDefinition)shell.Editor.Project.Definition.Find("init.auftrag")!;
        init.Parameters["Ausdruck"] = "\"FA-9999\"";
        BlockCatalog.Default.Rebuild(init);
        await Release(shell, ui, "1.8");
        await Answer(ui, s, "Kennung erfassen", c => { c.Input = "SN-3"; c.Confirm(); }); // Prüfling 3 lief bereits
        await Rest(ui, s);
        await ui.Until(() => s.DutCount == 4 && s.ActiveContent is { IsAwaiting: true }, "vierter Prüfling");
        ui.Pump();
        Assert.Contains("1.8", s.PendingVersionText);
        Assert.Contains("nächsten Start", s.PendingVersionText);
        Assert.Contains("Init geändert", s.PendingVersionText);
        Assert.Single(run.Events.OfType<VersionActivated>());
        Assert.Equal("v1.7 · Freigegeben", s.RunningVersionText);

        s.RequestAbort();
        await ui.Until(() => !s.IsRunning, "Ende");
        _ = s.StartAsync();
        await ui.Until(() => s.IsRunning, "Neustart");
        Assert.Equal("v1.8 · Freigegeben", s.RunningVersionText);
        Assert.Null(s.PendingVersionText);
        s.RequestAbort();
        await ui.Until(() => !s.IsRunning, "Ende");
    }

    [Fact]
    public void Vertraeglichkeit_einer_neuen_Version_am_Pruefling()
    {
        var a = SampleProjects.AcPruefung();
        var b = SampleProjects.AcPruefung();
        b.Definition.Version = "9.9";
        b.Description = "anders";
        ((StepDefinition)b.Definition.Find("spannung")!).Parameters["Obergrenze"] = "250";
        Assert.True(DefinitionCompatibility.CanSwapAtDut(a, b, out _));

        var c = SampleProjects.AcPruefung();
        c.Definition.Root.Variables.Add(new VariableDefinition("Neu", VarType.Int, 1L));
        Assert.False(DefinitionCompatibility.CanSwapAtDut(a, c, out var r1));
        Assert.Contains("Variablen", r1);

        var d = SampleProjects.AcPruefung();
        ((StepDefinition)d.Definition.Find("fin.reset")!).Parameters["Wert"] = "true";
        Assert.False(DefinitionCompatibility.CanSwapAtDut(a, d, out var r2));
        Assert.Contains("Finalization", r2);
    }

    // ------------------------------------------------------------------ Hilfen

    private static async Task Answer(QueueDispatcher ui, StationViewModel s, string step, Action<OperatorContentViewModel> act)
    {
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true } c && c.Title == step, step);
        act(s.ActiveContent!);
        ui.Pump();
    }

    private static async Task Rest(QueueDispatcher ui, StationViewModel s)
    {
        await Answer(ui, s, "Sichtprüfung", c => c.ChooseCommand.Execute("i. O."));
        await Answer(ui, s, "Gehäusetemperatur erfassen", c => { c.Input = "22"; c.Confirm(); });
        await Answer(ui, s, "Prüfling anschließen", c => c.ConfirmCommand.Execute(null));
    }

    private static byte[] Rewrite(byte[] zipBytes, string entry, Func<string, string> change)
    {
        using var input = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        using var ms = new MemoryStream();
        using (var output = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in input.Entries)
            {
                using var r = new StreamReader(e.Open(), Encoding.UTF8);
                var text = r.ReadToEnd();
                var n = output.CreateEntry(e.FullName);
                using var w = new StreamWriter(n.Open(), new UTF8Encoding(false));
                w.Write(e.FullName == entry ? change(text) : text);
            }
        }
        return ms.ToArray();
    }
}
