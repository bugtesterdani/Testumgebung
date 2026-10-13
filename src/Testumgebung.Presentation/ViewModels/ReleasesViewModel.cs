using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Identity;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.Releases;

namespace Testumgebung.Presentation.ViewModels;

public sealed record PackageRow(string Area, string Name, string Version, string Hash, bool Ok);

public sealed record RightsRow(string Label, bool Granted);

public sealed record StationVersionRow(string Station, string Text);
public sealed record ReleaseProgramRow(string Name, string Status);

/// <summary>Schritt der Fortschrittsanzeige Entwurf → Entwicklungsprüfung → Freigabe.</summary>
public sealed record StageRow(int Number, string Title, string Detail, bool Done, bool Current);

/// <summary>
/// Entwicklerseite „Releases“ (Abbildung Freigabeprüfung): Versionen, Paketinhalt mit Hashes, Entwicklungsprüfung,
/// ausdrückliche Freigabe, Audit-Log, Identität und Rechte, Versionen auf den Prüfplätzen.
/// </summary>
public sealed partial class ReleasesViewModel : ObservableObject
{
    private readonly ReleaseHub _hub;
    private readonly SessionService _session;
    private readonly EditorViewModel _editor;
    private readonly IUiDispatcher _ui;
    private readonly IReadOnlyList<StationViewModel> _stations;
    private ReleaseInfo? _selected;
    private string _newVersion = "";
    private bool _confirmed;
    private string? _status;
    private bool _statusIsError;
    private bool _busy;
    private string _auditState = "";
    private string? _programName;

