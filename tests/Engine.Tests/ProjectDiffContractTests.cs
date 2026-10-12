using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Storage;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;

namespace Testumgebung.Engine.Tests;

public sealed class ProjectDiffContractTests
{
    private static GroupDefinition Group(TestProject p) => (GroupDefinition)p.Definition.Find("group")!;
    private static StepDefinition Step(TestProject p) => (StepDefinition)p.Definition.Find("step")!;
    private static readonly Dictionary<string, Action<TestProject>> Changes = new()
    {
        ["name"] = p => Group(p).Name = "Neue Gruppe",
        ["description"] = p => Group(p).Description = new string('x', 250),
        ["storage"] = p => Group(p).Storage = Group(p).Storage.With(StorageTarget.File, false),
        ["condition"] = p => Group(p).EntryCondition = "true",
        ["reset"] = p => Group(p).ResultBeforeRepeat = RepeatResultPolicy.ResetToNResult,
        ["resources"] = p => Group(p).Resources.Add("DMM"),
        ["timeout"] = p => Group(p).ResourceTimeout = TimeSpan.FromSeconds(3),
        ["dut"] = p => Group(p).IsDutRun = true,
        ["exit"] = p => Group(p).ExitGroup = new GroupDefinition("exit", "Exit"),
        ["variable-add"] = p => Group(p).Variables.Add(new VariableDefinition("Added", VarType.Bool, true)),
        ["variable-remove"] = p => Group(p).Variables.Clear(),
        ["variable-type"] = p => { Group(p).Variables.Clear(); Group(p).Variables.Add(new VariableDefinition("Value", VarType.Int, 1L)); },
        ["variable-value"] = p => { Group(p).Variables.Clear(); Group(p).Variables.Add(new VariableDefinition("Value", VarType.String, "new")); },
        ["step-code"] = p => Step(p).Parameters["Code"] = "return Pass(2);",
        ["step-parameter"] = p => Step(p).Parameters["Added"] = "new",
        ["step-block"] = p => Group(p).Children[0] = BlockCatalog.Default.Create("warten", "step"),
        ["step-remove"] = p => Group(p).Children.Clear(),
        ["step-add"] = p => Group(p).Children.Add(BlockCatalog.Default.Create("code", "second")),
        ["kind"] = p => { Group(p).Children.Clear(); Group(p).Children.Add(new GroupDefinition("step", "Code")); },
        ["code-version"] = p => p.Definition.Library[0].Version = "2",
        ["code-source"] = p => p.Definition.Library[0].Source = "return Pass(2);\n//new",
        ["code-port"] = p => p.Definition.Library[0].Ports.Add(new CodePort("Input", VarType.String, PortDirection.Input)),
        ["code-remove"] = p => p.Definition.Library.Clear(),
        ["code-add"] = p => p.Definition.Library.Add(new CodeBlockDefinition("added", "Added")),
        ["group-version"] = p => p.Definition.GroupLibrary[0].Version = "2",
        ["group-port"] = p => p.Definition.GroupLibrary[0].Ports.Add(new CodePort("Input", VarType.Double, PortDirection.Input)),
        ["group-remove"] = p => p.Definition.GroupLibrary.Clear(),
        ["group-add"] = p => p.Definition.GroupLibrary.Add(new GroupBlockDefinition("added", "Added", new GroupDefinition("added.body", "Body"))),
        ["project-description"] = p => p.Description = "Änderung",
    };

    public static IEnumerable<object[]> Cases() => Changes.Keys.Select(k => new object[] { k });

    private static TestProject Project()
    {
        var group = new GroupDefinition("group", "Group");
        group.Variables.Add(new VariableDefinition("Value", VarType.String, "old"));
        var step = BlockCatalog.Default.Create("code", "step"); step.Parameters["Code"] = "return Pass(1);";
        BlockCatalog.Default.Rebuild(step); group.Children.Add(step);
        var loop = new GroupDefinition("loop", "Loop"); loop.Children.Add(group);
        var project = new TestProject(Kit.Test(loop));
        project.Definition.Library.Add(new CodeBlockDefinition("code", "Code") { Source = "return Pass(1);" });
        project.Definition.GroupLibrary.Add(new GroupBlockDefinition("groupblock", "Groupblock", new GroupDefinition("body", "Body")));
        return project;
    }

    [Theory, MemberData(nameof(Cases))]
    public void Version_comparison_reports_each_edit_in_both_directions_and_keeps_inputs(string change)
    {
        var serializer = new ProjectSerializer();
        var before = Project(); var after = serializer.Clone(before);
        Assert.Empty(ProjectDiff.Compare(before, after));
        Changes[change](after);
        var first = serializer.Serialize(before); var second = serializer.Serialize(after);
        var forward = ProjectDiff.Compare(before, after); var reverse = ProjectDiff.Compare(after, before);
        Assert.NotEmpty(forward); Assert.NotEmpty(reverse);
        Assert.All(forward.Concat(reverse), row => { Assert.False(string.IsNullOrWhiteSpace(row.Area)); Assert.False(string.IsNullOrWhiteSpace(row.Item)); Assert.False(string.IsNullOrWhiteSpace(row.KindText)); });
        Assert.Equal(first, serializer.Serialize(before)); Assert.Equal(second, serializer.Serialize(after));
        Assert.Empty(ProjectDiff.Compare(after, serializer.Clone(after)));
        if (forward.Any(e => e.Kind == DiffKind.Added)) Assert.Contains(reverse, e => e.Kind == DiffKind.Removed);
        if (forward.Any(e => e.Kind == DiffKind.Removed)) Assert.Contains(reverse, e => e.Kind == DiffKind.Added);
    }

    [Theory]
    [InlineData(RepeatMode.Once)]
    [InlineData(RepeatMode.Count)]
    [InlineData(RepeatMode.Duration)]
    [InlineData(RepeatMode.While)]
    [InlineData(RepeatMode.Until)]
    [InlineData(RepeatMode.Endless)]
    public void Repetition_changes_are_visible_and_described(RepeatMode mode)
    {
        var before = Project(); var after = new ProjectSerializer().Clone(before);
        Group(before).Repetition = new Repetition(RepeatMode.Count, 3);
        Group(after).Repetition = new Repetition(mode, 2, TimeSpan.FromSeconds(5), "true");
        Assert.Contains(ProjectDiff.Compare(before, after), d => d.Detail.StartsWith("Wiederholung:"));
    }
}
