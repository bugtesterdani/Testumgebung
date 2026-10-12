using System.Collections.Concurrent;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

/// <summary>UI-Thread-Ersatz: Aktionen werden gesammelt und vom Test „gepumpt“ (wie der WPF-Dispatcher).</summary>
internal sealed class QueueDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public void Post(Action action) => _queue.Enqueue(action);

    public void Pump()
    {
        while (_queue.TryDequeue(out var a)) a();
    }

    /// <summary>Pumpt, bis die Bedingung erfüllt ist.</summary>
    public async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Kit.Timeout;
        while (true)
        {
            Pump();
            if (condition()) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Nicht erreicht: {what}");
            await Task.Delay(5);
        }
    }
}

internal sealed class FakeDialogs(string path) : IFileDialogService
{
    public List<string> Errors { get; } = [];
    public string? OpenProject() => path;
    public string? SaveProject(string suggestedName) => path;
    public void ShowError(string message) => Errors.Add(message);
    public List<string> Opened { get; } = [];
    public void OpenExternal(string path) => Opened.Add(path);
}

public class EditorViewModelTests
{
    private static EditorViewModel Create() => new(SampleProjects.AcPruefung());

    private static PaletteItem Item(EditorViewModel e, string? type)
        => e.Palette.SelectMany(c => c.Items).First(i => i.BlockType == type);

    [Fact]
    public void Ablaufliste_zeigt_Abschnitte_nummerierte_Karten_und_Exit()
    {
        var e = Create();
        Assert.Equal(["Test Start / Init", "Test Loop / Testing", "Test End / Finalization"], e.Nodes.Select(n => n.Name));
        var pruefling = e.FindNode("pruefling")!;
        Assert.Equal("1", pruefling.Number);
        Assert.Equal(NodeIcon.DutGroup, pruefling.Icon);
        Assert.Equal("1.8", e.FindNode("belastung")!.Number);
        Assert.Equal("1.8.E", e.FindNode("belastung.exit")!.Number);
        Assert.True(e.FindNode("belastung.exit")!.IsExit);
        Assert.Contains("×3", e.FindNode("belastung")!.Annotation);
        Assert.Contains("wiederholt bis Werkerabbruch", e.Nodes[1].Annotation);
        Assert.Empty(e.Diagnostics);
        Assert.IsType<ProjectPropertiesViewModel>(e.Properties);
    }

    [Fact]
    public void Einfuegen_an_festen_Positionen()
    {
        var e = Create();
        e.SelectedNode = e.FindNode("spannung");                   // Schritt → danach einfügen
        var inserted = e.Insert(Item(e, BlockCatalog.Wait));
        var list = ((GroupDefinition)e.Project.Definition.Find("pruefling")!).Children;
        Assert.Equal(list.IndexOf(e.Project.Definition.Find("spannung")!) + 1, list.IndexOf(inserted.Definition));
        Assert.Same(inserted, e.SelectedNode);
        Assert.IsType<StepPropertiesViewModel>(e.Properties);

        e.SelectedNode = e.FindNode("belastung");                  // aufgeklappte Gruppe → ans Ende (vor Exit)
        var group = e.Insert(Item(e, null));
        var belastung = (GroupDefinition)e.Project.Definition.Find("belastung")!;
        Assert.Same(group.Definition, belastung.Children[^1]);
        Assert.Equal("1.9.E", e.FindNode("belastung.exit")!.Number); // Exit bleibt am Ende
        Assert.IsType<GroupPropertiesViewModel>(e.Properties);
        Assert.NotEqual(inserted.Id, group.Id);
        Assert.True(e.IsDirty);
    }

