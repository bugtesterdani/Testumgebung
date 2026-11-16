using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class StationViewModel
{
    private sealed record ReferenceRunRequest(string Id, ResultRunKind Kind, ReferenceCheckConfig Config);

    private readonly object _referenceLock = new();
    private ReferenceCheckState _referenceState = new();
    private ReferenceCheckConfig? _referenceConfig;
    private ReferenceRunRequest? _referenceRun;
    private DutRunCompleted? _referenceResult;
    private bool _loadingReference;
    private string _referenceScan = "";
    private RelayCommand? _startReferenceCommand;

    private ReferenceCheckConfig? ReferenceConfig => _coordinator?.Setup.Station(Name)?.ReferenceCheck;

    public bool HasReferenceCheck => ReferenceConfig?.Enabled == true;

    public string ReferenceScan
    {
        get => _referenceScan;
        set => SetProperty(ref _referenceScan, value ?? "");
    }

    public RelayCommand StartReferenceCommand => _startReferenceCommand ??= new(() => _ = StartReferenceAsync());

    private ReferenceCheckState ReferenceState(ReferenceCheckConfig config)
    {
        if (!ReferenceEquals(_referenceConfig, config))
        {
            _referenceConfig = config;
            _referenceState = new();
        }
        return _referenceState;
    }

    private bool ReferenceDue(ReferenceCheckConfig config)
    {
        lock (_referenceLock) return ReferenceState(config).IsDue(config, _clock.Now);
    }

    internal bool BlockProductionForReference()
    {
        if (ReferenceConfig is not { Enabled: true } config || !ReferenceDue(config)) return false;
        Error = config.Validate() ?? "Produktionsstart gesperrt. Gut- und Schlechtmuster prüfen.";
        return true;
    }

    private Func<bool>? ProductionReferenceGuard()
        => !_simulationOnly && _referenceRun is null && ReferenceConfig is { Enabled: true } config
            ? () => !ReferenceDue(config) : null;

    public async Task StartReferenceAsync()
    {
        if (IsRunning || PairContext is not null || _loadingReference || ReferenceConfig is not { Enabled: true } config) return;
        if (config.Validate() is { } error)
        {
            Error = error;
            return;
        }
        var id = ReferenceScan.Trim();
        var kind = config.Match(id);
        if (kind == ResultRunKind.Production)
        {
            Error = "Kennung eines konfigurierten Gut- oder Schlechtmusters scannen.";
            return;
        }
        if (_releases is not null && UseDraft)
        {
            Error = "Für die Referenzprüfung eine freigegebene Prüfsoftware verwenden.";
            return;
        }

        _loadingReference = true;
        try
        {
            _referenceResult = null;
            await StartStationProgramAsync(new(id, kind, config)).ConfigureAwait(true);
        }
        finally
        {
            _loadingReference = false;
            ReferenceScan = "";
        }
    }

    private Task RunReferenceAsync(TestProject project, ReleaseInfo? release, ReferenceRunRequest reference, SelectionOutcome? outcome)
    {
        if (!ReferenceEquals(ReferenceConfig, reference.Config))
        {
            Error = "Die Referenzkonfiguration wurde geändert. Referenzkennung erneut scannen.";
            return Task.CompletedTask;
        }
        var groups = project.Definition.Loop.Descendants().OfType<GroupDefinition>().Where(group => group.IsDutRun).ToArray();
        if (groups.Length == 0)
        {
            Error = "Für die Referenzprüfung muss die Prüfsoftware einen Prüflingstest enthalten.";
            return Task.CompletedTask;
        }
        var presets = ReferencePresets(reference.Id, groups, outcome);
        return RunProjectWithReferenceAsync(project, release, presets, 1, $"Referenzprüfung · {reference.Id}",
            false, false, outcome?.PythonVenv, reference);
    }

    private static Dictionary<string, object?> ReferencePresets(string id, IReadOnlyList<GroupDefinition> groups, SelectionOutcome? outcome)
    {
        var presets = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (outcome is not null)
        {
            foreach (var (key, value) in outcome.Parameters) presets[key] = value;
            if (outcome.Order is { Length: > 0 } order) presets["Auftrag"] = order;
        }
        foreach (var group in groups) presets[group.DutIdVariable] = id;
        return presets;
    }

    private void RecordReferenceProgress(EngineEvent engineEvent)
    {
        if (_simulationOnly || engineEvent is not DutRunCompleted completed) return;
        if (_referenceRun is not null)
        {
            _referenceResult = completed;
            return;
        }
        if (ReferenceConfig is not { Enabled: true } config) return;
        lock (_referenceLock) ReferenceState(config).RecordProduction();
    }

    private void FinishReferenceRun(RunStatus status, bool safe)
    {
        if (_referenceRun is not { } reference) return;
        var sampleStatus = safe && !status.OperatorAbort && status.Rating != ResultRating.Error
            && _referenceResult is { } result && result.DutId == reference.Id
            ? result.Status : new RunStatus(ResultRating.Error, status.OperatorAbort);
        bool accepted;
        lock (_referenceLock)
            accepted = ReferenceState(reference.Config).RecordReference(reference.Config, reference.Kind, sampleStatus, _clock.Now);
        var due = ReferenceDue(reference.Config);
        _ui.Post(() => LogSelection(accepted ? "Referenzprüfung bestätigt" : "Referenzprüfung nicht bestätigt",
            reference.Id, accepted ? ResultRating.Pass : ResultRating.Error, due ? "Referenzprüfung erforderlich" : "Produktion freigegeben"));
    }

    internal void RefreshReferenceCheck() => OnPropertyChanged(nameof(HasReferenceCheck));
}
