using System.Text.Json.Nodes;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class MalformedProjectTests
{
    [Theory]
    [InlineData("children")]
    [InlineData("variables")]
    [InlineData("simulation")]
    [InlineData("curves")]
    [InlineData("deviceProfiles")]
    [InlineData("duplicate")]
    [InlineData("timeout")]
    [InlineData("syntax")]
    public void Opening_invalid_project_shows_error_and_preserves_the_current_project(string fault)
    {
        var serializer = new ProjectSerializer();
        var original = new TestProject(Test(Group("loop")));
        var json = JsonNode.Parse(serializer.Serialize(original))!;
        if (fault is "children" or "variables") json["loop"]![fault] = new JsonArray((JsonNode?)null);
        else if (fault == "duplicate") json["loop"]!["id"] = json["init"]!["id"]!.GetValue<string>();
        else if (fault == "timeout") json["loop"]!["resourceTimeoutMs"] = double.MaxValue;
        else if (fault != "syntax") json[fault] = new JsonArray((JsonNode?)null);
        var content = fault == "syntax" ? "{ broken" : json.ToJsonString();
        Assert.Throws<ProjectFormatException>(() => serializer.Deserialize(content));

        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, content);
            var dialogs = new Dialogs(file);
            var editor = new EditorViewModel(original, dialogs: dialogs);
            editor.MarkChanged();
            editor.OpenCommand.Execute(null);
            Assert.Same(original, editor.Project);
            Assert.True(editor.IsDirty);
            Assert.Null(editor.FilePath);
            Assert.Contains("Projekt konnte nicht geöffnet werden", Assert.Single(dialogs.Errors));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Null_fields_in_existing_project_are_rejected_or_remain_editable()
    {
        var serializer = new ProjectSerializer();
        var source = JsonNode.Parse(serializer.Serialize(SampleProjects.AcPruefung()))!;
        foreach (var path in PropertyPaths(source, []))
        {
            var changed = source.DeepClone();
            var parent = changed;
            foreach (var part in path[..^1])
                parent = parent is JsonArray array ? array[int.Parse(part)]! : parent![part]!;
            if (parent is JsonArray entries) entries[int.Parse(path[^1])] = null;
            else parent![path[^1]] = null;
            try
            {
                var project = serializer.Deserialize(changed.ToJsonString());
                var editor = new EditorViewModel(project);
                editor.RebuildTree();
            }
            catch (ProjectFormatException) { }
        }
    }

    private static IEnumerable<string[]> PropertyPaths(JsonNode? node, string[] prefix)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                string[] path = [.. prefix, property.Key];
                yield return path;
                foreach (var nested in PropertyPaths(property.Value, path)) yield return nested;
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                string[] path = [.. prefix, i.ToString(System.Globalization.CultureInfo.InvariantCulture)];
                yield return path;
                foreach (var nested in PropertyPaths(array[i], path)) yield return nested;
            }
        }
    }

    private sealed class Dialogs(string path) : IFileDialogService
    {
        public List<string> Errors { get; } = [];
        public string? OpenProject() => path;
        public string? SaveProject(string suggestedName) => null;
        public void ShowError(string message) => Errors.Add(message);
        public void OpenExternal(string path) { }
    }
}
