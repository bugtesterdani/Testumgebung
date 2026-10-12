using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class LibraryEditorBoundaryTests
{
    [Theory]
    [InlineData("C#")]
    [InlineData("Python")]
    [InlineData("C++")]
    [InlineData("LabVIEW")]
    public async Task Language_switch_updates_templates_and_preserves_custom_source(string target)
    {
        foreach (var source in new[] { "", "template", "custom source" })
        {
            var shell = new ShellViewModel(new QueueDispatcher(), project: SampleProjects.AcPruefung());
            var block = shell.Library.Add(target == "C#" ? CodeLanguage.Python : CodeLanguage.CSharp);
            var oldLanguage = block.Language; var oldSource = source == "template" ? block.Source : source;
            block.Source = oldSource; block.Language = oldLanguage; Assert.Equal(oldSource, block.Source);
            block.Language = target; Assert.Equal(target, block.Language);
            if (source == "custom source" && target != "LabVIEW") Assert.Equal(oldSource, block.Source);
            else Assert.NotEqual(oldSource, block.Source);
            Assert.Equal(target == "LabVIEW", block.IsLabView); Assert.Equal(block.IsLabView, block.OpenInLabViewCommand.CanExecute(null));
            Assert.NotEmpty(block.ApiHelp); Assert.Contains(target, block.Languages);
            var name = block.Name; block.Name = " "; Assert.Equal(name, block.Name); block.Name = "Changed";
            block.Version = "2"; block.Description = "description"; Assert.Equal("description", block.Description); Assert.Contains("Changed", block.DisplayName);
            block.AddPortCommand.Execute(null); block.AddPortCommand.Execute(null); Assert.Contains(block.Ports, p => p.Name == "Eingang2");
            block.RemovePortCommand.Execute(new object()); var row = block.Ports[^1]; block.RemovePortCommand.Execute(row); Assert.DoesNotContain(row, block.Ports);
            await block.BuildAsync(); Assert.False(block.BuildOk); Assert.Contains("Keine Ausführungsumgebung", block.BuildStatus);
            await block.TestAsync(); Assert.Contains("Keine Ausführungsumgebung", block.TestResult);
            Assert.False(block.IsBusy); Assert.Empty(block.UsedIn);
            if (block.IsLabView) { block.Source = "vi = "; block.OpenInLabViewCommand.Execute(null); Assert.Contains("Kein VI", block.BuildStatus); block.Source = "vi = C:/missing-" + Guid.NewGuid() + ".vi"; block.OpenInLabViewCommand.Execute(null); Assert.Contains("nicht gefunden", block.BuildStatus); }
        }
    }

    [Fact]
    public void Graphic_library_ports_body_and_selection_remain_synchronized()
    {
        var project = SampleProjects.AcPruefung();
        var body = new GroupDefinition("body", "Body") { ExitGroup = new GroupDefinition("exit", "Exit") };
        body.Add(new GroupDefinition("nested", "Nested"), BlockCatalog.Default.Create(BlockCatalog.SetRating, "rate", "Rate", new Dictionary<string, string> { ["Bewertung"] = "PASS" }));
        var definition = new GroupBlockDefinition("graphic", "Graphic", body); project.Definition.GroupLibrary.Add(definition);
        var shell = new ShellViewModel(new QueueDispatcher(), project: project); var library = shell.Library; var group = Assert.Single(library.GroupBlocks);
        Assert.Equal("Grafisch", group.Language); Assert.Equal("", group.Description); Assert.Equal(4, group.Structure.Count);
        group.Name = ""; Assert.Equal("Graphic", group.Name); group.Name = "Changed"; group.Version = "2"; group.Description = "description";
        Assert.Contains("Changed", group.DisplayName); Assert.Equal("description", group.Description); Assert.Empty(group.UsedIn);
        group.AddPortCommand.Execute(null); group.AddPortCommand.Execute(null); Assert.Equal(2, definition.Ports.Count);
        group.Ports[1].Name = group.Ports[0].Name; Assert.Equal(2, definition.Ports.Count);
        group.Ports[1].Name = "valid"; group.Ports[0].Name = ""; Assert.Equal(2, definition.Ports.Count);
        group.Ports[0].Name = "restored"; group.RemovePortCommand.Execute(new object()); group.RemovePortCommand.Execute(group.Ports[1]); Assert.Single(definition.Ports);
        library.SelectedItem = group; Assert.Same(group, library.SelectedItem); Assert.Null(library.Selected);
        group.OpenInEditorCommand.Execute(null); Assert.Equal(ShellPage.Operator, shell.Page); Assert.Equal("body", shell.Editor.SelectedNode!.Id);
        var code = library.Add(CodeLanguage.CSharp); Assert.Same(code, library.SelectedItem); Assert.Null(library.SelectedGroup);
        library.SelectedItem = new object(); Assert.Same(code, library.SelectedItem);
        library.SelectedItem = group; Assert.Null(library.Selected); library.SelectedGroup = null;
        library.RemoveAssemblyCommand.Execute(null); Assert.Empty(library.Assemblies);
    }
}
