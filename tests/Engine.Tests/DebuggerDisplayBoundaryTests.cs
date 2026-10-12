using System.Reflection;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Scopes;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class DebuggerDisplayBoundaryTests
{
    private static void Handle(DebugViewModel page, EngineEvent value)
        => typeof(DebugViewModel).GetMethod("Handle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [value]);
    private static DebugViewModel Create() => new ShellViewModel(new QueueDispatcher(), project: SampleProjects.AcPruefung(), clock: new VirtualClock()).SelectedStation.Debug;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Pause_display_preserves_step_outputs_and_group_snapshots(bool group, bool withValue)
    {
        var page = Create();
        Assert.Equal("", page.NodeName); Assert.Empty(page.Breadcrumb); Assert.Equal("", page.PositionText); Assert.Equal("", page.OutputText);
        Assert.Equal(ResultRating.NResult, page.PendingRating); Assert.Equal(ResultRating.NResult, page.ParentRating); Assert.False(page.IsGroupPause);
        Assert.False(page.ContinueCommand.CanExecute(null)); Assert.False(page.Patch.Validate()); Assert.False(page.Patch.Apply());
        page.SelectPatchNode(null); page.Patch.Select("missing"); Assert.False(page.Patch.HasTarget);
        var marks = new List<(string, RunMarker)>(); page.MarkNode = (id, marker) => marks.Add((id, marker));
        Handle(page, new StepStarted { Time = DateTimeOffset.UnixEpoch, RunId = "r", NodeId = "previous", Path = "previous" });
        Handle(page, new StepStarted { Time = DateTimeOffset.UnixEpoch, RunId = "r", NodeId = "node", Path = "node" });
        Assert.Contains(("previous", RunMarker.None), marks);
        if (!group && withValue) Handle(page, new StepCompleted(ResultRating.Pass, 2.5, "V", null, null, null, false) { Time = DateTimeOffset.UnixEpoch, RunId = "r", NodeId = "node", Path = "Root > Node" });
        var snapshot = withValue ? new ScopeSnapshot("scope", "g", "Group", [new VariableSnapshot("v", VarType.Double, 2.5, "Group")]) : null;
        Handle(page, new Paused(new PauseInfo("node", "Node", "Root > Node", 2, group, ResultRating.Fail, ResultRating.Pass, 3, snapshot)) { Time = DateTimeOffset.UnixEpoch, RunId = "r" });
        Assert.True(page.IsPaused); Assert.Equal("Node", page.NodeName); Assert.Equal(new[] { "Root", "Node" }, page.Breadcrumb);
        Assert.Equal(ResultRating.Fail, page.PendingRating); Assert.Equal(ResultRating.Pass, page.ParentRating); Assert.Equal(group, page.IsGroupPause);
        Assert.Contains("Iteration 2", page.PositionText); Assert.Contains("Ausführung 3", page.PositionText);
        Assert.Equal(group ? (withValue ? "1" : "0") + " lokale Variablen (Schnappschuss)" : withValue ? "Node = 2,5 V" : "—", page.OutputText);
        Assert.Equal(withValue ? 1 : 0, page.Snapshot.Count);
        Assert.True(page.ContinueCommand.CanExecute(null)); page.SelectPatchNode("another");
        Handle(page, new DevelopmentEvent("tester", "changed", "details") { Time = DateTimeOffset.UnixEpoch, RunId = "r" });
        Assert.Equal("changed", Assert.Single(page.DevelopmentLog).Kind); Assert.NotEmpty(page.DevelopmentLog[0].TimeText);
        Handle(page, new Resumed(DebugCommand.Continue) { Time = DateTimeOffset.UnixEpoch, RunId = "r" });
        Handle(page, new Resumed(DebugCommand.Continue) { Time = DateTimeOffset.UnixEpoch, RunId = "r" });
        Assert.False(page.IsPaused); Assert.Empty(page.Variables); Assert.False(page.Patch.HasTarget);
    }

    [Theory]
    [InlineData("entry", CodeDebugCapability.EntryExitOnly, "Eintritt", "Nur Eintritt/Austritt (reduziert)")]
    [InlineData("exit", CodeDebugCapability.EntryExitOnly, "Austritt", "Nur Eintritt/Austritt (reduziert)")]
    [InlineData("line", CodeDebugCapability.FullLineStepping, "Zeile 7", "Zeilen-Stepping")]
    [InlineData("line", (CodeDebugCapability)99, "Zeile 7", "")]
    public void Code_pause_display_describes_language_capability_and_clears_state(string kind, CodeDebugCapability capability, string position, string capabilityText)
    {
        var page = Create(); Assert.False(page.IsCodePaused); Assert.Equal(0, page.CodeLine); Assert.Equal("", page.CodeNodeName);
        Assert.Equal("", page.CodePositionText); Assert.Equal("", page.CodeCapabilityText); page.ClearCodePause();
        page.StopOnCodeEntry = true; page.StopOnCodeEntry = true; Assert.True(page.StopOnCodeEntry); page.StopOnCodeEntry = false;
        page.ToggleCodeBreakpoint("node", 7); Assert.True(page.HasCodeBreakpoint("node", 7)); page.ToggleCodeBreakpoint("node", 7); Assert.False(page.HasCodeBreakpoint("node", 7));
        typeof(DebugViewModel).GetMethod("OnCodePaused", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [new CodeLinePause("node", "Code", "Root > Code", 1, 7, kind, capability, [new CodeDebugVariable("v", 1L)])]);
        Assert.True(page.IsCodePaused); Assert.Equal(7, page.CodeLine); Assert.Equal("Code", page.CodeNodeName);
        Assert.Contains(position, page.CodePositionText); Assert.Equal(capabilityText, page.CodeCapabilityText); Assert.Single(page.CodeVariables);
        Assert.True(page.CodeContinueCommand.CanExecute(null)); Assert.True(page.CodeStepCommand.CanExecute(null)); Assert.True(page.CodeStopCommand.CanExecute(null));
        page.ClearCodePause(); Assert.False(page.IsCodePaused); Assert.Empty(page.CodeVariables); Assert.False(page.CodeContinueCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("value", false)]
    [InlineData("../value", true)]
    public void Variable_row_keeps_origin_and_reports_edits_without_active_pause(string path, bool shadowed)
    {
        var page = Create(); var row = new DebugVariableRowViewModel(page, new DebugVariable("value", path, VarType.String, "original", "Group"));
        Assert.Equal(path, row.Name); Assert.Equal("string", row.Type); Assert.Equal("Group", row.Origin); Assert.Equal(shadowed, row.IsShadowed);
        row.Commit(); Assert.Null(row.Error); Assert.Equal("OK", row.StatusText);
        row.ValueText = "new"; row.CommitCommand.Execute(null); Assert.NotNull(row.Error); Assert.Equal(row.Error, row.StatusText);
        row.ValueText = "other"; Assert.Null(row.Error); row.ValueText = "other";
    }
}