    [Fact]
    public void Verschieben_einruecken_ausruecken_erhaelt_IDs()
    {
        var e = Create();
        var node = e.FindNode("anschluss")!;
        e.SelectedNode = node;
        e.MoveBy(node, -1);
        var children = ((GroupDefinition)e.Project.Definition.Find("pruefling")!).Children.Select(c => c.Id).ToList();
        Assert.True(children.IndexOf("anschluss") < children.IndexOf("temperatur"));

        e.MoveBy(e.FindNode("anschluss"), +1);
        children = ((GroupDefinition)e.Project.Definition.Find("pruefling")!).Children.Select(c => c.Id).ToList();
        Assert.True(children.IndexOf("anschluss") > children.IndexOf("temperatur"));

        // In die vorherige Geschwistergruppe einrücken und wieder ausrücken
        e.Indent(e.FindNode("bewertung"));
        Assert.Equal("belastung", e.FindNode("bewertung")!.Parent!.Id);
        e.Outdent(e.FindNode("bewertung"));
        Assert.Equal("pruefling", e.FindNode("bewertung")!.Parent!.Id);
        Assert.Same(e.FindNode("bewertung"), e.SelectedNode);

        // Abschnitte und Exit-Gruppen sind nicht verschiebbar; nicht in sich selbst
        Assert.False(e.CanMove(e.Nodes[0], e.Nodes[1], 0));
        Assert.False(e.CanMove(e.FindNode("belastung.exit")!, e.Nodes[1], 0));
        Assert.False(e.CanMove(e.FindNode("pruefling")!, e.FindNode("belastung")!, 0));
        Assert.True(e.Move(e.FindNode("belastung")!, e.Nodes[0], 0));
        Assert.Equal("init", e.FindNode("belastung")!.Parent!.Id);
        Assert.Empty(e.Project.Definition.Validate());
    }

    [Fact]
    public void Loeschen_und_Exit_Gruppe_umschalten()
    {
        var e = Create();
        Assert.False(e.CanDelete(e.Nodes[0]));
        e.Delete(e.FindNode("temperatur"));
        Assert.Null(e.FindNode("temperatur"));
        Assert.Equal("pruefling", e.SelectedNode!.Id);

        e.SelectedNode = e.FindNode("belastung");
        var props = Assert.IsType<GroupPropertiesViewModel>(e.Properties);
        props.HasExitGroup = false;
        Assert.Null(e.FindNode("belastung.exit"));
        Assert.Null(((GroupDefinition)e.Project.Definition.Find("belastung")!).ExitGroup);
        ((GroupPropertiesViewModel)e.Properties!).HasExitGroup = true;
        Assert.Contains(e.FindNode("belastung")!.Children, c => c.IsExit);
    }

    [Fact]
    public void Gruppeneigenschaften_mit_Validierung()
    {
        var e = Create();
        e.SelectedNode = e.FindNode("belastung");
        var p = (GroupPropertiesViewModel)e.Properties!;

        p.EntryCondition = "RessourcenErhalten ==";
        Assert.NotNull(p.ConditionError);
        Assert.Null(((GroupDefinition)e.Project.Definition.Find("belastung")!).EntryCondition);
        p.EntryCondition = "Temperatur < 40";
        Assert.Null(p.ConditionError);
        Assert.Contains("wenn Temperatur < 40", e.FindNode("belastung")!.Annotation);

        p.RepeatMode = RepeatMode.Until;
        Assert.True(p.ShowExpression);
        p.RepeatExpression = "Versuch >= 2";
        Assert.Equal(new Repetition(RepeatMode.Until, 3, TimeSpan.Zero, "Versuch >= 2"), ((GroupDefinition)e.Project.Definition.Find("belastung")!).Repetition);

        p.Variables.Add();
        var row = p.Variables.Rows[^1];
        row.Type = VarType.Int;
        row.Initial = "abc";
        Assert.Equal("Wert muss Zahl sein", row.Error);
        row.Initial = "5";
        Assert.Null(row.Error);
        Assert.Contains(((GroupDefinition)e.Project.Definition.Find("belastung")!).Variables, v => v.Name == row.Name && Equals(v.Initial, 5L));

        p.Resources = "AC_Quelle, Spannung_Pruefling";
        Assert.Equal(["AC_Quelle", "Spannung_Pruefling"], ((GroupDefinition)e.Project.Definition.Find("belastung")!).Resources);
    }

