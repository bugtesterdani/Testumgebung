using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;
using Testumgebung.Engine.Blocks;
using Testumgebung.Domain.Storage;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Releases;
using Testumgebung.Persistence.Stations;
using Testumgebung.Hardware;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class StationViewModel
{
    // ------------------------------------------------------------------ Steuerung

    /// <summary>Editorstand ausschließlich mit simulierten Geräten ausführen, unabhängig von der Stationszuordnung.</summary>
    public Task StartSimulationAsync(string? variant = null)
    {
        if (!CanStart || Selection.IsActive) return Task.CompletedTask;
        var presets = variant is null ? null : new Dictionary<string, object?> { [LimitSetTable.VariantParameter] = variant };
        return RunProjectAsync(_serializer.Clone(_projectSource()), null, presets, null, simulationOnly: true);
    }

    /// <summary>Startet den Hauptablauf mit einer unveränderlichen Kopie der aktuellen Projektversion.</summary>
    public async Task StartAsync()
    {
        if (IsRunning) return;
        // Auswahl-/Auftragsablauf: „Start“ öffnet die Erfassung bzw. startet den zugeordneten Test
        if (Selection.IsActive)
        {
            await Selection.StartSelectedAsync().ConfigureAwait(true);
            return;
        }
        if (SelectionConfig is { Enabled: true })
        {
            Selection.Activate();
            if (Selection.CanSubmitOrder) await Selection.StartSelectedAsync().ConfigureAwait(true);
            return;
        }
        if (BlockProductionForReference()) return;
        await StartStationProgramAsync().ConfigureAwait(true);
    }

    private async Task StartStationProgramAsync(ReferenceRunRequest? reference = null)
    {
        TestProject project;
        ReleaseInfo? release = null;
        try
        {
            if (reference is not null && Selection.Outcome is { Test: { } selected } outcome)
            {
                (project, release) = await LoadTestAsync(selected).ConfigureAwait(true);
                await RunReferenceAsync(project, release, reference, outcome).ConfigureAwait(true);
                return;
            }
            if (_releases is null || UseDraft)
            {
                project = _serializer.Clone(_projectSource());
            }
            else
            {
                await _releases.RefreshAsync().ConfigureAwait(true);
                var name = _projectSource().Definition.Name;
                var resolved = _releases.ResolveStation(ProgramStationId, name);
                if (resolved is not null)
                {
                    ProgramVersionWarning = resolved.Warning;
                    (release, project) = await _releases.OpenResolvedAsync(resolved.Release).ConfigureAwait(true);
                }
                else
                {
                    var released = _releases.GetReleased(name) ?? throw new ReleasePackageException($"Keine freigegebene Version von „{name}“. Zentrales Standardprogramm oder Platzzuordnung in Releases einrichten.");
                    (release, project) = released;
                }
            }
        }
        catch (Exception ex) when (ex is ProjectFormatException or ReleasePackageException or InvalidOperationException)
        {
            Error = ex.Message;
            return;
        }
        if (reference is null) await RunProjectAsync(project, release, null, null).ConfigureAwait(true);
        else await RunReferenceAsync(project, release, reference, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Lauf eines Projekts (freigegebene Version oder Entwurf) bis zum Ende. Aus der Auswahl: mit Vorbelegung
    /// (Kennung, Auftrag, Prüfparameter) und genau einem Prüfling.
    /// </summary>
    internal async Task RunProjectAsync(TestProject project, ReleaseInfo? release, IReadOnlyDictionary<string, object?>? presets, int? dutLimit,
        string? selectionNote = null, bool simulationOnly = false, bool orderSession = false, string? pythonVenv = null)
        => await RunProjectWithReferenceAsync(project, release, presets, dutLimit, selectionNote, simulationOnly, orderSession, pythonVenv, null).ConfigureAwait(true);

    private async Task RunProjectWithReferenceAsync(TestProject project, ReleaseInfo? release, IReadOnlyDictionary<string, object?>? presets,
        int? dutLimit, string? selectionNote, bool simulationOnly, bool orderSession, string? pythonVenv, ReferenceRunRequest? reference)
    {
        if (IsRunning || (!simulationOnly && reference is null && BlockProductionForReference())) return;
        _referenceRun = reference;
        var completion = _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _run = null;
        _runTask = null;
        _abortBeforeEngineStart = false;
        _simulationOnly = simulationOnly;
        OnPropertyChanged(nameof(IsSimulation));
        RaiseRunState();
        try
        {
            await RunProjectCoreAsync(project, release, presets, dutLimit, selectionNote, orderSession, pythonVenv).ConfigureAwait(true);
        }
        finally
        {
            try { await DrainStorageAsync().ConfigureAwait(true); }
            finally
            {
                _simulationOnly = false;
                _referenceRun = null;
                completion.TrySetResult();
                OnPropertyChanged(nameof(IsSimulation));
                RaiseRunState();
            }
        }
    }

    private DeviceConfig[] CalibrationDevices() => _simulationOnly || _coordinator is null
        ? []
        : StationHardware.CalibrationDevices(_coordinator.Setup, Name);


    private IReadOnlyDictionary<string, string> CalibrationResourceIds()
    {
        if (_simulationOnly || _coordinator?.Setup.Station(Name) is not { } station)
            return new Dictionary<string, string>();
        return station.Resources.ToDictionary(resource => resource.Name, resource => resource.Device, StringComparer.Ordinal);
    }

    private HardwareRegistry? BuildRunRegistry(TestProject project)
    {
        HardwareRegistry registry;
        if (_coordinator is not null && !_simulationOnly)
        {
            // Parallelbetrieb: Zyklusrisiko mit den laufenden Abläufen der anderen Prüfplätze → Start verweigert.
            if (_coordinator.TryBeginRun(Name, project.Definition) is { } refusal)
            {
                Error = refusal;
                return null;
            }
            try
            {
                registry = _coordinator.BuildRegistry(Name, project, seed: Name.GetHashCode());
            }
            catch (StationSetupException ex)
            {
                _coordinator.EndRun(Name);
                Error = ex.Message;
                return null;
            }
        }
        else
        {
            registry = SampleProjects.CreateSimulation(project, seed: Name.GetHashCode()).Registry;
        }

        return registry;
    }

    private async Task<bool> ValidateRunProjectAsync(TestProject project, ReleaseInfo? release)
    {
        if (release is not null && _releases is not null)
        {
            var differences = await _releases.Service.VerifyEnvironmentAsync(release).ConfigureAwait(true);
            if (differences.Count > 0)
            {
                Error = "Freigabe passt nicht zur Ausführungsumgebung: " + string.Join(" ", differences);
                return false;
            }
        }
        var diagnostics = project.Definition.Validate();
        if (diagnostics.Count > 0)
        {
            Error = string.Join(Environment.NewLine, diagnostics);
            return false;
        }

        return true;
    }

    private TestRunOptions RunOptions(HardwareRegistry registry, IReadOnlyDictionary<string, object?>? presets,
        IReadOnlyDictionary<string, object?>? rootPresets, int? dutLimit, bool orderSession, string? pythonVenv, Action<ResourceBinding> onDeviceAccess)
        => new()
        {
            Clock = _clock,
            Resources = _resources,
            Hardware = registry,
            AccessJournal = _coordinator?.Journal,
            OnDeviceAccess = onDeviceAccess,
            Operator = this,
            Debugger = Debug.Controller,
            StationName = Name,
            CodeRuntime = _codeRuntime,
            PythonVenv = pythonVenv,
            PairedStation = PairContext,
            // Beide Seiten behalten bis zum nächsten gemeinsamen Auftrag dieselbe Version.
            VersionSource = _referenceRun is null && PairContext is null ? this : null,
            Presets = presets,
            SessionPresets = rootPresets,
            ContinuousLoop = orderSession,
            SingleLoopIteration = _referenceRun is not null,
            DutLimit = dutLimit,
            CanBeginDut = ProductionReferenceGuard(),
            EventHistoryLimit = 2000,
            RetainWaveformsInHistory = false,
            DevelopmentRun = _runIsDraft,
        };

    private async Task RunProjectCoreAsync(TestProject project, ReleaseInfo? release, IReadOnlyDictionary<string, object?>? presets, int? dutLimit,
        string? selectionNote, bool orderSession, string? pythonVenv)
    {
        _runIsDraft = _simulationOnly || (_releases is not null && UseDraft);
        _runningProject = project;
        _runningReleaseId = release?.ReleaseId;
        _rejectedReleaseId = null;
        PendingVersionText = null;
        ActiveRelease = release;
        if (!TryPreparePresets(project, ref presets, orderSession, out var rootPresets)) return;
        if (!await ValidateRunProjectAsync(project, release).ConfigureAwait(true)) return;

        var devices = CalibrationDevices();
        var today = DateOnly.FromDateTime(_clock.Now.LocalDateTime);
        if (StationHardware.CalibrationRefusal(devices, today, _runIsDraft) is { } refusal)
        {
            Error = refusal;
            return;
        }

        var registry = BuildRunRegistry(project);
        if (registry is null) return;

        var runTask = StartPreparedRun(project, registry, presets, rootPresets, dutLimit, selectionNote, orderSession, pythonVenv, devices, today);

        RunStatus status;
        try
        {
            status = await runTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _coordinator?.EndRun(Name);
            var problems = await SafeStateAsync().ConfigureAwait(false);
            _ui.Post(() =>
            {
                Error = ex.Message + (problems.Count > 0 ? " " + string.Join(" ", problems) : "");
                RaiseRunState();
            });
            return;
        }
        _coordinator?.EndRun(Name);
        // Der sichere Gerätezustand wird vor dem abschließenden UI-Status geprüft.
        // Ein abgeschlossenes Prüfergebnis bestätigt allein noch keinen sicheren Ausgangszustand.
        var safeProblems = await SafeStateAsync().ConfigureAwait(false);
        FinishReferenceRun(status, safeProblems.Count == 0);
        _ui.Post(() => CompleteRunDisplay(status, safeProblems));
    }

    private bool TryPreparePresets(TestProject project, ref IReadOnlyDictionary<string, object?>? presets, bool orderSession,
        out IReadOnlyDictionary<string, object?>? rootPresets)
    {
        rootPresets = null;
        IReadOnlyDictionary<string, object?> variantPresets;
        try
        {
            variantPresets = LimitSetTable.Resolve(project, presets?.GetValueOrDefault(LimitSetTable.VariantParameter) as string);
        }
        catch (ProjectFormatException ex)
        {
            Error = ex.Message;
            LogSelection("Grenzwertsatz fehlt oder ist ungültig", ex.Message, ResultRating.Error, "Kein Start");
            return false;
        }
        if (variantPresets.Count > 0)
        {
            var combined = new Dictionary<string, object?>(presets!, StringComparer.Ordinal);
            combined.Remove(LimitSetTable.VariantParameter);
            foreach (var (key, value) in variantPresets) combined[key] = value;
            presets = combined;
        }
        rootPresets = orderSession || _referenceRun is not null ? presets : variantPresets;
        return true;
    }

    private Task<RunStatus> StartPreparedRun(TestProject project, HardwareRegistry registry,
        IReadOnlyDictionary<string, object?>? presets, IReadOnlyDictionary<string, object?>? rootPresets,
        int? dutLimit, string? selectionNote, bool orderSession,
        string? pythonVenv, IReadOnlyList<DeviceConfig> devices, DateOnly today)
    {
        PrepareRunDisplay(project, presets, orderSession, selectionNote);
        var resourceIds = CalibrationResourceIds();
        var accessed = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        void TrackDevice(ResourceBinding binding)
        {
            if (resourceIds.TryGetValue(binding.Name, out var deviceId)) accessed.TryAdd(deviceId, 0);
        }
        _run = new TestRun(project.Definition, RunOptions(registry, presets, rootPresets, dutLimit, orderSession, pythonVenv, TrackDevice), runId: $"{Name.Replace(' ', '-')}-{DateTime.Now:HHmmss}");
        if (_results is not null)
        {
            var resolver = _storage;
            var recorder = new ResultRecorder(_run, _results.Journal, resolver, _referenceRun?.Kind ?? ResultRunKind.Production);
            var snapshot = (_coordinator?.Setup.Devices ?? []).Where(device => !device.IsSimulation)
                .Select(device => (device.Id, Result: new ResultDevice(device.Name, device.Calibration?.SerialNumber,
                    device.Calibration?.CalibratedOn, device.Calibration?.DueOn))).ToArray();
            recorder.RunRecorded += recorded =>
            {
                recorded.Devices = snapshot.Where(device => accessed.ContainsKey(device.Id)).Select(device => device.Result).ToArray();
                QueueStorage(recorded, resolver);
            };
        }
        // Engine-Ereignisse kommen aus dem Ausführungskontext. Observable Collections und
        // gebundene Eigenschaften werden erst über den UI-Dispatcher geändert.
        _run.EventRaised += RecordReferenceProgress;
        _run.EventRaised += engineEvent => _ui.Post(() => Handle(engineEvent));
        RunningVersion = project.Definition.Version;
        OnPropertyChanged(nameof(RunningVersionText));
        Hint = "Prüfung läuft.";
        foreach (var warning in StationHardware.CalibrationWarnings(devices, today))
            AddProtocol(new ProtocolEntry(_clock.Now, ProtocolKind.Info, warning, "—", "", "", null, "Kalibrierung"));
        var task = _runTask = Task.Run(_run.RunAsync);
        if (_abortBeforeEngineStart) RequestAbort();
        RaiseRunState();
        return task;
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_storage))]
    private void PrepareRunDisplay(TestProject project, IReadOnlyDictionary<string, object?>? presets,
        bool orderSession, string? selectionNote)
    {
        Error = null;
        _storage = new StorageResolver(project.Definition);
        ApplyLayout(project.Definition.Layout);
        ResetDisplay(project.Definition);
        if (orderSession && presets?.GetValueOrDefault("Auftrag") is string order) CurrentOrder = order;
        if (selectionNote is not null) LogSelection("Auswahl · Kennung zugeordnet", selectionNote, ResultRating.NResult, "OK");
    }

    private void CompleteRunDisplay(RunStatus status, IReadOnlyList<string> safeProblems)
    {
        foreach (var safeProblem in safeProblems)
            AddProtocol(new ProtocolEntry(DateTimeOffset.Now, ProtocolKind.Error, "Sicherer Zustand", "—", "", "", ResultRating.Error, safeProblem));
        if (safeProblems.Count > 0) Error = "Sicherer Zustand nicht für alle Geräte bestätigt – Geräte prüfen: " + string.Join(" ", safeProblems);
        LastStatus = status;
        State = EngineState.Completed;
        StatusText = "Bereit";
        StatusKind = StationStatusKind.Idle;
        Hint = $"Ablauf beendet: {status}. Neuer Start möglich.";
        foreach (var operatorContentViewModel in Contents) operatorContentViewModel.Cancel();
        Contents.Clear();
        _contentById.Clear();
        OnPropertyChanged(nameof(ActiveContent));
        RaiseRunState();
    }

    /// <summary>Lauf für gezielte Ausführung im Debugkontext (eigene Projektkopie, Simulation dieses Prüfplatzes).</summary>
    public TestRun CreateTargetedRun()
    {
        var project = _serializer.Clone(_projectSource());
        var registry = SampleProjects.CreateSimulation(project, seed: Name.GetHashCode()).Registry;
        return new TestRun(project.Definition, new TestRunOptions
        {
            Clock = _clock,
            Resources = _resources,
            Hardware = registry,
            AccessJournal = _coordinator?.Journal,
            Operator = this,
            StationName = Name,
            CodeRuntime = _codeRuntime,
        }, runId: $"{Name.Replace(' ', '-')}-Debug");
    }

    /// <summary>Werkerabbruch. Erneuter Abbruch beendet nur die aktuelle Exit-Gruppe.</summary>
    public void RequestAbort()
    {
        // Werkerabbruch beendet auch den Auswahl-/Auftragsablauf (Abschnitt 5)
        if (Selection.IsActive) Selection.End(IsRunning ? "Werkerabbruch – Auswahl-/Auftragsablauf beendet." : "Auswahl-/Auftragsablauf beendet.");
        if (!IsRunning) return;
        if (_run is null) { _abortBeforeEngineStart = true; return; }
        if (AbortCount == 0) BuildExitSequence(_run);
        _run.RequestAbort();
    }

    private void RaiseRunState()
    {
        OnPropertiesChanged(nameof(IsRunning), nameof(CanStart), nameof(CanAbort), nameof(IsAborting), nameof(UsesSelection), nameof(Hint));
        Selection.RaiseRunning();
        StartCommand.RaiseCanExecuteChanged();
        AbortCommand.RaiseCanExecuteChanged();
        Debug.RaiseRunState();
    }

    /// <summary>Nach jedem Lauf (auch Abbruch/Fehler): Geräte dieses Prüfplatzes in den sicheren Zustand (zusätzlich zu den Exit-Gruppen).</summary>
    private async Task<IReadOnlyList<string>> SafeStateAsync()
    {
        if (_coordinator is null || _simulationOnly) return [];
        try
        {
            return await _coordinator.SafeStateAsync(Name).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or StationSetupException)
        {
            return [ex.Message];
        }
    }

    /// <summary>„ (belegt von Prüfplatz 02)“ – wer die angeforderten Geräte gerade hält.</summary>
    private string Holders(IReadOnlyList<string> names)
    {
        var registry = _run?.Options.Hardware;
        var keys = names.Select(name => registry?.LockKeyOf(name) ?? name).ToHashSet(StringComparer.Ordinal);
        var owners = _resources.Snapshot().Where(ownership => keys.Contains(ownership.Resource) && ownership.OwnerRunId is not null && ownership.OwnerRunId != _run?.RunId)
            .Select(ownership => ownership.OwnerName ?? ownership.OwnerRunId!).Distinct().ToList();
        return owners.Count == 0 ? "" : $" (belegt von {string.Join(", ", owners)})";
    }

    private void ResetDisplay(TestDefinition definition)
    {
        LiveOutputs.Clear();
        LiveWaveform.Clear();
        LiveWaveformTitle = null;
        Overview.Clear();
        _overviewByNode.Clear();
        ExitSequence.Clear();
        ClearMeasurements();
        Contents.Clear();
        _contentById.Clear();
        Protocol.Clear();
        AbortCount = 0;
        DutCount = 0;
        DutRating = ResultRating.NResult;
        DutWerror = false;
        LastStatus = null;
        CurrentDutId = "—";
        CurrentOrder = "—";
        CurrentStepName = "";
        CurrentStepPath = "";

        var dut = FindDutGroup(definition.Loop) ?? definition.Loop;
        var number = 0;
        foreach (var child in dut.Children)
        {
            // Vom Entwickler ausgeblendete Schritte/Gruppen erscheinen nicht in der Ablaufübersicht.
            if (_storage is not null && !_storage.IsEnabled(child.Id, StorageTarget.Display)) continue;
            var item = new OverviewItemViewModel(child.Id, ++number, child.Name);
            Overview.Add(item);
            _overviewByNode[child.Id] = item;
            if (child is GroupDefinition group)
                foreach (var node in group.Descendants()) _overviewByNode[node.Id] = item;
        }

        _dutIdWriters.Clear();
        _orderWriters.Clear();
        foreach (var step in definition.AllNodes().OfType<StepDefinition>())
        {
            if (!step.Parameters.TryGetValue("Ziel", out var target)) continue;
            if (target == dut.DutIdVariable) _dutIdWriters.Add(step.Id);
            if (target == "Auftrag") _orderWriters.Add(step.Id);
        }
        OnPropertiesChanged(nameof(ProgressText), nameof(DutProgress));
    }

    private static GroupDefinition? FindDutGroup(GroupDefinition group)
        => group.IsDutRun ? group : group.Children.OfType<GroupDefinition>().Select(FindDutGroup).FirstOrDefault(group2 => group2 is not null);

    private void BuildExitSequence(TestRun run)
    {
        ExitSequence.Clear();
        var number = 0;
        var frames = run.ExecutionStack.Where(frame => frame.IsGroup).Reverse();
        foreach (var frame in frames)
        {
            if (run.Definition.Find(frame.NodeId) is not GroupDefinition { ExitGroup: { } exit } group) continue;
            var description = string.Join(", ", exit.Children.Take(3).Select(node => node.Name));
            var name = group == run.Definition.Root ? "Hauptablauf Finalization" : $"{exit.Name} ({group.Name})";
            ExitSequence.Add(new ExitItemViewModel(exit.Id, ++number, name, description));
        }
    }
}
