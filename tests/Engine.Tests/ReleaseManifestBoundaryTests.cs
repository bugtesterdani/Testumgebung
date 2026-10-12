using System.Text.Json.Nodes;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ReleaseManifestBoundaryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Legacy_library_metadata_uses_identifiers_and_empty_version(bool removeName, bool removeVersion)
    {
        var project = new TestProject(Test(Group("loop")));
        project.Definition.Library.Add(new CodeBlockDefinition("code-id", "Code name") { Version = "2" });
        project.Definition.GroupLibrary.Add(new GroupBlockDefinition("group-id", "Group name", Group("body")) { Version = "3" });
        var root = JsonNode.Parse(new ProjectSerializer().Serialize(project))!.AsObject();
        foreach (var section in new[] { "library", "groupLibrary" })
        {
            var block = root[section]![0]!.AsObject();
            if (removeName) block.Remove("name"); if (removeVersion) block.Remove("version");
        }
        var manifest = ReleasePackage.ComputeEntries(root.ToJsonString(), project);
        var blocks = manifest.Where(e => e.Area == "Bausteine").ToArray();
        Assert.Equal(removeName ? new[] { "code-id", "group-id" } : new[] { "Code name", "Group name" }, blocks.Select(e => e.Name));
        Assert.Equal(removeVersion ? new[] { "", "" } : new[] { "2", "3" }, blocks.Select(e => e.Version));
        Assert.All(manifest, e => Assert.Equal(64, e.Sha256.Length));
    }

    [Theory]
    [InlineData("C#", ".cs")]
    [InlineData("Python", ".py")]
    [InlineData("C++", ".cpp")]
    public void Sources_are_grouped_by_language_and_hash_tracks_exact_content(string language, string extension)
    {
        var step = BlockCatalog.Default.Create("code", "source/unsafe", "Source", new Dictionary<string, string> { ["Sprache"] = language, ["Quelltext"] = "original" });
        var project = new TestProject(Test(Group("loop").Add(step)));
        var file = Assert.Single(ReleasePackage.CodeFiles(project.Definition));
        Assert.EndsWith(extension, file.Path); Assert.Equal("original", file.Source); Assert.DoesNotContain("unsafe/", file.Path);
        var serializer = new ProjectSerializer();
        var before = ReleasePackage.ComputeEntries(serializer.Serialize(project), project).Single(e => e.Area == "Codeartefakte");
        step.Parameters["Quelltext"] = "changed";
        var after = ReleasePackage.ComputeEntries(serializer.Serialize(project), project).Single(e => e.Area == "Codeartefakte");
        Assert.NotEqual(before.Sha256, after.Sha256); Assert.Contains("1 Quelle(n)", after.Name);
    }

    [Fact]
    public void Missing_optional_sections_still_have_flow_curve_and_hardware_entries()
    {
        var project = new TestProject(Test(Group("loop")));
        var root = JsonNode.Parse(new ProjectSerializer().Serialize(project))!.AsObject();
        foreach (var section in new[] { "library", "groupLibrary", "assemblies", "curves", "simulation", "simulationOutputs" }) root.Remove(section);
        var manifest = ReleasePackage.ComputeEntries(root.ToJsonString(), project);
        Assert.Equal(new[] { "Ablauf", "Kurven", "Definitionen" }, manifest.Select(e => e.Area));
        Assert.Equal("keine", manifest[1].Name); Assert.All(manifest, e => Assert.Equal(64, e.Sha256.Length));
    }
}
