/**
 * @file
 * @brief Stellt Release-Lebenszyklus, Entwicklungsprüfung, Freigabe und Prüfplatzversionen dar.
 */

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
public sealed class ReleaseProgramRow(string name, string status) : ObservableObject
{
    private string _status = status;
    public string Name { get; } = name;
    public string Status { get => _status; set => SetProperty(ref _status, value); }
}

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
        LoadInEditorCommand = new RelayCommand(() => _ = LoadInEditorAsync(), () => CanLoadInEditor);
        hub.Changed += () => _ui.Post(Rebuild);
        session.Changed += () => _ui.Post(RaiseIdentity);
        editor.ProjectReplaced += () => _ui.Post(Rebuild);
        foreach (var station in stations)
            station.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(StationViewModel.AvailablePrograms)) _ui.Post(Rebuild);
                if (eventArgs.PropertyName is nameof(StationViewModel.RunningVersionText) or nameof(StationViewModel.IsRunning)) _ui.Post(RebuildStations);
            };
        Sources = new ProgramSourcesViewModel(hub, editor, session, ui, stations);
        Rebuild();
        RebuildStations();
    }

    public ProgramSourcesViewModel Sources { get; }

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
    public ReleaseProgramRow? SelectedProgram
    {
        get => Programs.FirstOrDefault(program => program.Name == ProjectName);
        set { if (value is not null) SelectedProgramName = value.Name; }
    }
    public string ProgramStatus => Programs.FirstOrDefault(releaseProgramRow => releaseProgramRow.Name == ProjectName)?.Status ?? "Kein Release vorhanden";

    private TestProject? DraftSource() => _editor.Project.Definition.Name == ProjectName ? _editor.Project
        : _stations.SelectMany(station => station.Coordinator?.Setup.ExampleProjects ?? []).FirstOrDefault(project => project.Definition.Name == ProjectName);
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
            OnPropertiesChanged(nameof(SelectedTitle), nameof(SelectedSourceText), nameof(ReviewReport), nameof(CanConfirm), nameof(ReleaseByText), nameof(ReleaseInstruction), nameof(CompareTargets));
        }
    }

    public string SelectedTitle => _selected is null ? "Kein Release gewählt" : $"Paket-Inhalt – v{_selected.Version} ({_selected.StateText})";

    public string SelectedSourceText => _selected?.Source is { } source
        ? $"{source.Source.Provider} · {source.Source.Repository} · {source.Source.Line} · {source.Source.ProjectPath}\nRevision: {source.Revision} · Repository-Identität: {source.RepositoryIdentity}"
        : "Lokaler Entwurf / ältere Freigabe ohne Remote-Quellnachweis";

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

    public string ReleaseByText => _selected is { ReleasedAt: { } time, ReleasedBy: { Length: > 0 } by }
        ? $"{by} · {time.ToLocalTime():dd.MM.yyyy HH:mm}" : "Noch nicht freigegeben";
    public string ReleaseInstruction => _selected?.State switch
    {
        ReleaseState.Draft => "Zuerst die Entwicklungsprüfung durchführen. Danach kann diese Version ausdrücklich freigegeben werden.",
        ReleaseState.Review => "Prüfbericht kontrollieren, Freigabe bestätigen und ausdrücklich freigeben. Dafür ist das Recht Freigeben erforderlich.",
        ReleaseState.Released => "Diese Version ist freigegeben und steht den zugeordneten Prüfplätzen zur Verfügung.",
        ReleaseState.Archived => "Diese frühere Freigabe ist archiviert. Für neue Prüfungen die aktuelle freigegebene Version wählen.",
        _ => "Zuerst ein Testprogramm und eine Version auswählen.",
    };

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
    public string IdentitySource => _session.User is not { } userIdentity ? "" : userIdentity.IsConfirmed ? "Firmenkonto bestätigt · Microsoft Entra ID" : _session.Provider is LocalPasswordIdentityProvider ? "Lokaler Passwortzugang (keine Entra-Bestätigung)" : "Simulierte Anmeldung (nicht bestätigt)";
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
        _ui.Post(() => _ = Sources.RefreshAsync());
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
            _ui.Post(() => Selected = Releases.FirstOrDefault(release => release.ReleaseId == info.ReleaseId));
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
            var passed = checks.All(reviewCheck => reviewCheck.Passed);
            await _hub.RefreshAsync().ConfigureAwait(false);
            _ui.Post(() =>
            {
                Selected = Releases.FirstOrDefault(release => release.ReleaseId == info.ReleaseId);
                Checks.Clear();
                foreach (var reviewCheck in checks) Checks.Add(reviewCheck);
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
                Selected = Releases.FirstOrDefault(release => release.ReleaseId == info.ReleaseId);
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
        var definition = project.Definition;
        var requests = new List<(string Name, CodeRequest Request)>();
        foreach (var codeBlock in definition.Library) requests.Add(($"Baustein {codeBlock.Name} ({codeBlock.Language})", new CodeRequest(codeBlock.Language, codeBlock.Source, codeBlock.Ports) { Assemblies = definition.Assemblies }));
        foreach (var step in definition.EnabledNodes().OfType<StepDefinition>().Where(step2 => step2.BlockType == BlockCatalog.Code))
        {
            var ports = CodeBlockConverter.VisibleVariables(definition, step.Id).Select(variable => new CodePort(variable.Name, variable.Type, PortDirection.InOut)).ToList();
            requests.Add(($"CodeBlock {step.Name}", new CodeRequest(BlockCatalog.ParseLanguage(step.Parameters.GetValueOrDefault("Sprache") ?? "C#"),
                step.Parameters.GetValueOrDefault("Quelltext") ?? "", ports)
            { Assemblies = definition.Assemblies }));
        }
        if (requests.Count == 0) return [new ReviewCheck("Codeartefakte", true, "Keine Codeartefakte im Paket.")];
        if (_editor.CodeRuntime is not { } runtime) return [new ReviewCheck("Codeartefakte bauen", false, "Keine Ausführungsumgebung für Code konfiguriert.")];
        var checks = new List<ReviewCheck>();
        foreach (var (name, request) in requests)
        {
            try
            {
                var compileResult = await runtime.CompileAsync(request, ct).ConfigureAwait(false);
                checks.Add(new ReviewCheck(name, compileResult.Success, compileResult.Success ? $"Build OK ({compileResult.RuntimeInfo})" : string.Join("; ", compileResult.Diagnostics.Where(diagnostic => diagnostic.IsError).Take(3))));
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
        var versions = _hub.All.Where(release => release.ProjectName == ProjectName).Select(release => release.Version).Append(DraftSource()?.Definition.Version ?? "1.0");
        var max = versions.Select(version => Version.TryParse(version.Count(character => character == '.') == 0 ? version + ".0" : version, out var version2) ? version2 : new Version(0, 0)).Max()!;
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
                foreach (var manifestEntry in opened.Manifest.Entries)
                {
                    var ok = opened.Problems.All(problem => !problem.Contains($"'{manifestEntry.Name}'", StringComparison.Ordinal));
                    Package.Add(new PackageRow(manifestEntry.Area, manifestEntry.Name, manifestEntry.Version, manifestEntry.Sha256.Length > 12 ? manifestEntry.Sha256[..12] + "…" : manifestEntry.Sha256, ok));
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
                foreach (var auditEntry in entries.OrderByDescending(auditEntry2 => auditEntry2.Sequence).Take(200)) Audit.Add(auditEntry);
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
        OnPropertiesChanged(nameof(Programs), nameof(SelectedProgramName), nameof(SelectedProgram), nameof(ProgramStatus));
        OnPropertiesChanged(nameof(StoreError), nameof(StoreName), nameof(ProjectName), nameof(CompareTargets));
        RebuildStations();
        LoadInEditorCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanLoadInEditor));
    }

    private void RebuildPrograms()
    {
        var names = _hub.All.Select(release => release.ProjectName)
            .Concat(_stations.SelectMany(station => station.Coordinator?.Setup.ExampleProjects ?? []).Select(project => project.Definition.Name))
            .Append(_editor.Project.Definition.Name).Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct().OrderBy(name => name).ToList();
        var rows = new List<ReleaseProgramRow>();
        foreach (var name in names)
        {
            var latest = _hub.All.Where(release => release.ProjectName == name).OrderByDescending(release => release.CreatedAt).FirstOrDefault();
            var released = _hub.CurrentInfo(name);
            rows.Add(new(name, released is not null ? $"Freigegeben v{released.Version}" : latest is not null ? latest.StateText : "Entwurf · kein Release vorhanden"));
        }
        UpdatePrograms(rows);
    }

    /// <summary>Do not clear a bound selector on polling: preserve rows and restore changed selections by item identity.</summary>
    private void UpdatePrograms(IReadOnlyList<ReleaseProgramRow> rows)
    {
        for (var index2 = 0; index2 < rows.Count; index2++)
        {
            var existing = Programs.FirstOrDefault(program => program.Name == rows[index2].Name);
            if (existing is null) Programs.Insert(index2, rows[index2]);
            else
            {
                var index = Programs.IndexOf(existing);
                if (index != index2) Programs.Move(index, index2);
                existing.Status = rows[index2].Status;
            }
        }
        while (Programs.Count > rows.Count) Programs.RemoveAt(Programs.Count - 1);
    }

    private void RebuildReleaseSelection()
    {
        var selectedId = _selected?.ReleaseId;
        Releases.Clear();
        foreach (var release in _hub.All.Where(release2 => release2.ProjectName == ProjectName).OrderByDescending(release2 => release2.CreatedAt)) Releases.Add(release);
        var match = Releases.FirstOrDefault(release2 => release2.ReleaseId == selectedId);
        if (match is not null && !ReferenceEquals(match, _selected)) Selected = match;
        if (_selected is null && Releases.Count > 0) Selected = Releases[0];
        if (_selected is not null && Releases.All(release2 => release2.ReleaseId != _selected.ReleaseId)) Selected = null;
    }

    private void RebuildStations()
    {
        StationVersions.Clear();
        var released = _hub.CurrentInfo(ProjectName);
        foreach (var station in _stations)
        {
            var text = station.IsRunning ? $"läuft auf {station.RunningVersionText}" : released is null ? "bereit · keine freigegebene Version" : $"bereit · startet mit v{released.Version}";
            StationVersions.Add(new StationVersionRow(station.Name, text));
        }
    }

    private void BuildStages()
    {
        Stages.Clear();
        var selected = _selected;
        string D(DateTimeOffset? timestamp) => timestamp is { } timestamp2 ? timestamp2.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "";
        var state = selected?.State ?? ReleaseState.Draft;
        Stages.Add(new StageRow(1, "Entwurf", selected is null ? "" : $"{selected.CreatedBy} · {D(selected.CreatedAt)}", selected is not null, state == ReleaseState.Draft));
        Stages.Add(new StageRow(2, "Entwicklungsprüfung", selected?.ReviewedAt is null ? (state == ReleaseState.Draft ? "Ausstehend" : "") : $"{selected.ReviewedBy} · {D(selected.ReviewedAt)}",
            state is ReleaseState.Review or ReleaseState.Released or ReleaseState.Archived, state == ReleaseState.Review));
        Stages.Add(new StageRow(3, state == ReleaseState.Archived ? "Archiviert" : "Freigabe", selected?.ReleasedAt is null ? "Ausstehend" : $"{selected.ReleasedBy} · {D(selected.ReleasedAt)}",
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
        LoadInEditorCommand.RaiseCanExecuteChanged();
        OnPropertiesChanged(nameof(CanConfirm), nameof(CanLoadInEditor));
    }
}
