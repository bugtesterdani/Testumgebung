using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Hardware;
using Testumgebung.Instruments;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class StationsViewModel
{
    // ------------------------------------------------------------------ Live-Status

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            RefreshLive();
        });
    }

    /// <summary>Status aus der Ressourcenvermittlung und dem Zugriffsprotokoll übernehmen.</summary>
    public void RefreshLive()
    {
        _snapshot = _coordinator.Resources.Snapshot();
        var station = _selectedStation;
        RefreshAssignmentStatus();

        void RefreshAssignmentStatus()
        {
            foreach (var row in Rows)
            {
                if (station is null) continue;
                var key = _draft.LockKey(station, row.Assignment);
                var ownership = _snapshot.FirstOrDefault(ownership2 => ownership2.Resource == key);
                (row.Status, row.StatusKind) = ownership switch
                {
                    { OwnerRunId: not null } when ownership.OwnerName == station => ($"Reserviert (eigener Lauf){Waiting(ownership)}", StationStatusKind.Paused),
                    { OwnerRunId: not null } => ($"Reserviert – {ownership.OwnerName ?? ownership.OwnerRunId}{Waiting(ownership)}", StationStatusKind.Waiting),
                    { WaitingNames.Count: > 0 } => ($"Frei – wartend: {string.Join(", ", ownership.WaitingNames!)}", StationStatusKind.Waiting),
                    _ when _draft.Device(row.Assignment.Device) is null => ("Gerät fehlt", StationStatusKind.Aborting),
                    _ => ("Bereit", StationStatusKind.Running),
                };
            }

        }

        Reservations.Clear();
        WaitStatus.Clear();
        Acquired = false;
        ScopeText = "—";
        if (_selectedGroup is { } group && station is not null)
        {
            var keys = AddReservations(group, station);
            Acquired = keys.Count > 0 && keys.All(key2 => _snapshot.Any(ownership2 => ownership2.Resource == key2 && ownership2.OwnerName == station));
            RefreshWaitStatus(group);
            var owner = _snapshot.FirstOrDefault(ownership2 => keys.Contains(ownership2.Resource) && ownership2.OwnerRunId is not null);
            if (owner is not null)
                ScopeText = $"{owner.OwnerRunId} / {owner.ScopeName ?? "Gruppe"}" +
                            (owner.Since is { } since ? $" seit {since.ToLocalTime():HH:mm:ss}" : "");
        }
        List<string> AddReservations(GroupDefinition group2, string station)
        {
            var keyOf = _draft.KeyOf(station);
            var keys = new List<string>();
            foreach (var name in group2.Resources)
            {
                var assignment = _draft.Station(station)?.Resources.FirstOrDefault(assignment2 => assignment2.Name == name);
                var key = keyOf(name);
                keys.Add(key);
                var ownership2 = _snapshot.FirstOrDefault(ownership3 => ownership3.Resource == key);
                var (text, kind) = ownership2 switch
                {
                    { OwnerRunId: not null } when ownership2.OwnerName == station => ("Reserviert (eigener Lauf)", StationStatusKind.Paused),
                    { OwnerRunId: not null } => ($"Reserviert ({ownership2.OwnerName ?? ownership2.OwnerRunId})", StationStatusKind.Waiting),
                    _ when assignment is null => ("nicht zugeordnet", StationStatusKind.Aborting),
                    _ => ("Bereit", StationStatusKind.Running),
                };
                Reservations.Add(new ReservationRow(name, assignment is null ? "—" : _draft.Device(assignment.Device)?.Name ?? assignment.Device, assignment?.Channel ?? "—", key, text, kind));
            }
            return keys;
        }

        void RefreshWaitStatus(GroupDefinition group3)
        {
            foreach (var st in _draft.Stations)
            {
                var stKeys = group3.Resources.Select(_draft.KeyOf(st.Name)).ToList();
                var owns = stKeys.Any(stKey => _snapshot.Any(ownership3 => ownership3.Resource == stKey && ownership3.OwnerName == st.Name));
                var waits = stKeys.Any(stKey => _snapshot.Any(ownership3 => ownership3.Resource == stKey && (ownership3.WaitingNames ?? []).Contains(st.Name)));
                WaitStatus.Add(new WaitRow(st.Name, owns ? "Eigentümer" : waits ? "In Warteschlange" : "—", owns, waits));
            }
        }

        AcquiredText = Acquired ? "Ja" : "Nein";
        OnPropertiesChanged(nameof(Acquired), nameof(AcquiredText), nameof(ScopeText));

        Accesses.Clear();
        foreach (var hardwareAccess in _coordinator.Journal.Recent().Take(60))
            Accesses.Add(new AccessRow(hardwareAccess.Start.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), hardwareAccess.Station, hardwareAccess.Resource, hardwareAccess.Operation, hardwareAccess.Key, hardwareAccess.Conflict));
        OnPropertiesChanged(nameof(AccessSummary), nameof(HasConflicts));
    }

    private static string Waiting(ResourceOwnership ownership) => ownership.WaitingNames is { Count: > 0 } items ? " · wartend: " + string.Join(", ", items) : "";

    // ------------------------------------------------------------------ Zugriffsprotokoll

    public ObservableCollection<AccessRow> Accesses { get; } = [];

    public bool HasConflicts => _coordinator.Journal.Conflicts.Count > 0;

    public string AccessSummary
    {
        get
        {
            var conflicts = _coordinator.Journal.Conflicts.Count;
            return $"{_coordinator.Journal.Count} Gerätezugriffe · " +
                   (conflicts == 0 ? "keine widersprüchlichen Zugriffe" : $"{conflicts} WIDERSPRÜCHLICHE Zugriffe");
        }
    }
}