    [Fact]
    public void Bausteinparameter_bearbeiten_mit_Diagnose()
    {
        var e = Create();
        e.SelectedNode = e.FindNode("spannung");
        var p = (StepPropertiesViewModel)e.Properties!;
        Assert.Equal("Messung", p.Title);
        var lower = p.Parameters.Single(x => x.Descriptor.Name == "Untergrenze");
        lower.Value = "GrenzwertMin +";
        Assert.True(p.HasDiagnostics);
        Assert.True(e.FindNode("spannung")!.HasDiagnostic);
        lower.Value = "225";
        Assert.False(p.HasDiagnostics);
        Assert.Equal("225", ((StepDefinition)e.Project.Definition.Find("spannung")!).Parameters["Untergrenze"]);

        e.SelectedNode = e.FindNode("sicht");
        var op = (StepPropertiesViewModel)e.Properties!;
        var wait = op.Parameters.Single(x => x.Descriptor.Name == "Warten");
        Assert.True(wait.IsBool && wait.BoolValue);
        var kind = op.Parameters.Single(x => x.Descriptor.Name == "Art");
        Assert.True(kind.IsChoice);
        Assert.Contains("Auswahl", kind.Choices);
    }

    [Fact]
    public void Speichern_und_Oeffnen_ueber_Dialogdienst()
    {
        var path = Path.Combine(Path.GetTempPath(), $"projekt-{Guid.NewGuid():N}.tproj.json");
        try
        {
            var dialogs = new FakeDialogs(path);
            var e = new EditorViewModel(SampleProjects.AcPruefung(), dialogs: dialogs);
            e.SelectedNode = e.FindNode("spannung");
            e.Insert(e.Palette.SelectMany(c => c.Items).First(i => i.BlockType == BlockCatalog.Wait));
            e.SaveAsCommand.Execute(null);
            Assert.False(e.IsDirty);
            Assert.Equal(path, e.FilePath);

            var other = new EditorViewModel(SampleProjects.AcPruefung(), dialogs: dialogs);
            var replaced = false;
            other.ProjectReplaced += () => replaced = true;
            other.OpenCommand.Execute(null);
            Assert.True(replaced);
            Assert.Equal(e.AllNodes().Select(n => n.Id), other.AllNodes().Select(n => n.Id));
            Assert.Empty(dialogs.Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class StationViewModelTests
{
    private static (ShellViewModel Shell, QueueDispatcher Ui) CreateShell(IClock? clock = null)
    {
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: SampleProjects.AcPruefung(), clock: clock ?? new VirtualClock());
        return (shell, ui);
    }

    private static async Task Answer(QueueDispatcher ui, StationViewModel s, string stepName, Action<OperatorContentViewModel> act)
    {
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true } c && c.Title == stepName, $"Werkerinteraktion {stepName}");
        act(s.ActiveContent!);
        ui.Pump();
    }

    private static async Task CompleteDut(QueueDispatcher ui, StationViewModel s, string serial)
    {
        await Answer(ui, s, "Kennung erfassen", c => { c.Input = serial; c.Confirm(); });
        await Answer(ui, s, "Sichtprüfung", c => c.ChooseCommand.Execute("i. O."));
        await Answer(ui, s, "Gehäusetemperatur erfassen", c =>
        {
            c.Input = "abc";
            c.Confirm();
            Assert.Equal("Wert muss Zahl sein", c.Error);                 // Validierung im Fenster
            c.Input = "60";
            c.Confirm();
            Assert.Equal("Zulässiger Bereich: 0 – 50 °C", c.Error);
            c.Input = "22,5";
            c.Confirm();
            Assert.Null(c.Error);
        });
        await Answer(ui, s, "Prüfling anschließen", c => c.ConfirmCommand.Execute(null));
    }

    [Fact]
    public async Task Werkeransicht_fuehrt_Pruefling_durch_den_Ablauf_und_zeigt_Ergebnisse()
    {
        var (shell, ui) = CreateShell();
        var s = shell.SelectedStation;
        Assert.True(s.CanStart);
        Assert.Equal("Bereit", s.StatusText);

        _ = s.StartAsync(); // kehrt erst nach Laufende zurück
        await ui.Until(() => s.IsRunning, "läuft");
        Assert.False(s.StartCommand.CanExecute(null));
        Assert.True(s.AbortCommand.CanExecute(null));
        Assert.Equal(9, s.Overview.Count);

        await CompleteDut(ui, s, "SN-10482");
        await ui.Until(() => s.Protocol.Any(p => p.Text.Contains("SN-10482 abgeschlossen")), "Prüfling abgeschlossen");

        var done = s.Protocol.Single(p => p.Text.Contains("SN-10482 abgeschlossen"));
        Assert.Equal(ResultRating.Pass, done.Rating);
        Assert.Equal("FA-2038", s.CurrentOrder);
        Assert.Contains(s.Protocol, p => p.Text == "Messung Spannung" && p.Kind == ProtocolKind.Measure && p.Unit == "V" && p.Limits == "220 – 240" && p.Rating == ResultRating.Pass);
        Assert.Contains(s.Protocol, p => p.Text == "Gehäusetemperatur erfassen" && p.Value == "22,5" && p.Unit == "°C");
        Assert.Contains(s.Protocol, p => p.Text == "Messung unter Last" && p.Iteration == "3");
        // Kurvenanzeige der Werkeransicht: L1–L3 der Aufnahme, Effektivwerte (Rückmessung der Ausgabekurve AC_Profil: 325 V Scheitel)
        Assert.True(s.HasLiveWaveform);
        Assert.StartsWith("Netzkurven aufnehmen", s.LiveWaveformTitle);
        Assert.Equal(["Spannung_L1", "Spannung_L2", "Spannung_L3"], s.LiveWaveform.Channels.Select(c => c.Id));
        Assert.All(s.LiveWaveform.Channels, c => Assert.StartsWith("229.8", c.RmsText));
        s.LiveWaveform.SetCursorAtX(125);                                        // 5 ms: Scheitel L1
        Assert.Equal("325 V", s.LiveWaveform.Channels[0].CursorText);

        // Zweiter Durchlauf beginnt, Übersicht zurückgesetzt
        await ui.Until(() => s.DutCount == 2 && s.ActiveContent is { IsAwaiting: true }, "zweiter Durchlauf");
        Assert.StartsWith("Durchlauf 2 • Schritt 1 von 9", s.ProgressText);
        Assert.Equal("Wartet auf Werker", s.StatusText);

        // Werkerabbruch während der Eingabe
        s.AbortCommand.Execute(null);
        await ui.Until(() => !s.IsRunning && s.LastStatus is not null, "Laufende");
        Assert.True(s.LastStatus!.Value.OperatorAbort);
        Assert.Equal(["Exit Prüfling (Prüfling)", "Hauptablauf Finalization"], s.ExitSequence.Select(x => x.Name));
        Assert.All(s.ExitSequence, x => Assert.Equal(ItemState.Done, x.State));
        Assert.Contains(s.Protocol, p => p.Kind == ProtocolKind.Abort && p.Status == "WERROR");
        Assert.Empty(s.Contents);
        Assert.Equal("Bereit", s.StatusText);
        Assert.True(s.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Entwickler_pausiert_aendert_Variable_und_setzt_fort()
    {
        var (shell, ui) = CreateShell();
        Assert.False(shell.NavigateCommand.CanExecute(ShellPage.Editor));
        shell.Login();
        Assert.Equal(ShellPage.Editor, shell.Page);
        Assert.Equal("ENTWICKLERANSICHT", shell.ViewTitle);

        var s = shell.SelectedStation;
        shell.Editor.ToggleBreakpoint(shell.Editor.FindNode("spannung"));
        Assert.True(shell.Editor.FindNode("spannung")!.HasBreakpoint);
        Assert.True(s.Debug.Controller.HasBreakpoint("spannung"));

        _ = s.StartAsync();
        await CompleteDut(ui, s, "SN-1");
        await ui.Until(() => s.Debug.IsPaused, "Pause");

        var d = s.Debug;
        Assert.Equal("Messung Spannung", d.NodeName);
        Assert.Equal(ResultRating.Pass, d.PendingRating);
        Assert.Equal(ResultRating.Pass, d.ParentRating);       // nur Sichtprüfung bisher übergeben
        Assert.Equal("SN-1", s.CurrentDutId);
        Assert.Contains(s.Measurements, m => m.Name == "Messung Spannung" && m.Rating == ResultRating.Pass);
        Assert.Equal(RunMarker.Paused, shell.Editor.FindNode("spannung")!.RunMarker);
        Assert.Equal("Pausiert", s.StatusText);
        Assert.StartsWith("Messung Spannung = 2", d.OutputText);
        var spannung = d.Variables.Single(v => v.Name == "Spannung");
        Assert.Equal("Prüfling", spannung.Origin);
        Assert.Contains(d.Variables, v => v.Name == "Nennspannung" && v.Origin == "Hauptablauf");

        spannung.ValueText = "abc";
        spannung.Commit();
        Assert.Equal("Wert muss Zahl sein", spannung.Error);
        spannung.ValueText = "250";
        spannung.Commit();
        Assert.Null(spannung.Error);
        ui.Pump();
        Assert.Equal("250", d.Variables.Single(v => v.Name == "Spannung").ValueText);
        Assert.Contains(d.DevelopmentLog, l => l.Kind == "Variable geändert" && l.User == "m.weber@simuliert.local"); // Rechte und Protokoll gehören dem angemeldeten Konto

        d.ContinueCommand.Execute(null);
        await ui.Until(() => !d.IsPaused, "fortgesetzt");
        s.RequestAbort();
        await ui.Until(() => !s.IsRunning && s.LastStatus is not null, "Ende");
        Assert.True(s.Run!.IsDevelopmentRun);

        shell.Logout();
        Assert.Equal(ShellPage.Operator, shell.Page);
        Assert.False(shell.IsDeveloper);
    }

    [Fact]
    public async Task Gezielte_Ausfuehrung_aus_dem_Editor()
    {
        var (shell, ui) = CreateShell();
        shell.Login();
        shell.Editor.SelectedNode = shell.Editor.FindNode("spannung");
        shell.SelectedStation.Debug.ContextText = "GrenzwertMax = 200";
        Assert.True(shell.ExecuteSelectedCommand.CanExecute(null));
        shell.ExecuteSelectedCommand.Execute(null);
        await ui.Until(() => shell.SelectedStation.Debug.TargetResult is not null, "Ergebnis");
        Assert.StartsWith("Ausgeführt: FAIL", shell.SelectedStation.Debug.TargetResult);

        shell.SelectedStation.Debug.ContextText = "";
        shell.Editor.SelectedNode = shell.Editor.FindNode("belastung");
        ((GroupPropertiesViewModel)shell.Editor.Properties!).EntryCondition = "Temperatur > 30";
        shell.ExecuteSelectedCommand.Execute(null);
        await ui.Until(() => shell.SelectedStation.Debug.TargetResult?.StartsWith("Verweigert") == true, "verweigert");
        Assert.Contains("Temperatur > 30", shell.SelectedStation.Debug.TargetResult);
    }

    [Fact]
    public async Task Zwei_Pruefplaetze_teilen_die_AC_Quelle()
    {
        var (shell, ui) = CreateShell(SystemClock.Instance); // echte Zeit: Ressourcen-Timeout 30 s läuft nicht ab
        var (a, b) = (shell.Stations[0], shell.Stations[1]);
        // B hält die AC-Quelle, solange A wartet
        // Gesperrt wird das Gerät (gemeinsame AC-Quelle), nicht der Name
        var key = shell.Coordinator.Setup.KeyOf(a.Name)("AC_Quelle");
        Assert.Equal("sim-gemeinsam:L1/L2/L3", key);
        var gate = await shell.Resources.AcquireAsync(new ResourceRequest("extern", "x", [key], "Wartung"), null, CancellationToken.None);
        _ = a.StartAsync();
        await CompleteDut(ui, a, "SN-A");
        await ui.Until(() => a.StatusText == "Warten auf AC_Quelle (belegt von Wartung)", "A wartet");
        Assert.Equal(StationStatusKind.Waiting, a.StatusKind);
        Assert.Equal("Bereit", b.StatusText);
        gate.Dispose();
        await ui.Until(() => a.Protocol.Any(p => p.Text.Contains("SN-A abgeschlossen")), "A fertig");
        a.RequestAbort();
        await ui.Until(() => !a.IsRunning && a.LastStatus is not null, "A Ende");
    }
}
