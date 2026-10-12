using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class RunPatchBoundaryTests
{
    [Fact]
    public async Task Patch_validation_is_atomic_for_empty_unsupported_and_invalid_changes()
    {
        var rate = BlockCatalog.Default.Create(BlockCatalog.SetRating, "rate", "Rate", new Dictionary<string, string> { ["Bewertung"] = "PASS" });
        var later = Group("later").Add(rate, Steps.Rate("code", ResultRating.Pass));
        var def = Test(Group("loop").Add(Steps.Rate("pause", ResultRating.Pass), later));
        def.Library.Add(new CodeBlockDefinition("lib", "Library") { Source = "return Pass();" });
        def.GroupLibrary.Add(new GroupBlockDefinition("groupLib", "Group library", Group("body")));
        var debugger = new DebugController(); debugger.SetBreakpoint("pause");
        var run = new TestRun(def, new TestRunOptions { Clock = new VirtualClock(), Debugger = debugger });
        var paused = run.WaitFor<Paused>(); var task = run.RunAsync(); await paused.Within();
        try
        {
            Assert.False(run.ValidatePatch([]).IsValid);
            foreach (var change in new[]
            {
                new PatchChange("rate", "name", null), new PatchChange("rate", "name", " "),
                new PatchChange("rate", "unsupported", "x"), new PatchChange("later", "param:X", "x"),
                new PatchChange("code", "param:X", "x"), new PatchChange("missing", "name", "x"),
                new PatchChange("lib", "name", ""), new PatchChange("lib", "code", " "),
                new PatchChange("groupLib", "name", null), new PatchChange("later", "condition", "true +"),
                new PatchChange("rate", "move", "0"), new PatchChange("rate", "move", "bad"),
                new PatchChange("rate", "move", "-1"), new PatchChange("rate", "move", "5"),
            })
            {
                var result = run.ApplyPatch([new PatchChange("rate", "name", "must not apply"), change], "fixture");
                Assert.False(result.Applied); Assert.NotEmpty(result.Diagnostics); Assert.Equal("Rate", rate.Name); Assert.Equal(0, run.PatchCount);
            }
            Assert.True(run.ValidatePatch([new PatchChange("rate", "param:Bewertung", "FAIL"), new PatchChange("rate", "param:Bewertung", "PASS")]).IsValid);
            var resultOk = run.ApplyPatch([
                new PatchChange("rate", "name", " Changed "), new PatchChange("rate", "param:Bewertung", "FAIL"),
                new PatchChange("later", "condition", "true"), new PatchChange("lib", "name", " Renamed library "),
                new PatchChange("lib", "code", "return Fail();"), new PatchChange("groupLib", "name", " Renamed group ")], "fixture");
            Assert.True(resultOk.Applied); Assert.Equal(1, resultOk.PatchNumber); Assert.True(run.IsDevelopmentRun);
            Assert.Equal("Changed", rate.Name); Assert.Equal("Renamed library", def.Library[0].Name); Assert.Equal("return Fail();", def.Library[0].Source);
            Assert.Equal("Renamed group", def.GroupLibrary[0].Name);
            Assert.True(run.ApplyPatch([new PatchChange("later", "condition", null)], "fixture").Applied); Assert.Null(later.EntryCondition);
            Assert.True(run.ApplyPatch([new PatchChange("rate", "move", "1")], "fixture").Applied);
            Assert.Equal("rate", later.Children[^1].Id);
            Assert.True(run.ApplyPatch([new PatchChange("code", "delete", null)], "fixture").Applied); Assert.Single(later.Children);
        }
        finally { debugger.Continue(); await task.Within(); }
        Assert.Equal(ResultRating.Fail, run.Steps("rate").Single().Rating);
    }

    [Theory]
    [InlineData(ParameterKind.Code, true, false)]
    [InlineData(ParameterKind.Choice, false, true)]
    [InlineData(ParameterKind.Text, false, false)]
    public void Patch_row_tracks_changes_and_editor_type(ParameterKind kind, bool code, bool choice)
    {
        var row = new PatchRowViewModel("param:X", "X", kind, "original", ["one", "two"]);
        Assert.Equal(code, row.IsCode); Assert.Equal(choice, row.IsChoice); Assert.Equal(!code && !choice, row.IsText);
        Assert.False(row.IsChanged); row.Value = "new"; Assert.True(row.IsChanged); row.Value = "new";
        row.Value = "original"; Assert.False(row.IsChanged); Assert.Equal(2, row.Choices.Count);
        Assert.Empty(new PatchRowViewModel("name", "Name", ParameterKind.Text, "n").Choices);
    }
}
