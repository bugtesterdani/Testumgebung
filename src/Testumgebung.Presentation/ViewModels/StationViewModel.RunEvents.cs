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
    // ------------------------------------------------------------------ Ereignisse der Engine

    private void HandleInteractionAndResources(EngineEvent engineEvent)
    {
        switch (engineEvent)
        {
            case OperatorContentShown os:
                HandleOperatorContentShown(os);
                break;

            case OperatorContentCleared cl:
                HandleOperatorContentCleared(cl);
                break;

            case OperatorAbortRequested operatorAbortRequested:
                HandleOperatorAbortRequested(operatorAbortRequested);
                break;

            case ResourceWaitStarted rw:
                HandleResourceWaitStarted(rw);
                break;

            case ResourceAcquired:
                HandleResourceAcquired();
                break;

            case ResourceNotAcquired rn:
                HandleResourceNotAcquired(rn);
                break;

            case LiveOutputReported live:
                LiveOutputs.Apply(live.NodeId, Last(live.Path), live.Update);
                break;

            case Diagnostic dg:
                HandleDiagnostic(dg);
                break;

            case DevelopmentEvent dev:
                HandleDevelopmentEvent(dev);
                break;

            case VersionActivated va:
                HandleVersionActivated(va);
                break;

            case DutRunCompleted dc:
                HandleDutRunCompleted(dc);
                break;

        }
    }

    private void Handle(EngineEvent engineEvent)
    {
        _liveResults.Apply(engineEvent);
        switch (engineEvent)
        {
            case TraceUpdated trace:
                LiveWaveform.SetData(trace.Data);
                LiveWaveformTitle = LiveWaveform.HasEnvelopeViolation ? "Messwertverlauf · FAIL festgestellt · Aufnahme läuft" : "Messwertverlauf · Aufnahme läuft";
                break;
            case StateChanged stateChanged:
                HandleStateChanged(stateChanged);
                break;

            case DutRunStarted dutRunStarted:
                HandleDutRunStarted(dutRunStarted);
                break;

            case StepStarted st:
                HandleStepStarted(st);
                break;

            case GroupEntered ge:
                HandleGroupEntered(ge);
                break;

            case NodeSkipped skipped:
                HandleDisabledNode(skipped);
                break;

            case GroupNotEntered gn:
                HandleGroupNotEntered(gn);
                break;

            case GroupIterationStarted { ResultReset: true } iteration:
                HandleGroupResultReset(iteration);
                break;

            case GroupCompleted gc:
                HandleGroupCompleted(gc);
                break;

            case ExitGroupAborted ea:
                HandleExitGroupAborted(ea);
                break;

            case StepCompleted sc:
                HandleStepCompleted(sc);
                break;

            default:
                HandleInteractionAndResources(engineEvent);
                break;

        }
        Debug.Handle(engineEvent);
    }

    private void HandleStateChanged(StateChanged stateChanged)
    {
        State = stateChanged.State;
        UpdateStatus();
    }

    private void HandleDutRunStarted(DutRunStarted dutRunStarted)
    {
        LiveOutputs.Clear();
        DutCount++;
        DutRating = ResultRating.NResult;
        DutWerror = false;
        CurrentDutId = "—";
        ClearMeasurements();
        foreach (var overviewItemViewModel in Overview)
        {
            overviewItemViewModel.State = ItemState.Pending;
            overviewItemViewModel.Rating = ResultRating.NResult;
        }
        AddProtocol(new ProtocolEntry(dutRunStarted.Time, ProtocolKind.Run, $"Durchlauf {DutCount} – Prüfling gestartet", "—", "", "", null, "Läuft", IsHeader: true));
    }

    private void HandleStepStarted(StepStarted st)
    {
        CurrentStepName = Last(st.Path);
        CurrentStepPath = st.Path;
        MarkOverview(st.NodeId, ItemState.Running, null);
    }

    private void HandleGroupEntered(GroupEntered ge)
    {
        if (_overviewByNode.TryGetValue(ge.NodeId, out var gi) && gi.NodeId == ge.NodeId) gi.State = ItemState.Running;
        SetExitState(ge.NodeId, ItemState.Running, null);
    }

    private void HandleDisabledNode(NodeSkipped skipped)
    {
        var ids = _run?.Definition.Find(skipped.NodeId) is GroupDefinition group
            ? group.Descendants().Select(node => node.Id).Append(group.Id) : [skipped.NodeId];
        foreach (var id in ids)
        {
            if (_overviewByNode.TryGetValue(id, out var item)) item.State = ItemState.Skipped;
            SetExitState(id, ItemState.Skipped, null);
        }
        AddProtocol(new ProtocolEntry(skipped.Time, ProtocolKind.Info, Last(skipped.Path), "—", "", "", null, "Deaktiviert"));
        OnPropertiesChanged(nameof(ProgressText), nameof(DutProgress));
    }

    private void HandleGroupNotEntered(GroupNotEntered gn)
    {
        if (_overviewByNode.TryGetValue(gn.NodeId, out var ni) && ni.NodeId == gn.NodeId) ni.State = ItemState.Skipped;
        SetExitState(gn.NodeId, ItemState.Skipped, null);
    }

    private void HandleGroupResultReset(GroupIterationStarted iteration)
    {
        if (_run?.Definition.Find(iteration.NodeId) is not GroupDefinition group) return;
        var resetNodes = group.Descendants().Select(node => node.Id).Append(group.Id).ToHashSet(StringComparer.Ordinal);
        ResetOverviewRatings(resetNodes, group.Id);
        foreach (var row in ExitSequence.Where(row => resetNodes.Contains(row.NodeId)))
        {
            row.Rating = ResultRating.NResult;
            row.State = ItemState.Pending;
        }
        if (_liveResults.CurrentRating is { } current) DutRating = current;
        OnPropertiesChanged(nameof(ProgressText), nameof(DutProgress));
    }

    private void ResetOverviewRatings(IReadOnlySet<string> resetNodes, string groupId)
    {
        foreach (var row in Overview)
        {
            RefreshOverviewRating(row, resetNodes);
            if (resetNodes.Contains(row.NodeId)) row.State = row.NodeId == groupId ? ItemState.Running : ItemState.Pending;
        }
    }

    private void RefreshOverviewRating(OverviewItemViewModel row, IReadOnlySet<string> resetNodes)
    {
        if (_liveResults.ForNode(row.NodeId) is { } rating) row.Rating = rating;
        else if (resetNodes.Contains(row.NodeId)) row.Rating = ResultRating.NResult;
    }

    private void HandleGroupCompleted(GroupCompleted gc)
    {
        if (_overviewByNode.TryGetValue(gc.NodeId, out var ci) && ci.NodeId == gc.NodeId)
        {
            ci.State = ItemState.Done;
            ci.Rating = gc.Rating;
        }
        if (ExitSequence.FirstOrDefault(exitItemViewModel => exitItemViewModel.NodeId == gc.NodeId) is { State: not ItemState.Aborted } exitItemViewModel)
        {
            exitItemViewModel.State = ItemState.Done;
            exitItemViewModel.Rating = gc.Rating;
        }
    }

    private void HandleExitGroupAborted(ExitGroupAborted ea)
    {
        SetExitState(ea.NodeId, ItemState.Aborted, null);
    }

    private void HandleStepCompleted(StepCompleted sc)
    {
        LiveOutputs.Complete(sc.NodeId);
        OnStepCompleted(sc);
    }

    private void HandleOperatorContentShown(OperatorContentShown os)
    {
        ShowContent(os.Prompt);
    }

    private void HandleOperatorContentCleared(OperatorContentCleared cl)
    {
        foreach (var operatorContentViewModel in Contents.Where(operatorContentViewModel2 => operatorContentViewModel2.Prompt.GroupScopeId == cl.GroupScopeId).ToList())
        {
            operatorContentViewModel.Cancel();
            Contents.Remove(operatorContentViewModel);
            _contentById.Remove(operatorContentViewModel.DisplayId);
        }
        OnPropertyChanged(nameof(ActiveContent));
    }

    private void HandleOperatorAbortRequested(OperatorAbortRequested operatorAbortRequested)
    {
        AbortCount = operatorAbortRequested.Number;
        DutWerror = true;
        Hint = operatorAbortRequested.Number == 1
            ? "Werkerabbruch: reguläre Schritte gestoppt, Abschlussgruppen laufen von innen nach außen."
            : $"Erneuter Abbruch ({operatorAbortRequested.Number}): {operatorAbortRequested.Effect}";
        AddProtocol(new ProtocolEntry(operatorAbortRequested.Time, ProtocolKind.Abort, $"Werkerabbruch angefordert ({operatorAbortRequested.Number})", "—", "", "", null, "ABORT"));
        UpdateStatus();
    }

    private void HandleResourceWaitStarted(ResourceWaitStarted rw)
    {
        _waitingResource = string.Join(", ", rw.Resources) + Holders(rw.Resources);
    }

    private void HandleResourceAcquired()
    {
        _waitingResource = null;
    }

    private void HandleResourceNotAcquired(ResourceNotAcquired rn)
    {
        _waitingResource = null;
        AddProtocol(new ProtocolEntry(rn.Time, ProtocolKind.Error, $"{Last(rn.Path)}: Ressourcen nicht erhalten", "—", "", "", rn.Reason.Contains("Zeitlimit") ? ResultRating.Error : null, rn.Reason));
    }

    private void HandleDiagnostic(Diagnostic dg)
    {
        AddProtocol(new ProtocolEntry(dg.Time, ProtocolKind.Info, dg.Message, "—", "", "", null, "Hinweis"));
    }

    private void HandleDevelopmentEvent(DevelopmentEvent dev)
    {
        DevelopmentRecorded?.Invoke(dev);
    }

    private void HandleVersionActivated(VersionActivated va)
    {
        RunningVersion = va.ToVersion;
        OnPropertyChanged(nameof(RunningVersionText));
        AddProtocol(new ProtocolEntry(va.Time, ProtocolKind.Info, $"Freigegebene Version {va.ToVersion} aktiv ab diesem Prüfling (vorher {va.FromVersion})", "—", "", "", null, "Version"));
        Hint = $"Neue freigegebene Version {va.ToVersion} ist ab diesem Prüfling aktiv.";
    }

    private void HandleDutRunCompleted(DutRunCompleted dc)
    {
        Selection.RecordDut(dc);
        DutRating = dc.Status.Rating;
        DutWerror = dc.Status.OperatorAbort;
        if (!string.IsNullOrEmpty(dc.DutId)) CurrentDutId = dc.DutId;
        AddProtocol(new ProtocolEntry(dc.Time, ProtocolKind.Run, $"Prüfling {(dc.DutId == "" ? "ohne Kennung" : dc.DutId)} abgeschlossen", "—", "", "",
            dc.Status.OperatorAbort ? null : dc.Status.Rating, dc.Status.OperatorAbort ? "ABORT" : "Abgeschlossen", IsHeader: true));
    }

    private void OnStepCompleted(StepCompleted sc)
    {
        UpdateLiveWaveform(sc);
        if (_overviewByNode.TryGetValue(sc.NodeId, out var item))
        {
            var current = sc.GroupScopeId.Length > 0 ? _liveResults.ForNode(item.NodeId) : null;
            item.Rating = current ?? item.Rating.Merge(sc.Rating);
            if (item.NodeId == sc.NodeId) item.State = sc.Cancelled ? ItemState.Aborted : ItemState.Done;
        }
        DutRating = DutRating.Merge(sc.Rating);

        var value = FormatValue(sc.Value);
        UpdateRunIdentifiers(sc);

        if (_storage is not null && !_storage.IsEnabled(sc.NodeId, StorageTarget.Display))
        {
            // Anzeige ausgeschlossen: Ablauf und Bewertung bleiben unverändert, nur keine Zeilen in Protokoll/Messwerten.
            OnPropertiesChanged(nameof(ProgressText), nameof(DutProgress));
            return;
        }

        DisplayStepResult(sc, value);
    }

    private void UpdateLiveWaveform(StepCompleted sc)
    {
        if (sc.Value is WaveformSet { Channels.Count: > 0 } waves)
        {
            LiveWaveform.SetData(waves);
            LiveWaveformTitle = $"{Last(sc.Path)} · {sc.Time.ToLocalTime():HH:mm:ss} · {waves}";
        }
    }

    private void UpdateRunIdentifiers(StepCompleted sc)
    {
        if (_dutIdWriters.Contains(sc.NodeId) && sc.Value is string id && id.Length > 0) CurrentDutId = id;
        if (_orderWriters.Contains(sc.NodeId) && sc.Value is string order && order.Length > 0) CurrentOrder = order;
    }

    private void DisplayStepResult(StepCompleted sc, string value)
    {
        var limits = sc.LowLimit is null && sc.HighLimit is null ? "—" : $"{Format.Number(sc.LowLimit)} – {Format.Number(sc.HighLimit)}";
        var isMeasurement = sc.LowLimit is not null || sc.HighLimit is not null || (sc.Unit is not null && sc.Value is double);
        if (isMeasurement && !sc.Cancelled)
        {
            var name = Last(sc.Path);
            SetMeasurement(new MeasurementRow(name, value, sc.Unit ?? "", limits, sc.Rating));
        }

        var status = sc.Cancelled ? "Abgebrochen" : sc.Rating == ResultRating.Error ? "Fehler" : "OK";
        AddProtocol(new ProtocolEntry(sc.Time, isMeasurement ? ProtocolKind.Measure : ProtocolKind.Step, Last(sc.Path), value, sc.Unit ?? "—", limits,
            sc.Rating, sc.Message is null ? status : $"{status}: {sc.Message}", Iteration: sc.Iteration > 1 ? sc.Iteration.ToString(CultureInfo.InvariantCulture) : ""));
        OnPropertiesChanged(nameof(ProgressText), nameof(DutProgress));
    }

    private void MarkOverview(string nodeId, ItemState state, ResultRating? rating)
    {
        if (!_overviewByNode.TryGetValue(nodeId, out var item)) return;
        item.State = state;
        if (rating is { } rating2) item.Rating = rating2;
        OnPropertiesChanged(nameof(ProgressText), nameof(DutProgress));
    }

    private void SetExitState(string nodeId, ItemState state, ResultRating? rating)
    {
        if (ExitSequence.FirstOrDefault(exitItemViewModel => exitItemViewModel.NodeId == nodeId) is not { } exitItemViewModel) return;
        exitItemViewModel.State = state;
        if (rating is not null) exitItemViewModel.Rating = rating;
    }

    private void UpdateStatus()
    {
        if (!IsRunning && State is EngineState.Completed or EngineState.Idle)
        {
            StatusText = "Bereit";
            StatusKind = StationStatusKind.Idle;
            return;
        }
        (StatusText, StatusKind) = State switch
        {
            EngineState.WaitingResource => ($"Warten auf {_waitingResource ?? "Ressource"}", StationStatusKind.Waiting),
            EngineState.AwaitingOperator => ("Wartet auf Werker", StationStatusKind.Waiting),
            EngineState.PausedBeforeResultReturn => ("Pausiert", StationStatusKind.Paused),
            _ when AbortCount > 0 => ("Abschließen", StationStatusKind.Aborting),
            EngineState.Initializing => ("Initialisierung", StationStatusKind.Running),
            EngineState.Finalizing => ("Abschließen", StationStatusKind.Aborting),
            _ => ("Prüfung läuft", StationStatusKind.Running),
        };
        OnPropertyChanged(nameof(IsAborting));
    }

    private void AddProtocol(ProtocolEntry entry)
    {
        Protocol.Add(entry);
        while (Protocol.Count > MaxProtocol) Protocol.RemoveAt(0);
    }

    private static string Last(string path)
    {
        var index = path.LastIndexOf(" > ", StringComparison.Ordinal);
        return index < 0 ? path : path[(index + 3)..];
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "—",
        double number => Format.Number(number),
        bool flag => flag ? "true" : "false",
        string text => text,
        DateTime dt => dt.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}
