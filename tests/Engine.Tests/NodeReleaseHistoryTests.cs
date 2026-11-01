using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class NodeReleaseHistoryTests
{
    private sealed class Fixture : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "node-history-" + Guid.NewGuid());
        public ReleaseService Service { get; }
        public TestProject Project { get; }
        public StepDefinition Step { get; }
        public Fixture()
        {
            Service = new(new FolderReleaseStore(Folder));
            Step = BlockCatalog.Default.Create("code", "step");
            Step.Parameters["Grenze"] = "10";
            Step.Description = "Erste Zeile\nZweite Zeile";
            var loop = new GroupDefinition("loop", "Prüfen");
            loop.Children.Add(Step);
            Project = new(Kit.Test(loop));
        }
        public async Task<ReleaseInfo> Add(string version, int day, ReleaseState state = ReleaseState.Archived, string? projectName = null, bool noTime = false)
        {
            var project = new ProjectSerializer().Clone(Project);
            if (projectName is not null)
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(new ProjectSerializer().Serialize(project))!;
                json["name"] = projectName;
                project = new ProjectSerializer().Deserialize(json.ToJsonString());
            }
            project.Definition.Version = version;
            var time = new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero);
            var bytes = ReleasePackage.Build(project, "Tester", time).Bytes;
            var info = new ReleaseInfo(Guid.NewGuid().ToString("N"), project.Definition.Name, version, state,
                ReleasePackage.Sha256(bytes), bytes.Length, "Tester", time, ReleasedAt: state is ReleaseState.Draft or ReleaseState.Review || noTime ? null : time);
            await Service.Store.CreateAsync(info, bytes, new AuditDraft(time, "Tester", "Fixture", version));
            return info;
        }
        public void Dispose() { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); }
    }

    [Fact]
    public async Task Unchanged_values_keep_their_origin_and_changes_use_release_dates_not_version_names()
    {
        using var f = new Fixture();
        var first = await f.Add("10", 1);
        await f.Add("2", 2);
        f.Step.Parameters["Grenze"] = "20";
        var third = await f.Add("1", 3, ReleaseState.Released);
        f.Step.Parameters["Grenze"] = "999";
        await f.Add("draft", 4, ReleaseState.Draft);
        await f.Add("review", 5, ReleaseState.Review);
        await f.Add("other", 6, ReleaseState.Released, "Anderes Programm");
        var rows = await NodeReleaseHistory.LoadAsync(f.Service, f.Project.Definition.Name, "step");
        var values = rows.Where(r => r.Field == "Parameter / Grenze").ToArray();
        Assert.Equal(2, values.Length);
        Assert.Equal("10", values[0].Value); Assert.Equal(first, values[0].Since);
        Assert.Equal(third, values[0].Until); Assert.True(values[0].FirstRecorded);
        Assert.Equal("10", values[1].Before); Assert.Equal("20", values[1].Value);
        Assert.Equal(third, values[1].Since); Assert.Null(values[1].Until); Assert.True(values[1].IsLatestValue);
        Assert.Contains("v1", values[1].SinceText); Assert.Contains("bis vor v1", values[0].UntilText);
        Assert.Single(rows, r => r.Field == "Kommentar / Verlauf");
        Assert.Equal("Erste Zeile\nZweite Zeile", rows.Single(r => r.Field == "Kommentar / Verlauf").Value);
    }

    [Fact]
    public async Task Rename_move_removal_and_reappearance_use_stable_element_ids()
    {
        using var f = new Fixture();
        await f.Add("1", 1);
        f.Step.Name = "Neuer Name";
        f.Project.Definition.Loop.Children.Clear();
        f.Project.Definition.Init.Children.Add(f.Step);
        await f.Add("2", 2);
        f.Project.Definition.Init.Children.Clear();
        await f.Add("3", 3);
        f.Project.Definition.Loop.Children.Add(f.Step);
        await f.Add("4", 4, ReleaseState.Released);
        var rows = await NodeReleaseHistory.LoadAsync(f.Service, f.Project.Definition.Name, "step");
        var name = rows.Where(r => r.Field == "Bezeichnung").ToArray();
        Assert.Equal(4, name.Length); Assert.Equal("2", name[1].Since.Version);
        Assert.Null(name[2].Value); Assert.Equal("Entfernt / zurückgesetzt", name[2].ChangeText);
        Assert.Equal("4", name[2].Until!.Version); Assert.False(name[2].IsLatestValue);
        Assert.Equal("Hinzugefügt / gesetzt", name[3].ChangeText);
        Assert.Equal(3, rows.Count(r => r.Field == "Parameter / Grenze")); // Verschieben erzeugt keinen neuen Feldwert.
        Assert.Empty(await NodeReleaseHistory.LoadAsync(f.Service, f.Project.Definition.Name, "unbekannt"));
    }

    [Fact]
    public async Task Group_history_contains_own_variables_and_settings_without_child_changes()
    {
        using var f = new Fixture();
        var group = f.Project.Definition.Loop;
        group.Variables.Add(new("Spannung", VarType.Double, 12d));
        group.Variables.Add(new("Strom", VarType.Double, 1d));
        await f.Add("1", 1);
        group.Variables.Reverse();
        f.Step.Parameters["Grenze"] = "30";
        await f.Add("2", 2);
        group.Variables.RemoveAll(v => v.Name == "Spannung");
        group.Variables.Add(new("Spannung", VarType.Double, 24d));
        group.Parallel = true;
        await f.Add("3", 3, ReleaseState.Released);
        var rows = await NodeReleaseHistory.LoadAsync(f.Service, f.Project.Definition.Name, "loop");
        Assert.DoesNotContain(rows, r => r.Field.Contains("Grenze"));
        var voltage = rows.Where(r => r.Field == "Variablen / Spannung / Anfangswert").ToArray();
        Assert.Equal(2, voltage.Length); Assert.Equal("12", voltage[1].Before); Assert.Equal("24", voltage[1].Value);
        Assert.Equal("3", voltage[1].Since.Version);
        Assert.Contains(rows, r => r.Field == "Parallele Ausführung" && r.Value == "true");
    }

    [Fact]
    public async Task Referenced_library_source_changes_are_visible_on_the_call_card()
    {
        using var f = new Fixture();
        var call = BlockCatalog.Default.Create(BlockCatalog.LibraryBlock, "call");
        call.Parameters["Baustein"] = "lib";
        f.Project.Definition.Loop.Children.Add(call);
        var library = new CodeBlockDefinition("lib", "Bibliothek") { Source = "return Pass();" };
        f.Project.Definition.Library.Add(library);
        await f.Add("1", 1);
        library.Source = "// Begründung\nreturn Pass(2);";
        await f.Add("2", 2, ReleaseState.Released);
        var rows = await NodeReleaseHistory.LoadAsync(f.Service, f.Project.Definition.Name, "call");
        var source = rows.Where(r => r.Field.EndsWith("Quelltext")).ToArray();
        Assert.Equal(2, source.Length); Assert.Contains("Begründung", source[1].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_changed_package_clears_the_view_instead_of_showing_partial_validity(bool delete)
    {
        using var f = new Fixture();
        var first = await f.Add("1", 1);
        await f.Add("2", 2, ReleaseState.Released);
        var model = new NodeHistoryViewModel(f.Service, f.Project.Definition.Name, "step", f.Step.Name);
        await model.LoadAsync(); Assert.NotEmpty(model.Rows);
        var path = Path.Combine(f.Folder, first.ReleaseId + ".tpkg");
        if (delete) File.Delete(path);
        else File.WriteAllBytes(path, ReleasePackage.Build(f.Project, "changed", DateTimeOffset.UtcNow).Bytes);
        await model.LoadAsync();
        Assert.Empty(model.Rows); Assert.Contains("nicht vollständig belegbar", model.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_or_missing_release_timestamp_is_not_presented_as_a_valid_period(bool missing)
    {
        using var f = new Fixture();
        await f.Add("1", 1);
        await f.Add("2", 1, ReleaseState.Released, noTime: missing);
        await Assert.ThrowsAsync<ReleaseRuleException>(() => NodeReleaseHistory.LoadAsync(f.Service, f.Project.Definition.Name, "step"));
    }

    [Fact]
    public async Task View_filters_fields_and_versions_and_excludes_superseded_values()
    {
        using var f = new Fixture();
        await f.Add("1", 1);
        f.Step.Parameters["Grenze"] = "20";
        await f.Add("2", 2, ReleaseState.Released);
        var model = new NodeHistoryViewModel(f.Service, f.Project.Definition.Name, "step", "Karte");
        await model.LoadAsync();
        model.Filter = "Grenze"; Assert.Equal(2, model.Rows.Count);
        model.Selected = model.Rows.Last(); model.LatestOnly = true;
        Assert.Single(model.Rows); Assert.Null(model.Selected); Assert.Equal("20", model.Rows[0].Value);
        model.Filter = "unbekannt"; Assert.Empty(model.Rows);
        model.Filter = ""; Assert.NotEmpty(model.Rows);
        var offline = new NodeHistoryViewModel(null, "test", "id", "Karte");
        await offline.LoadAsync(); Assert.Contains("Keine Release-Ablage", offline.Status);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await model.LoadAsync(cancelled.Token); Assert.Empty(model.Rows); Assert.Contains("abgebrochen", model.Status);
    }
}
