using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.Persistence.Stations;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class StationViewModel
{
    private string? _wearReservationFailure;

    private bool CanAttemptPhysicalStart() => !IsRunning && !BlockWearStart();

    internal bool BlockWearStart()
    {
        var refusal = WearRefusal();
        if (refusal is null) return false;
        Error = refusal;
        return true;
    }

    private string? WearRefusal()
    {
        if (_coordinator?.Setup.Station(Name)?.WearCounters is not { Count: > 0 } counters) return null;
        try
        {
            var problem = counters.Select(counter => counter.Validate()).FirstOrDefault(message => message is not null);
            if (problem is not null) return problem;
            return WearLimitMessage(_coordinator.Wear.Read(counters));
        }
        catch (Exception ex) when (WearStorageFailed(ex))
        {
            return $"Verschleißzähler konnten nicht gelesen werden: {ex.Message}";
        }
    }

    private Func<bool>? DutStartGuard()
    {
        var reference = ProductionReferenceGuard();
        if (_simulationOnly || _coordinator?.Setup.Station(Name)?.WearCounters is not { Count: > 0 }) return reference;
        return () => (reference?.Invoke() ?? true) && CheckWearDuringRun();
    }

    private bool CheckWearDuringRun()
    {
        var refusal = _wearReservationFailure ?? WearRefusal();
        if (refusal is null) return true;
        PublishWearBlock(refusal);
        return false;
    }

    private Func<string?>? WearReservation()
    {
        _wearReservationFailure = null;
        if (_simulationOnly || _coordinator?.Setup.Station(Name)?.WearCounters is not { Count: > 0 } counters) return null;
        return () => ReserveWear(_coordinator.Wear, counters);
    }

    private string? ReserveWear(WearCounterStore store, IReadOnlyList<WearCounterConfig> counters)
    {
        try
        {
            var (started, readings) = store.BeginDut(counters);
            if (!started) return WearLimitMessage(readings);
            foreach (var reading in readings.Where(reading => reading.Warning || reading.Blocked))
                PublishWearWarning(reading);
            return null;
        }
        catch (Exception ex) when (WearStorageFailed(ex))
        {
            _wearReservationFailure = $"Verschleißzähler konnten nicht gespeichert werden: {ex.Message}";
            PublishWearBlock(_wearReservationFailure);
            return _wearReservationFailure;
        }
    }

    private void PublishWearBlock(string message) => _ui.Post(() =>
    {
        if (Error == message) return;
        Error = message;
        LogSelection("Verschleißzähler", message, ResultRating.Error, "Kein weiterer Prüfling");
    });

    private void PublishWearWarning(WearReading reading) => _ui.Post(() =>
        LogSelection($"Verschleißzähler · {reading.Counter.Name}", reading.Count.ToString(), null,
            reading.Blocked ? "Sperrgrenze erreicht" : "Warngrenze erreicht"));

    private static string? WearLimitMessage(IEnumerable<WearReading> readings)
        => readings.FirstOrDefault(reading => reading.Blocked) is { } blocked
            ? $"Verschleißgrenze erreicht: {blocked.Counter.Name} ({blocked.Count}). Zähler nach Wartung zurücksetzen."
            : null;

    internal static bool WearStorageFailed(Exception exception)
        => exception is IOException or UnauthorizedAccessException or JsonException;
}
