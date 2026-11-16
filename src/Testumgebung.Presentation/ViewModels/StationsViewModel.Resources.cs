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
    // ------------------------------------------------------------------ Ressourcen-Details

    /// <summary>Eigenschaften der gewählten Ressource (Adapter, Gerät, Kanal, Bereiche, Fähigkeiten, Sperrschlüssel).</summary>
    public ObservableCollection<DetailRow> Details { get; } = [];

    public string DetailsTitle => _selectedRow is null ? "Ressourcen-Details" : "Ressourcen-Details: " + _selectedRow.Name;

    private void UpdateDetails()
    {
        Details.Clear();
        OnPropertyChanged(nameof(DetailsTitle));
        if (_selectedRow is not { } row || _selectedStation is null) return;
        var assignment = row.Assignment;
        var device = _draft.Device(assignment.Device);
        var sim = Project.Simulation.FirstOrDefault(resource => resource.Name == assignment.Name);
        var output = Project.SimulationOutputs.FirstOrDefault(output2 => output2.Name == assignment.Name);
        Details.Add(new("Adapter", device is null ? "—" : $"{device.Adapter}"));
        Details.Add(new("Gerät", device?.Name ?? $"unbekannt ({assignment.Device})"));
        Details.Add(new("Kanal", assignment.Channel));
        AddCapabilities();

        void AddCapabilities()
        {
            if (sim is not null)
            {
                var range = sim.Min > -1e8 || sim.Max < 1e8
                    ? string.Create(CultureInfo.InvariantCulture, $"{sim.Unit}: {sim.Min:0.##} – {sim.Max:0.##}")
                    : $"{sim.Unit}: unbegrenzt";
                Details.Add(new("Unterstützte Bereiche", range + (sim.Frequency > 0 ? string.Create(CultureInfo.InvariantCulture, $", {sim.Frequency:0.##} Hz") : "")));
            }
            if (output is not null)
                Details.Add(new("Unterstützte Bereiche", string.Create(CultureInfo.InvariantCulture, $"{output.Unit}: {output.Min:0.##} – {output.Max:0.##}, Abtastraten {output.SampleRates} Hz")));
            Details.Add(new("Synchrone Zeitbasis", sim is { Frequency: > 0 } || output is not null ? "Unterstützt" : "—"));
            Details.Add(new("Waveform-Speicher", output is not null ? $"Verfügbar (Kanäle {output.Channels})" : "—"));
            var actions = new List<string> { "Lesen", "Schreiben" };
            if (sim is { Frequency: > 0 }) actions.Add("Kurve aufnehmen");
            if (output is not null) actions.AddRange(["Laden", "Übertragen", "Starten", "Stoppen"]);
            Details.Add(new("Unterstützte Aktionen", string.Join(", ", actions)));
        }

        Details.Add(new("Sperre", assignment.RequiresLock ? "erforderlich (Reservierung an einer Gruppe)" : "keine"));
        Details.Add(new("Sperrschlüssel", _draft.LockKey(_selectedStation, assignment)));
        if (device is { Shared: true })
        {
            var key = _draft.LockKey(_selectedStation, assignment);
            var users = _draft.Stations.SelectMany(station => station.Resources.Where(assignment2 => _draft.LockKey(station.Name, assignment2) == key).Select(assignment2 => assignment2.Name == assignment.Name ? station.Name : $"{station.Name} ({assignment2.Name})")).Distinct();
            Details.Add(new("Gemeinsam mit", string.Join(", ", users)));
        }
    }

    // ------------------------------------------------------------------ Reservierungen je Gruppe

    public ObservableCollection<GroupDefinition> Groups { get; } = [];

    public GroupDefinition? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value)) return;
            OnPropertiesChanged(nameof(TimeoutEnabled), nameof(TimeoutSeconds), nameof(GroupPath));
            RefreshLive();
        }
    }

    public string GroupPath => _selectedGroup is null ? "" : PathOf(_selectedGroup);

    private string PathOf(GroupDefinition group2)
    {
        var parts = new List<string>();
        foreach (var root in Project.Definition.AllRoots)
            if (Find(root, group2, parts)) break;
        return string.Join(" › ", parts);

        static bool Find(GroupDefinition current, GroupDefinition target, List<string> path)
        {
            path.Add(current.Name);
            if (ReferenceEquals(current, target)) return true;
            foreach (var child in current.Children.OfType<GroupDefinition>().Concat(current.ExitGroup is { } group ? [group] : []))
                if (Find(child, target, path)) return true;
            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    private void ReloadGroups()
    {
        var keep = _selectedGroup?.Id;
        Groups.Clear();
        foreach (var group in Project.Definition.AllNodes().OfType<GroupDefinition>().Where(group2 => group2.Resources.Count > 0)) Groups.Add(group);
        _selectedGroup = Groups.FirstOrDefault(group2 => group2.Id == keep) ?? Groups.FirstOrDefault();
        OnPropertiesChanged(nameof(SelectedGroup), nameof(TimeoutEnabled), nameof(TimeoutSeconds), nameof(GroupPath), nameof(HasGroups));
    }

    public bool HasGroups => Groups.Count > 0;

    /// <summary>Optionale maximale Wartezeit der Gruppe (Ablaufeigenschaft; Änderung markiert das Projekt als geändert).</summary>
    public bool TimeoutEnabled
    {
        get => _selectedGroup?.ResourceTimeout is not null;
        set
        {
            if (_selectedGroup is null || value == TimeoutEnabled) return;
            _selectedGroup.ResourceTimeout = value ? TimeSpan.FromSeconds(30) : null;
            _editor.MarkChanged();
            OnPropertiesChanged(nameof(TimeoutEnabled), nameof(TimeoutSeconds));
        }
    }

    public string TimeoutSeconds
    {
        get => _selectedGroup?.ResourceTimeout is { } duration ? duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) : "";
        set
        {
            if (_selectedGroup is null) return;
            if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var sample) || sample <= 0)
            {
                Error = "Wartezeit: positive Zahl in Sekunden angeben.";
                return;
            }
            _selectedGroup.ResourceTimeout = TimeSpan.FromSeconds(sample);
            _editor.MarkChanged();
            Error = null;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<ReservationRow> Reservations { get; } = [];
    public ObservableCollection<WaitRow> WaitStatus { get; } = [];

    /// <summary>„RessourcenErhalten“ des gewählten Prüfplatzes für die Gruppe (live).</summary>
    public string AcquiredText { get; private set; } = "Nein";

    public bool Acquired { get; private set; }

    /// <summary>Gültigkeitsbereich der aktuellen Sperre (Besitzer / Gruppe).</summary>
    public string ScopeText { get; private set; } = "—";
}
