using Testumgebung.Contracts;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Pilot;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class EditorStatusBoundaryTests
{
    [Theory]
    [InlineData(CommKind.Stream)]
    [InlineData(CommKind.Modbus)]
    [InlineData(CommKind.Can)]
    [InlineData(CommKind.Lin)]
    [InlineData(CommKind.DoIp)]
    [InlineData(CommKind.OpcUa)]
    public void Communication_simulation_editor_updates_project_and_rejects_invalid_resource_names(CommKind kind)
    {
        var project = SampleProjects.AcPruefung(); var initial = new SimulatedComm("First", "Channel", CommKind.Stream, "");
        project.SimulationComm.Add(initial); project.SimulationComm.Add(new SimulatedComm("Other", "Channel", kind, ""));
        var editor = new ShellViewModel(project: project).Editor;
        var row = new CommSimulationRow(editor, initial); var changed = new List<string?>(); row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        foreach (var name in new string?[] { null, "", " ", "bad name", "Other" }) { row.Name = name!; Assert.Equal("First", row.Name); Assert.Equal("First", project.SimulationComm[0].Name); }
        row.Name = " Renamed "; Assert.Equal("Renamed", project.SimulationComm[0].Name);
        changed.Clear(); row.Name = "Renamed"; Assert.Empty(changed);
        row.Channel = " "; Assert.Equal("Channel", row.Channel); row.Channel = " Bus "; Assert.Equal("Bus", project.SimulationComm[0].Channel);
        row.Kind = kind; Assert.Equal(kind, project.SimulationComm[0].Kind); Assert.Contains(kind, row.Kinds); Assert.NotEmpty(row.Help);
        row.Rules = null!; Assert.Equal("", project.SimulationComm[0].Rules); row.RequiresLock = true; Assert.True(project.SimulationComm[0].RequiresLock);
        row.Kind = CommKind.Stream; row.Rules = "not a valid rule"; Assert.True(row.HasProblems); Assert.NotEmpty(row.Problems);
        row.Rules = ".* => OK"; Assert.False(row.HasProblems);
        project.SimulationComm.RemoveAt(0); row.Channel = "orphan channel"; Assert.Equal("orphan channel", row.Channel); Assert.Single(project.SimulationComm);
    }

    [Theory]
    [InlineData(ItemState.Pending, "Folgt")]
    [InlineData(ItemState.Running, "Läuft …")]
    [InlineData(ItemState.Done, "Erledigt")]
    [InlineData(ItemState.Skipped, "Nicht betreten")]
    [InlineData(ItemState.Aborted, "Abgebrochen")]
    [InlineData((ItemState)99, "")]
    public void Exit_state_description_and_rating_follow_current_cleanup_state(ItemState state, string expected)
    {
        var row = new ExitItemViewModel("id", 1, "Exit", "Cleanup"); row.State = state; row.State = state; Assert.Equal(expected, row.StateText);
        foreach (var rating in Enum.GetValues<ResultRating>().Select(r => (ResultRating?)r).Append(null)) { row.Rating = rating; row.Rating = rating; Assert.Equal(rating, row.Rating); }
        Assert.Equal("id", row.NodeId); Assert.Equal(1, row.Number); Assert.Equal("Exit", row.Name); Assert.Equal("Cleanup", row.Description);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Backup_rows_cannot_select_files_without_a_restore_target(bool target, bool secrets)
    {
        var entry = new BackupFileEntry("file", "description", "hash", 42, secrets); var row = new BackupFileRow(entry, target ? "target" : null);
        Assert.Equal(target, row.Selected); Assert.Equal(target, row.CanRestore); Assert.Equal(target ? "target" : "kein Ziel auf diesem Rechner", row.TargetText);
        row.Selected = false; Assert.False(row.Selected); row.Selected = true; Assert.Equal(target, row.Selected);
        Assert.Equal(secrets ? "enthält DPAPI-geschützte Geheimnisse" : "", row.SecretsText); Assert.Equal(entry.Name, row.Name); Assert.Equal(entry.Description, row.Description);
    }
}
