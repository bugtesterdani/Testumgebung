using System.Text.Json.Nodes;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Curves;
using Testumgebung.Persistence;

namespace Testumgebung.Engine.Tests;

public sealed class ProjectMetadataBoundaryTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("format")]
    [InlineData("version")]
    [InlineData("init")]
    [InlineData("loop")]
    [InlineData("finalization")]
    [InlineData("assembly")]
    [InlineData("duplicate-assembly")]
    [InlineData("group-body")]
    [InlineData("duplicate-bus")]
    public void Malformed_project_metadata_is_rejected_with_project_error(string damage)
    {
        var project = new TestProject(Kit.Test(Kit.Group("loop"))); var serializer = new ProjectSerializer();
        var json = JsonNode.Parse(serializer.Serialize(project))!.AsObject();
        switch (damage)
        {
            case "null": Assert.Throws<ProjectFormatException>(() => serializer.Deserialize("null")); return;
            case "format": json["format"] = "other"; break;
            case "version": json["formatVersion"] = 99; break;
            case "init" or "loop" or "finalization": json[damage] = null; break;
            case "assembly": json["assemblies"] = JsonNode.Parse("[{\"name\":\"bad.dll\",\"data\":\"AQID\"}]"); break;
            case "duplicate-assembly":
                project.Definition.Assemblies.Add(ManagedAssemblyImport.Read(typeof(OrderHelper.Rules).Assembly.Location)); json = JsonNode.Parse(serializer.Serialize(project))!.AsObject(); var assemblies = json["assemblies"]!.AsArray(); assemblies.Add(assemblies[0]!.DeepClone()); break;
            case "group-body": json["groupLibrary"] = JsonNode.Parse("[{\"id\":\"group\",\"name\":\"Group\"}]"); break;
            case "duplicate-bus": json["busDatabases"] = JsonNode.Parse("[{\"name\":\"BUS\"},{\"name\":\"bus\"}]"); break;
        }
        Assert.NotEmpty(Assert.Throws<ProjectFormatException>(() => serializer.Deserialize(json.ToJsonString())).Message);
    }

    [Fact]
    public void Older_project_without_optional_metadata_uses_documented_defaults()
    {
        var project = new TestProject(Kit.Test(Kit.Group("loop"))); project.Definition.Curves.Add(CurveGenerator.Generate(new GeneratorSettings { ChannelIds = ["A"], Duration = .01, SampleRate = 1000 }, "curve", "Curve", "1", "tester", DateTimeOffset.UnixEpoch));
        var serializer = new ProjectSerializer(); var json = JsonNode.Parse(serializer.Serialize(project))!.AsObject(); json["name"] = null; json["version"] = null;
        var curve = json["curves"]![0]!.AsObject(); foreach (var field in new[] { "name", "source", "history", "createdBy", "sha256" }) curve[field] = null; curve["channels"]![0]!["unit"] = null;
        var loaded = serializer.Deserialize(json.ToJsonString()); Assert.Equal("Unbenannt", loaded.Definition.Name); Assert.Equal("0.1", loaded.Definition.Version);
        var restored = Assert.Single(loaded.Definition.Curves.Versions); Assert.Equal("curve", restored.Name); Assert.Equal("", restored.SourceText); Assert.Equal("", restored.CreatedBy); Assert.Empty(restored.History); Assert.Equal("", restored.Channels[0].Unit);
    }
}