    public ReleasesViewModel(ReleaseHub hub, SessionService session, EditorViewModel editor, IUiDispatcher ui, IReadOnlyList<StationViewModel> stations)
    {
        _hub = hub;
        _session = session;
        _editor = editor;
        _ui = ui;
        _stations = stations;
        CreateDraftCommand = new RelayCommand(() => _ = CreateDraftAsync(), () => !_busy && _session.CanDevelop);
        ReviewCommand = new RelayCommand(() => _ = ReviewAsync(), () => !_busy && _session.CanDevelop && _selected?.State == ReleaseState.Draft);
        ReleaseCommand = new RelayCommand(() => _ = ReleaseAsync(), () => !_busy && _session.CanRelease && _confirmed && _selected?.State == ReleaseState.Review);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        hub.Changed += () => _ui.Post(Rebuild);
        session.Changed += () => _ui.Post(RaiseIdentity);
        editor.ProjectReplaced += () => _ui.Post(Rebuild);
        foreach (var s in stations)
            s.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StationViewModel.AvailablePrograms)) _ui.Post(Rebuild);
                if (e.PropertyName is nameof(StationViewModel.RunningVersionText) or nameof(StationViewModel.IsRunning)) _ui.Post(RebuildStations);
            };
        Rebuild();
        RebuildStations();
    }

    public RelayCommand CreateDraftCommand { get; }
    public RelayCommand ReviewCommand { get; }
    public RelayCommand ReleaseCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public ObservableCollection<ReleaseInfo> Releases { get; } = [];
    public ObservableCollection<PackageRow> Package { get; } = [];
    public ObservableCollection<ReviewCheck> Checks { get; } = [];
    public ObservableCollection<AuditEntry> Audit { get; } = [];
    public ObservableCollection<StageRow> Stages { get; } = [];
    public ObservableCollection<StationVersionRow> StationVersions { get; } = [];

    public ObservableCollection<ReleaseProgramRow> Programs { get; } = [];
    public string ProjectName => _programName ?? _editor.Project.Definition.Name;
    public string SelectedProgramName
    {
        get => ProjectName;
        set
        {
            if (string.IsNullOrEmpty(value) || value == ProjectName) return;
            _programName = value;
            Selected = null;
            Differences.Clear();
            CompareWith = null;
            CompareStatus = null;
            Rebuild();
            OnPropertyChanged();
        }
    }
    public string ProgramStatus => Programs.FirstOrDefault(p => p.Name == ProjectName)?.Status ?? "Kein Release vorhanden";

    private TestProject? DraftSource() => _editor.Project.Definition.Name == ProjectName ? _editor.Project
        : _stations.SelectMany(s => s.Coordinator?.Setup.ExampleProjects ?? []).FirstOrDefault(p => p.Definition.Name == ProjectName);
    public string StoreName => _hub.Service.Store.Name;
    public string? StoreError => _hub.Error;

    public ReleaseInfo? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            Confirmed = false;
            Checks.Clear();
            BuildStages();
            _ = LoadPackageAsync(value);
            RaiseCommands();
            OnPropertiesChanged(nameof(SelectedTitle), nameof(ReviewReport), nameof(CanConfirm), nameof(ReleaseByText), nameof(CompareTargets));
        }
    }

    public string SelectedTitle => _selected is null ? "Kein Release gewählt" : $"Paket-Inhalt – v{_selected.Version} ({_selected.StateText})";

    public string? ReviewReport => _selected?.ReviewReport;

    public string NewVersion
    {
        get => _newVersion;
        set => SetProperty(ref _newVersion, value);
    }

    /// <summary>„Geprüftes Paket freigeben“ – ausdrückliche Bestätigung.</summary>
    public bool Confirmed
    {
        get => _confirmed;
        set
        {
            if (SetProperty(ref _confirmed, value)) ReleaseCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanConfirm => _selected?.State == ReleaseState.Review && _session.CanRelease;

    public string ReleaseByText => $"{_session.User?.DisplayName ?? "—"} · {DateTime.Now:dd.MM.yyyy HH:mm}";

    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public string AuditState
    {
        get => _auditState;
        private set => SetProperty(ref _auditState, value);
    }

    // ------------------------------------------------------------------ Identität

    public string UserName => _session.User?.DisplayName ?? "Nicht angemeldet";
    public string UserUpn => _session.User?.Upn ?? "";
    public string IdentitySource => _session.User is not { } u ? "" : u.IsConfirmed ? "Firmenkonto bestätigt · Microsoft Entra ID" : "Simulierte Anmeldung (nicht bestätigt)";
    public bool IsConfirmed => _session.User?.IsConfirmed == true;
    public bool IsDevelopmentMode => _session.IsDevelopmentMode;

    public IReadOnlyList<RightsRow> Rights =>
    [
        new("Entwickeln (Entwurf, Test, Simulation)", _session.CanDevelop),
        new("Freigeben (Release freigeben)", _session.CanRelease),
    ];

    // ------------------------------------------------------------------ Aktionen

    public async Task RefreshAsync()
    {
        await _hub.RefreshAsync().ConfigureAwait(false);
        await LoadAuditAsync().ConfigureAwait(false);
    }

    public async Task<ReleaseInfo?> CreateDraftAsync()
    {
        if (!Begin()) return null;
        try
        {
            var source = DraftSource() ?? throw new InvalidOperationException("Dieses Programm zuerst im Editor öffnen, um einen neuen Entwurf zu erstellen.");
            var version = string.IsNullOrWhiteSpace(NewVersion) ? SuggestVersion() : NewVersion.Trim();
            var info = await _hub.Service.CreateDraftAsync(source, version, Actor()).ConfigureAwait(false);
            _ui.Post(() =>
            {
                if (_editor.Project.Definition.Name == info.ProjectName) _editor.Project.Definition.Version = version;
                NewVersion = "";
                Done($"Entwurf v{version} erstellt – Paket {info.ShortHash} ist unveränderlich.", false);
            });
            await _hub.RefreshAsync().ConfigureAwait(false);
            _ui.Post(() => Selected = Releases.FirstOrDefault(r => r.ReleaseId == info.ReleaseId));
            return info;
        }
        catch (Exception ex) when (ex is ReleaseRuleException or InvalidOperationException or ReleasePackageException or IOException or System.Data.Common.DbException)
        {
            _ui.Post(() => Done(ex.Message, true));
            return null;
        }
    }

    public async Task<bool> ReviewAsync()
    {
        if (_selected is not { } info || !Begin()) return false;
        try
        {
            var checks = await _hub.Service.ReviewAsync(info, Actor(), BuildCheckAsync).ConfigureAwait(false);
            var passed = checks.All(c => c.Passed);
            await _hub.RefreshAsync().ConfigureAwait(false);
            _ui.Post(() =>
            {
                Selected = Releases.FirstOrDefault(r => r.ReleaseId == info.ReleaseId);
                Checks.Clear();
                foreach (var c in checks) Checks.Add(c);
                Done(passed ? $"Entwicklungsprüfung v{info.Version} abgeschlossen – bereit zur Freigabe." : "Entwicklungsprüfung nicht bestanden.", !passed);
            });
            return passed;
        }
        catch (Exception ex) when (ex is ReleaseRuleException or ReleasePackageException or IOException or System.Data.Common.DbException)
        {
            _ui.Post(() => Done(ex.Message, true));
            return false;
        }
    }

    public async Task<bool> ReleaseAsync()
    {
        if (_selected is not { } info || !Begin()) return false;
        try
        {
            await _hub.Service.ReleaseAsync(info, Actor(), Confirmed).ConfigureAwait(false);
            await _hub.RefreshAsync().ConfigureAwait(false);
            _ui.Post(() =>
            {
                Selected = Releases.FirstOrDefault(r => r.ReleaseId == info.ReleaseId);
                Done($"Version {info.Version} ausdrücklich freigegeben. Laufende Prüflinge behalten ihre Version; neue gilt ab dem nächsten Prüfling.", false);
            });
            return true;
        }
        catch (Exception ex) when (ex is ReleaseRuleException or ReleasePackageException or IOException or System.Data.Common.DbException)
        {
            _ui.Post(() => Done(ex.Message, true));
            return false;
        }
    }

    private Actor Actor() => new(_session.User?.Upn ?? "", _session.Rights);

    private bool Begin()
    {
        if (_busy) return false;
        _busy = true;
        Status = "Bitte warten …";
        StatusIsError = false;
        RaiseCommands();
        return true;
    }

    private void Done(string message, bool error)
    {
        _busy = false;
        Status = message;
        StatusIsError = error;
        RaiseCommands();
        _ = LoadAuditAsync();
    }

    /// <summary>Codeartefakte bauen (Bibliotheksbausteine und CodeBlocks im Ablauf).</summary>
    private async Task<IReadOnlyList<ReviewCheck>> BuildCheckAsync(TestProject project, CancellationToken ct)
    {
        var d = project.Definition;
        var requests = new List<(string Name, CodeRequest Request)>();
        foreach (var b in d.Library) requests.Add(($"Baustein {b.Name} ({b.Language})", new CodeRequest(b.Language, b.Source, b.Ports) { Assemblies = d.Assemblies }));
        foreach (var s in d.AllNodes().OfType<StepDefinition>().Where(s => s.BlockType == BlockCatalog.Code))
        {
            var ports = CodeBlockConverter.VisibleVariables(d, s.Id).Select(v => new CodePort(v.Name, v.Type, PortDirection.InOut)).ToList();
            requests.Add(($"CodeBlock {s.Name}", new CodeRequest(BlockCatalog.ParseLanguage(s.Parameters.GetValueOrDefault("Sprache") ?? "C#"),
                s.Parameters.GetValueOrDefault("Quelltext") ?? "", ports)
            { Assemblies = d.Assemblies }));
        }
        if (requests.Count == 0) return [new ReviewCheck("Codeartefakte", true, "Keine Codeartefakte im Paket.")];
        if (_editor.CodeRuntime is not { } runtime) return [new ReviewCheck("Codeartefakte bauen", false, "Keine Ausführungsumgebung für Code konfiguriert.")];
        var checks = new List<ReviewCheck>();
        foreach (var (name, request) in requests)
        {
            try
            {
                var r = await runtime.CompileAsync(request, ct).ConfigureAwait(false);
                checks.Add(new ReviewCheck(name, r.Success, r.Success ? $"Build OK ({r.RuntimeInfo})" : string.Join("; ", r.Diagnostics.Where(x => x.IsError).Take(3))));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                checks.Add(new ReviewCheck(name, false, ex.Message));
            }
        }
        return checks;
    }

    private string SuggestVersion()
    {
        var versions = _hub.All.Where(r => r.ProjectName == ProjectName).Select(r => r.Version).Append(DraftSource()?.Definition.Version ?? "1.0");
        var max = versions.Select(v => Version.TryParse(v.Count(c => c == '.') == 0 ? v + ".0" : v, out var p) ? p : new Version(0, 0)).Max()!;
        return $"{max.Major}.{max.Minor + 1}";
    }

    private async Task LoadPackageAsync(ReleaseInfo? info)
    {
        _ui.Post(Package.Clear);
        if (info is null) return;
        try
        {
            var opened = await _hub.Service.OpenAsync(info).ConfigureAwait(false);
            _ui.Post(() =>
            {
                if (!ReferenceEquals(_selected, info)) return;
                Package.Clear();
                foreach (var e in opened.Manifest.Entries)
                {
                    var ok = opened.Problems.All(p => !p.Contains($"'{e.Name}'", StringComparison.Ordinal));
                    Package.Add(new PackageRow(e.Area, e.Name, e.Version, e.Sha256.Length > 12 ? e.Sha256[..12] + "…" : e.Sha256, ok));
                }
                if (!opened.IsIntact) Done("Paket nicht unversehrt: " + string.Join("; ", opened.Problems), true);
            });
        }
        catch (Exception ex) when (ex is ReleasePackageException or IOException or System.Data.Common.DbException)
        {
            _ui.Post(() => Done(ex.Message, true));
        }
    }

    public async Task LoadAuditAsync()
    {
        try
        {
            var entries = await _hub.Service.Store.ReadAuditAsync().ConfigureAwait(false);
            var verify = AuditChain.Verify(entries);
            _ui.Post(() =>
            {
                Audit.Clear();
                foreach (var e in entries.OrderByDescending(e => e.Sequence).Take(200)) Audit.Add(e);
                AuditState = verify is null ? $"Hash-Kette geprüft ({entries.Count} Einträge, unverändert)" : "WARNUNG: " + verify;
            });
        }
        catch (Exception ex) when (ex is IOException or System.Data.Common.DbException or InvalidOperationException)
        {
            _ui.Post(() => AuditState = "Audit-Log nicht lesbar: " + ex.Message);
        }
    }

    private void Rebuild()
    {
        RebuildPrograms();
        RebuildReleaseSelection();
        OnPropertiesChanged(nameof(Programs), nameof(SelectedProgramName), nameof(ProgramStatus));
        OnPropertiesChanged(nameof(StoreError), nameof(StoreName), nameof(ProjectName), nameof(CompareTargets));
        RebuildStations();
    }

    private void RebuildPrograms()
    {
        var names = _hub.All.Select(r => r.ProjectName)
            .Concat(_stations.SelectMany(s => s.Coordinator?.Setup.ExampleProjects ?? []).Select(p => p.Definition.Name))
            .Append(_editor.Project.Definition.Name).Distinct().OrderBy(n => n).ToList();
        Programs.Clear();
        foreach (var name in names)
        {
            var latest = _hub.All.Where(r => r.ProjectName == name).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            var released = _hub.CurrentInfo(name);
            Programs.Add(new(name, released is not null ? $"Freigegeben v{released.Version}" : latest is not null ? latest.StateText : "Entwurf · kein Release vorhanden"));
        }
    }

    private void RebuildReleaseSelection()
    {
        var selectedId = _selected?.ReleaseId;
        Releases.Clear();
        foreach (var r in _hub.All.Where(r => r.ProjectName == ProjectName).OrderByDescending(r => r.CreatedAt)) Releases.Add(r);
        var match = Releases.FirstOrDefault(r => r.ReleaseId == selectedId);
        if (match is not null && !ReferenceEquals(match, _selected)) Selected = match;
        if (_selected is null && Releases.Count > 0) Selected = Releases[0];
        if (_selected is not null && Releases.All(r => r.ReleaseId != _selected.ReleaseId)) Selected = null;
    }

    private void RebuildStations()
    {
        StationVersions.Clear();
        var released = _hub.CurrentInfo(ProjectName);
        foreach (var s in _stations)
        {
            var text = s.IsRunning ? $"läuft auf {s.RunningVersionText}" : released is null ? "bereit · keine freigegebene Version" : $"bereit · startet mit v{released.Version}";
            StationVersions.Add(new StationVersionRow(s.Name, text));
        }
    }

    private void BuildStages()
    {
        Stages.Clear();
        var r = _selected;
        string D(DateTimeOffset? t) => t is { } x ? x.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "";
        var state = r?.State ?? ReleaseState.Draft;
        Stages.Add(new StageRow(1, "Entwurf", r is null ? "" : $"{r.CreatedBy} · {D(r.CreatedAt)}", r is not null, state == ReleaseState.Draft));
        Stages.Add(new StageRow(2, "Entwicklungsprüfung", r?.ReviewedAt is null ? (state == ReleaseState.Draft ? "Ausstehend" : "") : $"{r.ReviewedBy} · {D(r.ReviewedAt)}",
            state is ReleaseState.Review or ReleaseState.Released or ReleaseState.Archived, state == ReleaseState.Review));
        Stages.Add(new StageRow(3, state == ReleaseState.Archived ? "Archiviert" : "Freigabe", r?.ReleasedAt is null ? "Ausstehend" : $"{r.ReleasedBy} · {D(r.ReleasedAt)}",
            state is ReleaseState.Released or ReleaseState.Archived, state == ReleaseState.Released));
    }

    private void RaiseIdentity()
    {
        OnPropertiesChanged(nameof(UserName), nameof(UserUpn), nameof(IdentitySource), nameof(IsConfirmed), nameof(Rights),
            nameof(IsDevelopmentMode), nameof(CanConfirm), nameof(ReleaseByText));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        CreateDraftCommand.RaiseCanExecuteChanged();
        ReviewCommand.RaiseCanExecuteChanged();
        ReleaseCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanConfirm));
    }
}
