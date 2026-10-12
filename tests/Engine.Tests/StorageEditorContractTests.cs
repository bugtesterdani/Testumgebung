using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Storage;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class StorageEditorContractTests
{
    public static IEnumerable<object[]> Settings()
    {
        foreach (var target in StorageSettings.Targets)
            foreach (var parent in new[] { "An", "Aus", "Erben" })
                foreach (var child in new[] { "An", "Aus", "Erben", "unbekannt" })
                    yield return [target, parent, child];
    }

    [Theory, MemberData(nameof(Settings))]
    public void Storage_overrides_follow_parent_and_survive_project_reload(StorageTarget target, string parent, string child)
    {
        var group = new GroupDefinition("loop", "Loop");
        var step = BlockCatalog.Default.Create("warten", "step");
        group.Children.Add(step);
        var project = new TestProject(Kit.Test(group));
        var editor = new EditorViewModel(project);
        var parentSettings = new StorageSettingsViewModel(editor, group);
        var childSettings = new StorageSettingsViewModel(editor, step);
        var parentRow = Assert.Single(parentSettings.Rows, r => r.Target == target);
        var childRow = Assert.Single(childSettings.Rows, r => r.Target == target);
        parentRow.Selected = parent;
        childRow.Selected = child;
        childRow.Selected = child;
        bool? expectedOverride = child switch { "An" => true, "Aus" => false, _ => null };
        Assert.Equal(expectedOverride, step.Storage.Get(target));
        Assert.Equal(expectedOverride is null ? "Erben" : child, childRow.Selected);
        var expected = expectedOverride ?? new StorageResolver(project.Definition).Explain(group.Id, target).Value;
        Assert.Equal(!expected, childRow.IsOff);
        Assert.Contains(expected ? "an" : "aus", childRow.EffectiveText);
        Assert.Equal(3, childRow.Options.Count);
        Assert.False(string.IsNullOrWhiteSpace(childRow.Label));
        Assert.True(parentSettings.IsGroup);
        Assert.False(childSettings.IsGroup);
        Assert.NotEmpty(childSettings.Hint);
        Assert.NotEmpty(parentSettings.Hint);
        Assert.NotEmpty(childSettings.MinimalSet);
        var copy = new ProjectSerializer().Clone(project);
        Assert.Equal(expectedOverride, copy.Definition.Find("step")!.Storage.Get(target));
        Assert.Equal(expected, new StorageResolver(copy.Definition).Explain("step", target).Value);
        Assert.Equal(!step.Storage.IsInherit, childSettings.ResetCommand.CanExecute(null));
        childSettings.ResetCommand.Execute(null);
        Assert.True(step.Storage.IsInherit);
        Assert.False(childSettings.ResetCommand.CanExecute(null));
        Assert.Equal(!new StorageResolver(project.Definition).Explain(group.Id, target).Value, childRow.IsOff);
    }
}
