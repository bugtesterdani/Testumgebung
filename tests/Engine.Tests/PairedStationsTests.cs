using System.Collections.ObjectModel;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class PairedStationsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Handover_is_alternating_owned_and_cancellable(int first)
    {
        var gate = new StationHandover(); var other = 3-first;
        using var stop = new CancellationTokenSource();
        var waiting = gate.WaitAsync(other, "Pruefung", first, stop.Token);
        Assert.False(waiting.IsCompleted);
        await gate.WaitAsync(first, "Pruefung", first, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.WaitAsync(first, "Pruefung", first, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => gate.Release(other, "Pruefung"));
        gate.Release(first, "Pruefung"); await waiting.Within();
        var next = gate.WaitAsync(first, "Pruefung", first, stop.Token);
        Assert.False(next.IsCompleted);
        gate.Release(other, "Pruefung"); await next.Within();
        await gate.WaitAsync(other, "Separat", other, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.WaitAsync(first, "Pruefung", other, CancellationToken.None));
        var cancelled = gate.WaitAsync(other, "Pruefung", first, stop.Token);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await Assert.ThrowsAsync<ArgumentException>(() => gate.WaitAsync(0, "Pruefung", 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => gate.WaitAsync(1, "", 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => gate.WaitAsync(1, "Pruefung", 0, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => gate.Release(first, "missing"));
    }

    private static StepDefinition Block(string type, string id, params (string Key, string Value)[] values)
        => BlockCatalog.Default.Create(type, id, id, values.ToDictionary(v => v.Key, v => v.Value));

    private static TestProject Program()
    {
        var dut = Group("dut").Var("Kennung", VarType.String);
        dut.IsDutRun = true;
        dut.Add(Block(BlockCatalog.Operator, "serial", ("Art", "Eingabe"), ("Text", "Seriennummer scannen"), ("Datentyp", "Text"), ("Ziel", "Kennung"), ("Pflicht", "true")),
            Block(BlockCatalog.HandoverWait, "wait"),
            Block(BlockCatalog.Evaluate, "check", ("Bedingung", "true"), ("Wert", "Kennung")),
            Block(BlockCatalog.Operator, "remove", ("Art", "Anweisung"), ("Text", "Prüfling entnommen"), ("Warten", "true")),
            Block(BlockCatalog.HandoverRelease, "release"));
        var loop = Group("loop").Add(dut); loop.Repetition = Repetition.Endless;
        var definition = Test(loop); definition.Root.Var("Auftrag", VarType.String).Var("Pruefseite", VarType.Int);
        return new TestProject(definition);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("dut")]
    [InlineData("serial")]
    [InlineData("invalid")]
    [InlineData("type")]
    [InlineData("required")]
    [InlineData("no-match")]
    [InlineData("single-manual")]
    public async Task Invalid_program_or_order_does_not_leave_one_side_running(string damage)
    {
        var project = Program();
        var dut = (GroupDefinition)project.Definition.Loop.Children[0];
        if (damage == "order") project.Definition.Root.Variables.Clear();
        if (damage == "dut") project.Definition.Loop.Children.Clear();
        if (damage == "serial") dut.Variables.Clear();
        if (damage == "invalid") dut.Children.Add(dut.Children[0]);
        if (damage == "type") ((StepDefinition)dut.Children[0]).Parameters["Datentyp"] = "Zahl";
        if (damage == "required") ((StepDefinition)dut.Children[0]).Parameters["Pflicht"] = "false";
        var setup = StationSetup.Default(project, 2);
        setup.Stations[0].Selection = damage switch
        {
            "no-match" => new SelectionConfig { UseManual = false, UseRules = true },
            "single-manual" => new SelectionConfig { UseManual = true, UseRules = false },
            _ => new SelectionConfig { Rules = [new SelectionRule { Pattern = "*", Test = project.Definition.Name }] },
        };
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, stations: new StationCoordinator(setup, new ResourceManager()));
        var pair = shell.PairedStations; pair.Enabled = true; pair.Order = "A123";
        var running = pair.StartAsync();
        if (damage == "single-manual")
        {
            await ui.Until(() => pair.First!.IsRunning && pair.Second!.IsRunning, "Einzelprogramm manuell eindeutig");
            pair.Abort();
        }
        await ui.Until(() => running.IsCompleted, "Fehler beendet beide Seiten"); await running;
        Assert.False(pair.IsBusy); Assert.False(pair.First!.IsRunning); Assert.False(pair.Second!.IsRunning);
        Assert.True(pair.CanStart);
        if (damage != "single-manual") Assert.Contains(damage == "no-match" ? "Zuordnung" : "konnte", pair.Status);
    }

    [Fact]
    public async Task Shared_order_separate_serials_and_removal_controls_partner()
    {
        var project = Program(); var setup = StationSetup.Default(project, 2);
        foreach (var station in setup.Stations)
            station.Selection = new SelectionConfig { Rules = [new SelectionRule { Pattern = "A*", Test = project.Definition.Name }] };
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, clock: SystemClock.Instance,
            stations: new StationCoordinator(setup, new ResourceManager()));
        var pair = shell.PairedStations; pair.Enabled = true; pair.Order = "A123";
        var running = pair.StartAsync();
        await ui.Until(() => pair.First!.ActiveContent?.IsAwaiting == true && pair.Second!.ActiveContent?.IsAwaiting == true, "beide Seriennummern");
        Assert.Equal("A123", pair.First!.CurrentOrder); Assert.Equal("A123", pair.Second!.CurrentOrder);
        Assert.Equal(1, pair.First.Run!.Options.PairedStation!.Side); Assert.Equal(2, pair.Second.Run!.Options.PairedStation!.Side);
        Assert.Null(pair.First.Run.Options.VersionSource);
        Assert.Null(pair.Second.Run.Options.VersionSource);
        pair.First.ActiveContent!.Input = "A-SN1"; pair.First.ActiveContent.ConfirmCommand.Execute(null);
        pair.Second.ActiveContent!.Input = "B-SN1"; pair.Second.ActiveContent.ConfirmCommand.Execute(null);
        await ui.Until(() => pair.First.ActiveContent is { IsAwaiting: true, Text: "Prüfling entnommen" }, "Entnahme erste Seite");
        Assert.Empty(pair.Second.Run!.Steps("check"));
        Assert.Equal("A-SN1", Assert.Single(pair.First.Run!.Steps("check")).Value);
        pair.First.ActiveContent!.ConfirmCommand.Execute(null);
        await ui.Until(() => pair.Second.ActiveContent is { IsAwaiting: true, Text: "Prüfling entnommen" }, "Entnahme zweite Seite");
        Assert.Equal("B-SN1", Assert.Single(pair.Second.Run!.Steps("check")).Value);
        Assert.Single(pair.First.Contents, c => c.IsAwaiting);
        Assert.Single(pair.Second.Contents, c => c.IsAwaiting);
        pair.Order = "Unzulässig"; Assert.Equal("A123", pair.Order);
        pair.First = pair.Second; Assert.NotSame(pair.First, pair.Second);
        pair.Enabled = false; Assert.True(pair.Enabled);
        pair.Abort(); await ui.Until(() => running.IsCompleted, "gemeinsamer Abbruch"); await running;
        Assert.False(pair.First.IsRunning); Assert.False(pair.Second.IsRunning); Assert.False(pair.IsBusy);
        Assert.True(pair.CanStart);
        // Neue Sitzung bekommt frische Freigaben und beginnt wieder mit Seite 1.
        var restart = pair.StartAsync();
        await ui.Until(() => pair.First.IsRunning && pair.Second.IsRunning, "Neustart");
        pair.First.RequestAbort(); await ui.Until(() => restart.IsCompleted, "einseitiger Abbruch stoppt beide"); await restart;
        Assert.False(pair.Second.IsRunning);
    }

    [Fact]
    public async Task Inputs_on_same_side_are_queued_and_cancellation_releases_gate()
    {
        var project = Program(); var station = new StationViewModel("Test", () => project, new ResourceManager());
        var host = (IOperatorInterface)station;
        using var cancel = new CancellationTokenSource();
        var first = host.AskAsync("run", new OperatorPrompt(OperatorPromptKind.Input, "eins") { DisplayId = "one" }, CancellationToken.None);
        var second = host.AskAsync("run", new OperatorPrompt(OperatorPromptKind.Input, "zwei") { DisplayId = "two" }, cancel.Token);
        Assert.Single(station.Contents, c => c.IsAwaiting);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        station.ActiveContent!.Input = "1"; station.ActiveContent.ConfirmCommand.Execute(null); await first;
        var third = host.AskAsync("run", new OperatorPrompt(OperatorPromptKind.Input, "drei") { DisplayId = "three" }, CancellationToken.None);
        await Task.Delay(10);
        station.ActiveContent!.Input = "3"; station.ActiveContent.ConfirmCommand.Execute(null);
        Assert.Equal("3", await third.Within());
    }

    [Theory]
    [InlineData("same")]
    [InlineData("missing")]
    [InlineData("no-order")]
    public async Task Pair_guard_refuses_invalid_start(string mode)
    {
        var project = Program();
        var first = new StationViewModel("eins", () => project, new ResourceManager());
        var second = new StationViewModel("zwei", () => project, new ResourceManager());
        var stations = new ObservableCollection<StationViewModel> { first, second };
        var pair = new PairedStationsViewModel(stations) { Enabled = true, Order = "A123" };
        if (mode == "same") pair.Second = first;
        if (mode == "missing") stations.Remove(second);
        if (mode == "no-order") pair.Order = "";
        Assert.False(pair.CanStart); await pair.StartAsync(); Assert.False(first.IsRunning); Assert.False(pair.IsBusy);
    }

    [Fact]
    public async Task Program_missing_on_second_side_and_abort_during_selection_are_handled()
    {
        var project = Program();
        var first = new StationViewModel("eins", () => project, new ResourceManager());
        var second = new StationViewModel("zwei", () => SampleProjects.AcPruefung(), new ResourceManager());
        var pair = new PairedStationsViewModel(new ObservableCollection<StationViewModel> { first, second }) { Enabled = true, Order = "A123" };
        await pair.StartAsync(); Assert.Contains("nicht verfügbar", pair.Status); Assert.False(pair.IsBusy);
        var path = Path.Combine(Path.GetTempPath(), "tu-pair-selection-" + Guid.NewGuid().ToString("N") + ".tproj.json");
        try
        {
            new ProjectSerializer().Save(new TestProject(Test(Group("loop").Add(Block(BlockCatalog.Wait, "pause", ("Dauer", "60000"))))), path);
            var setup = StationSetup.Default(project, 2);
            setup.Stations[0].Selection = new SelectionConfig { UseCustom = true, CustomProject = path };
            var ui = new QueueDispatcher();
            var shell = new ShellViewModel(ui, project: project, stations: new StationCoordinator(setup, new ResourceManager()));
            var selectedPair = shell.PairedStations; selectedPair.Enabled = true; selectedPair.Order = "A123";
            var selecting = selectedPair.StartAsync();
            await ui.Until(() => selectedPair.First!.Selection.IsActive, "gemeinsame Auftragsauswertung");
            selectedPair.Abort(); await ui.Until(() => selecting.IsCompleted, "Abbruch der Auftragsauswertung"); await selecting;
            Assert.False(selectedPair.IsBusy); Assert.False(selectedPair.First!.IsRunning); Assert.False(selectedPair.Second!.IsRunning);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(BlockCatalog.HandoverWait)]
    [InlineData(BlockCatalog.HandoverRelease)]
    public async Task Handover_block_refuses_standalone_run(string type)
    {
        var run = new TestRun(Test(Group("loop").Add(Block(type, "step"))));
        await run.RunAsync();
        Assert.Equal(ResultRating.Error, Assert.Single(run.Steps("step")).Rating);
    }

    [Fact]
    public async Task Automatic_partner_stop_preserves_awaited_finalization()
    {
        var project = Program();
        project.Definition.Finalization.Add(Block(BlockCatalog.Operator, "cleanup", ("Art", "Anweisung"), ("Text", "End bestätigen"), ("Warten", "true")));
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project,
            stations: new StationCoordinator(StationSetup.Default(project, 2), new ResourceManager()));
        var pair = shell.PairedStations; pair.Enabled = true; pair.Order = "A123";
        var running = pair.StartAsync();
        await ui.Until(() => pair.First!.ActiveContent?.IsAwaiting == true && pair.Second!.ActiveContent?.IsAwaiting == true, "Seriennummern");
        pair.Abort();
        await ui.Until(() => pair.First!.ActiveContent is { IsAwaiting: true, Text: "End bestätigen" }
            && pair.Second!.ActiveContent is { IsAwaiting: true, Text: "End bestätigen" }, "End auf beiden Seiten");
        pair.First!.ActiveContent!.ConfirmCommand.Execute(null);
        await ui.Until(() => !pair.First.IsRunning, "erste Seite beendet");
        ui.Pump();
        Assert.True(pair.Second!.ActiveContent!.IsAwaiting);
        Assert.False(running.IsCompleted);
        pair.Second.ActiveContent.ConfirmCommand.Execute(null);
        await ui.Until(() => running.IsCompleted, "zweite Seite beendet"); await running;
        Assert.False(pair.IsBusy);
    }
}
