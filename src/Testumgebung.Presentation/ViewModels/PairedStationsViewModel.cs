using System.Collections.ObjectModel;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Ein Auftrag, eine Auswahl, zwei unabhängige Prüfläufe und ein Sitzungsabbruch.</summary>
public sealed class PairedStationsViewModel : ObservableObject
{
    private bool _enabled, _busy, _stopRequested;
    private string _order = "", _status = "Zwei Prüfplätze wählen und gemeinsamen Fertigungsauftrag scannen.";
    private StationViewModel? _first, _second;

    public PairedStationsViewModel(ObservableCollection<StationViewModel> stations)
    {
        Stations = stations;
        _first = stations.FirstOrDefault(); _second = stations.Skip(1).FirstOrDefault();
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => CanStart);
        AbortCommand = new RelayCommand(Abort, () => IsBusy);
        stations.CollectionChanged += (_, _) => Refresh();
    }
    public ObservableCollection<StationViewModel> Stations { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand AbortCommand { get; }
    public bool Enabled { get => _enabled; set { if (!_busy && SetProperty(ref _enabled, value)) Refresh(); } }
    public StationViewModel? First { get => _first; set { if (!_busy && SetProperty(ref _first, value)) Refresh(); } }
    public StationViewModel? Second { get => _second; set { if (!_busy && SetProperty(ref _second, value)) Refresh(); } }
    public string Order { get => _order; set { if (!_busy && SetProperty(ref _order, value ?? "")) Refresh(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy => _busy;
    public bool CanConfigure => !_busy;
    public bool CanStart => Enabled && !_busy && Order.Trim().Length > 0 && First is not null && Second is not null
        && !ReferenceEquals(First, Second) && Stations.Contains(First) && Stations.Contains(Second)
        && !First.IsRunning && !Second.IsRunning && !First.Selection.IsActive && !Second.Selection.IsActive;

    public async Task StartAsync()
    {
        if (!CanStart) { Status = "Zwei verschiedene, freie Prüfplätze und einen Auftrag auswählen."; return; }
        var first = First!; var second = Second!;
        _busy = true; _stopRequested = false; Refresh();
        var handover = new StationHandover();
        first.PairContext = new PairedStationContext(handover, 1);
        second.PairContext = new PairedStationContext(handover, 2);
        try
        {
            first.Selection.Activate(); first.Selection.Auftrag = Order.Trim();
            Status = "Fertigungsauftrag wird einmal für beide Seiten ausgewertet …";
            await first.Selection.AssignAsync().ConfigureAwait(true);
            if (_stopRequested) return;
            if (first.Selection.Outcome is { CanStart: false, ManualAllowed: true, Candidates.Count: 1 } single)
                first.Selection.AdoptSharedOrder(TestSelector.Manual(single, single.Candidates[0], Order.Trim()));
            if (first.Selection.Outcome is not { CanStart: true, Test: { } test } outcome)
            { Status = first.Selection.ResultText + " Bitte die Zuordnung unter Auswahl & Auftrag eindeutig konfigurieren."; return; }
            if (!second.AvailableTests().Any(t => t.Name == test.Name && t.Version == test.Version))
            { Status = "Das ausgewählte Testprogramm ist für die zweite Seite nicht verfügbar."; return; }
            // Beide Projekte vor dem ersten Start prüfen; keine einseitig gestartete falsche Zuordnung.
            var loadedFirst = await first.LoadTestAsync(test).ConfigureAwait(true);
            var loadedSecond = await second.LoadTestAsync(test).ConfigureAwait(true);
            ValidateProgram(loadedFirst.Project.Definition);
            ValidateProgram(loadedSecond.Project.Definition);
            if (_stopRequested) return;
            second.Selection.AdoptSharedOrder(outcome);
            Status = $"Auftrag {outcome.Order} · {test.Title} · eigene Seriennummer je Seite.";
            var firstRun = first.Selection.StartSelectedAsync();
            var secondRun = second.Selection.StartSelectedAsync();
            await Task.WhenAny(firstRun, secondRun).ConfigureAwait(true);
            // Bei Fehler, einseitigem Abbruch oder Ende auch den Partner beenden; keine verwaiste Freigabewartezeit.
            StopPartner(first); StopPartner(second);
            await Task.WhenAll(firstRun, secondRun).ConfigureAwait(true);
            Status = "Gemeinsamer Auftrag beendet. Zum Fortsetzen oder Wechsel erneut einen Fertigungsauftrag scannen.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        { Status = "Gemeinsamer Auftrag konnte nicht ausgeführt werden: " + ex.Message; }
        finally
        {
            StopPartner(first); StopPartner(second);
            first.Selection.End("Gemeinsamer Auftrag beendet."); second.Selection.End("Gemeinsamer Auftrag beendet.");
            await Task.WhenAll(first.WaitForCompletionAsync(), second.WaitForCompletionAsync()).ConfigureAwait(true);
            first.PairContext = null; second.PairContext = null;
            _busy = false; Refresh();
        }
    }

    private static void StopPartner(StationViewModel station)
    {
        // Ein zweiter Abbruch ist eine ausdrückliche Werkeraktion zum Überspringen von End.
        if (!station.IsAborting) station.RequestAbort();
    }

    private static void ValidateProgram(TestDefinition definition)
    {
        var errors = definition.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        if (!definition.Root.Variables.Any(v => v.Name == "Auftrag" && v.Type == Testumgebung.Contracts.VarType.String))
            throw new InvalidOperationException("Hauptablauf benötigt die String-Variable Auftrag.");
        var duts = definition.Loop.Descendants().OfType<GroupDefinition>().Where(g => g.IsDutRun).ToList();
        if (duts.Count == 0 || duts.Any(d => !d.Variables.Any(v => v.Name == d.DutIdVariable && v.Type == Testumgebung.Contracts.VarType.String)))
            throw new InvalidOperationException("Loop benötigt einen Prüflingstest mit eigener String-Seriennummer.");
        foreach (var dut in duts) ValidateSerialInput(dut);
    }

    private static void ValidateSerialInput(GroupDefinition dut)
    {
        if (dut.Children.FirstOrDefault() is not StepDefinition scan
            || scan.BlockType != Testumgebung.Engine.Blocks.BlockCatalog.Operator
            || scan.Parameters.GetValueOrDefault("Art") != "Eingabe"
            || scan.Parameters.GetValueOrDefault("Ziel") != dut.DutIdVariable
            || scan.Parameters.GetValueOrDefault("Datentyp") != "Text"
            || !bool.TryParse(scan.Parameters.GetValueOrDefault("Pflicht"), out var required) || !required)
            throw new InvalidOperationException("Jeder Prüflingstest muss mit einer verpflichtenden Text-Eingabe seiner Seriennummer beginnen.");
    }

    public void Abort()
    {
        _stopRequested = true;
        First?.RequestAbort(); Second?.RequestAbort();
        Status = "Beide Seiten werden abgebrochen; End-Abschnitte werden ausgeführt.";
    }

    private void Refresh()
    {
        OnPropertiesChanged(nameof(CanConfigure), nameof(CanStart), nameof(IsBusy));
        StartCommand.RaiseCanExecuteChanged(); AbortCommand.RaiseCanExecuteChanged();
    }
}
