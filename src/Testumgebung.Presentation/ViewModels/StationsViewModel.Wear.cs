using System.Collections.ObjectModel;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class StationsViewModel
{
    private readonly Func<string?>? _wearResetUser;
    private WearCounterRow? _selectedWearCounter;
    private RelayCommand? _addWearCounter, _removeWearCounter, _resetWearCounter, _refreshWearCounters;

    public ObservableCollection<WearCounterRow> WearCounters { get; } = [];
    public ObservableCollection<WearReset> WearResets { get; } = [];

    public WearCounterRow? SelectedWearCounter
    {
        get => _selectedWearCounter;
        set
        {
            if (!SetProperty(ref _selectedWearCounter, value)) return;
            _removeWearCounter?.RaiseCanExecuteChanged();
            _resetWearCounter?.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand AddWearCounterCommand => _addWearCounter ??= new(() =>
    {
        if (Station is not { } station) return;
        var number = 1;
        while (station.WearCounters.Any(counter => counter.Name == $"Zähler {number}")) number++;
        var counter = new WearCounterConfig { Name = $"Zähler {number}" };
        station.WearCounters.Add(counter);
        var row = new WearCounterRow(counter, Edited);
        WearCounters.Add(row);
        SelectedWearCounter = row;
        Edited();
    });

    public RelayCommand RemoveWearCounterCommand => _removeWearCounter ??= new(() =>
    {
        if (Station is not { } station || SelectedWearCounter is not { } selected) return;
        station.WearCounters.Remove(selected.Counter);
        WearCounters.Remove(selected);
        SelectedWearCounter = null;
        Edited();
    }, () => SelectedWearCounter is not null);

    public RelayCommand ResetWearCounterCommand => _resetWearCounter ??= new(ResetWearCounter, () => SelectedWearCounter is not null);
    public RelayCommand RefreshWearCountersCommand => _refreshWearCounters ??= new(RefreshWearCounters);

    private void ReloadWearCounters()
    {
        SelectedWearCounter = null;
        WearCounters.Clear();
        foreach (var counter in Station?.WearCounters ?? []) WearCounters.Add(new WearCounterRow(counter, Edited));
        RefreshWearCounters();
    }

    private void RefreshWearCounters()
    {
        WearResets.Clear();
        if (Station is not { } station || (station.WearCounters.Count == 0 && _coordinator.Setup.Station(station.Name)?.WearCounters.Count == 0)) return;
        try
        {
            var readings = _coordinator.Wear.Read(station.WearCounters);
            foreach (var (row, reading) in WearCounters.Zip(readings)) row.Count = reading.Count;
            foreach (var reset in _coordinator.Wear.Resets(station.Name)) WearResets.Add(reset);
        }
        catch (Exception ex) when (StationViewModel.WearStorageFailed(ex))
        {
            Error = "Verschleißzähler konnten nicht geladen werden: " + ex.Message;
        }
    }

    private void ResetWearCounter()
    {
        var user = _wearResetUser?.Invoke();
        if (string.IsNullOrWhiteSpace(user) || _anyRunning())
        {
            Error = "Zum Rücksetzen müssen alle Prüfplätze stillstehen und ein Entwickler angemeldet sein.";
            return;
        }
        if (Station is not { } station || SelectedWearCounter is not { } row) return;
        var saved = _coordinator.Setup.Station(station.Name)?.WearCounters.Find(counter => counter.Id == row.Counter.Id);
        if (saved is null)
        {
            Error = "Zähler vor dem Rücksetzen speichern.";
            return;
        }
        try
        {
            _coordinator.Wear.Reset(station.Name, saved, user, DateTimeOffset.Now);
            Error = null;
            RefreshWearCounters();
        }
        catch (Exception ex) when (StationViewModel.WearStorageFailed(ex))
        {
            Error = "Verschleißzähler konnte nicht zurückgesetzt werden: " + ex.Message;
        }
    }

    private IEnumerable<ConfigIssue> WearConfigIssues()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var station in _draft.Stations)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var counter in station.WearCounters)
            {
                var message = counter.Validate();
                if (message is null && (!ids.Add(counter.Id) || !names.Add(counter.Name.Trim())))
                    message = "Zählernamen müssen je Prüfplatz und Zählerkennungen rechnerweit eindeutig sein.";
                if (message is not null) yield return new(ConfigSeverity.Error, station.Name, null, message);
            }
        }
    }
}

public sealed class WearCounterRow(WearCounterConfig counter, Action edited) : ObservableObject
{
    internal WearCounterConfig Counter { get; } = counter;
    private long _count;

    public string Name { get => Counter.Name; set { Counter.Name = value; edited(); } }
    public long WarningAt { get => Counter.WarningAt; set { Counter.WarningAt = value; edited(); } }
    public long StopAt { get => Counter.StopAt; set { Counter.StopAt = value; edited(); } }
    public long Count { get => _count; internal set => SetProperty(ref _count, value); }
}
