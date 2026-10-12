using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class GroupEditorContractTests
{
    public static IEnumerable<object[]> Repetitions()
    {
        foreach (var mode in Enum.GetValues<RepeatMode>())
            foreach (var policy in Enum.GetValues<RepeatResultPolicy>())
                foreach (var stop in new[] { false, true })
                    yield return [mode, policy, stop];
    }

    [Theory, MemberData(nameof(Repetitions))]
    public void Group_settings_edited_in_properties_survive_reload(RepeatMode mode, RepeatResultPolicy policy, bool stop)
    {
        var group = new GroupDefinition("loop", "Loop");
        var project = new TestProject(Kit.Test(group));
        var editor = new EditorViewModel(project) { SelectedNode = null };
        editor.SelectedNode = editor.FindNode("loop");
        var properties = Assert.IsType<GroupPropertiesViewModel>(editor.Properties);
        properties.Name = "Messgruppe";
        properties.Description = "Beschreibung";
        properties.Count = 0;
        Assert.Equal(1, properties.Count);
        properties.Count = 3;
        properties.DurationSeconds = -1;
        Assert.Equal(.001, properties.DurationSeconds);
        properties.DurationSeconds = 2;
        properties.RepeatExpression = "true";
        properties.RepeatMode = mode;
        properties.ResultBeforeRepeat = policy;
        properties.StopWithinIteration = stop;
        properties.StopWithinIteration = stop;
        properties.EntryCondition = "true";
        properties.Resources = "COM1; TCP, COM1;  ";
        properties.ResourceTimeoutSeconds = -1;
        Assert.Null(properties.ResourceTimeoutSeconds);
        properties.ResourceTimeoutSeconds = null;
        properties.ResourceTimeoutSeconds = 5;
        properties.IsDutRun = true;
        properties.DutIdVariable = "Seriennummer";
        Assert.Equal(mode == RepeatMode.Count, properties.ShowCount);
        Assert.Equal(mode == RepeatMode.Duration, properties.ShowDuration);
        Assert.Equal(mode is RepeatMode.While or RepeatMode.Until, properties.ShowExpression);
        var copy = Assert.IsType<GroupDefinition>(new ProjectSerializer().Clone(project).Definition.Find("loop"));
        Assert.Equal("Messgruppe", copy.Name);
        Assert.Equal("Beschreibung", copy.Description);
        Assert.Equal(mode, copy.Repetition.Mode);
        Assert.Equal(stop, copy.Repetition.StopWithinIteration);
        if (mode == RepeatMode.Count) Assert.Equal(3, copy.Repetition.Count);
        if (mode == RepeatMode.Duration) Assert.Equal(TimeSpan.FromSeconds(2), copy.Repetition.Duration);
        if (mode is RepeatMode.While or RepeatMode.Until) Assert.Equal("true", copy.Repetition.Expression);
        Assert.Equal(policy, copy.ResultBeforeRepeat);
        Assert.Equal(new[] { "COM1", "TCP" }, copy.Resources);
        Assert.Equal(TimeSpan.FromSeconds(5), copy.ResourceTimeout);
        Assert.Equal("true", copy.EntryCondition);
        Assert.True(copy.IsDutRun);
        Assert.Equal("Seriennummer", copy.DutIdVariable);
        properties.EntryCondition = "invalid(";
        Assert.NotNull(properties.ConditionError);
        Assert.Equal("true", group.EntryCondition);
        properties.EntryCondition = " ";
        Assert.Null(properties.ConditionError);
        Assert.Null(group.EntryCondition);
        foreach (var invalid in new[] { "", " ", "invalid(" })
        {
            properties.RepeatExpression = invalid;
            Assert.NotNull(properties.RepeatError);
            Assert.Equal("true", group.Repetition.Expression);
        }
        properties.RepeatExpression = "false";
        Assert.Null(properties.RepeatError);
        Assert.Equal("false", group.Repetition.Expression);
        properties.HasExitGroup = true;
        properties.HasExitGroup = true;
        Assert.NotNull(group.ExitGroup);
        Assert.True(properties.CanHaveExit);
        properties.HasExitGroup = false;
        Assert.Null(group.ExitGroup);
    }

    [Fact]
    public void Loop_variable_edits_recover_after_invalid_and_duplicate_input()
    {
        var group = new GroupDefinition("loop", "Loop");
        var changes = 0;
        var list = new VariableListViewModel(group, () => changes++);
        list.AddCommand.Execute(null);
        list.AddCommand.Execute(null);
        Assert.Equal(new[] { "Variable1", "Variable2" }, group.Variables.Select(v => v.Name));
        var first = list.Rows[0];
        var second = list.Rows[1];
        first.Type = VarType.String;
        first.Name = "Auftrag";
        first.Initial = "A123";
        second.Name = "Auftrag";
        Assert.All(list.Rows, row => Assert.Equal("Name doppelt", row.Error));
        second.Name = "Seriennummer";
        Assert.All(list.Rows, row => Assert.Null(row.Error));
        Assert.Equal(new[] { "Auftrag", "Seriennummer" }, group.Variables.Select(v => v.Name));
        first.Name = "invalid name";
        Assert.NotNull(first.Error);
        Assert.Equal("Auftrag", group.Variables[0].Name);
        first.Name = "FA";
        Assert.Equal("FA", group.Variables[0].Name);
        Assert.Equal("A123", group.Variables[0].Initial);
        list.RemoveCommand.Execute(null);
        Assert.Equal(2, list.Rows.Count);
        list.RemoveCommand.Execute(second);
        Assert.Single(group.Variables);
        Assert.True(changes > 0);
    }
}

