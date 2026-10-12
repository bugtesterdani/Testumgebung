using System.Reflection;
using Testumgebung.Contracts;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class PatchEditorWorkflowTests
{
    [Fact]
    public async Task Patch_editor_exposes_parameters_and_applies_only_valid_changes_to_paused_run()
    {
        var rated = BlockCatalog.Default.Create(BlockCatalog.SetRating, "rate", "Rated", new Dictionary<string, string> { ["Bewertung"] = "PASS" });
        rated.Parameters["extension"] = "retained";
        var group = Group("later").Add(rated, Steps.Rate("custom", ResultRating.Pass));
        var definition = Test(Group("loop").Add(Steps.Rate("pause", ResultRating.Pass), group));
        var project = new TestProject(definition);
        var station = new StationViewModel("station", () => project, new ResourceManager());
        var editor = new PatchEditorViewModel(station, () => "tester");
        editor.Select(null); Assert.False(editor.HasTarget); Assert.False(editor.Validate()); Assert.False(editor.Apply());
        var debug = station.Debug.Controller; debug.SetBreakpoint("pause");
        var run = new TestRun(definition, new TestRunOptions { Debugger = debug, Clock = new VirtualClock() });
        typeof(StationViewModel).GetField("_run", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(station, run);
        var paused = run.WaitFor<Paused>(); var task = run.RunAsync(); await paused.Within();
        try
        {
            editor.Select("missing"); Assert.False(editor.HasTarget); Assert.Empty(editor.Rows);
            editor.Select("custom"); Assert.Contains("nicht patchbar", editor.Title); Assert.Single(editor.Rows);
            editor.Select("rate"); Assert.True(editor.HasTarget); Assert.Contains("Rated", editor.Title);
            Assert.Equal("retained", editor.Rows.Single(r => r.Property == "param:extension").Value);
            var name = editor.Rows.Single(r => r.Property == "name"); name.Value = " ";
            Assert.False(editor.Validate()); Assert.False(editor.Valid); Assert.NotNull(editor.Status);
            Assert.False(editor.Apply()); Assert.Equal(0, editor.PatchCount); Assert.Equal("Rated", rated.Name);
            name.Value = "Updated"; Assert.True(editor.Validate()); Assert.True(editor.Valid);
            editor.ApplyCommand.Execute(null); Assert.Equal(1, editor.PatchCount); Assert.Equal("Updated", rated.Name);
            Assert.All(editor.Rows, row => Assert.False(row.IsChanged));
            editor.Select("later"); Assert.StartsWith("Gruppe:", editor.Title);
            editor.Rows.Single(r => r.Property == "condition").Value = "true";
            editor.ValidateCommand.Execute(null); Assert.True(editor.Valid); Assert.True(editor.Apply());
            Assert.Equal("true", group.EntryCondition); Assert.Equal(2, editor.PatchCount);
            rated.Parameters.Clear(); editor.Select("rate");
            Assert.Equal(BlockCatalog.Default.Get(BlockCatalog.SetRating).Parameters.Single(p => p.Name == "Bewertung").Default, editor.Rows.Single(r => r.Property == "param:Bewertung").Value);
            editor.Select(null); Assert.Equal("", editor.Title); Assert.Null(editor.Status); Assert.Null(editor.Valid);
        }
        finally { run.RequestAbort(); await task.Within(); }
    }
}
