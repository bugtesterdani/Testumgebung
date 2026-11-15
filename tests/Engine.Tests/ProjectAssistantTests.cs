using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class ProjectAssistantTests
{
    private static string Docs(string relative)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        return Path.Combine(root.FullName, "docs", relative);
    }

    [Fact]
    public void Bundled_templates_create_unsaved_named_copies_without_touching_the_sources()
    {
        var templates = ProjectTemplates.InFolder(Docs("beispiele"));
        Assert.Null(templates[0].Path);
        Assert.True(templates.Count >= 4);
        foreach (var template in templates.Skip(1))
        {
            var path = Assert.IsType<string>(template.Path);
            var before = File.ReadAllText(path);
            var dialogs = new TestDialogs { Request = new("Mein Prüfprogramm", path) };
            var editor = new EditorViewModel(SampleProjects.AcPruefung(), dialogs: dialogs);
            editor.NewCommand.Execute(null);

            Assert.Equal("Mein Prüfprogramm", editor.Project.Definition.Name);
            Assert.Equal("0.1", editor.Project.Definition.Version);
            Assert.Null(editor.FilePath);
            Assert.True(editor.IsDirty);
            Assert.Equal(before, File.ReadAllText(path));
        }
    }

    [Fact]
    public void Wizard_cancellation_and_unsaved_protection_leave_the_editor_unchanged()
    {
        var original = SampleProjects.AcPruefung();
        var dialogs = new TestDialogs();
        var editor = new EditorViewModel(original, dialogs: dialogs);
        editor.MarkChanged();
        editor.NewCommand.Execute(null);
        Assert.Same(original, editor.Project);
        Assert.Equal(0, dialogs.UnsavedPrompts);

        dialogs.Request = new("Leer");
        editor.NewCommand.Execute(null);
        Assert.Same(original, editor.Project);
        Assert.Equal(1, dialogs.UnsavedPrompts);

        dialogs.Decision = UnsavedProjectDecision.Discard;
        editor.NewCommand.Execute(null);
        Assert.Equal("Leer", editor.Project.Definition.Name);
        Assert.Empty(editor.Project.Definition.Loop.Children);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void All_pages_and_catalog_blocks_resolve_to_installed_handbook_sections()
    {
        var folder = Docs("handbuch");
        foreach (var page in Enum.GetValues<ShellPage>())
        {
            var help = ContextHelp.Load(folder, page, null);
            Assert.False(string.IsNullOrWhiteSpace(help.Section), $"Missing help for {page}: {help.Topic}");
        }
        foreach (var block in BlockCatalog.Default.All)
        {
            var help = ContextHelp.Load(folder, ShellPage.Editor, block);
            Assert.Same(block, help.Block);
            Assert.False(string.IsNullOrWhiteSpace(help.Section), $"Missing help for {block.Type}: {help.Topic}");
        }
    }

    [Fact]
    public void Handbook_excerpt_keeps_subsections_and_ignores_headings_inside_code()
    {
        string[] lines = ["# Handbuch", "## Auswahl", "Anleitung", "```text", "## Code", "```",
            "### Parameter", "Werte", "## Nächstes Kapitel", "Anderer Inhalt"];
        var section = ContextHelp.ReadSection(lines, "Auswahl");
        Assert.Contains("## Code", section);
        Assert.Contains("### Parameter", section);
        Assert.DoesNotContain("Anderer Inhalt", section);
        Assert.Empty(ContextHelp.ReadSection(lines, "Unbekannt"));
    }

    private sealed class TestDialogs : IFileDialogService
    {
        public NewProjectRequest? Request { get; set; }
        public UnsavedProjectDecision Decision { get; set; } = UnsavedProjectDecision.Cancel;
        public int UnsavedPrompts { get; private set; }
        public NewProjectRequest? ChooseNewProject() => Request;
        public UnsavedProjectDecision AskSaveProjectChanges() { UnsavedPrompts++; return Decision; }
        public string? OpenProject() => null;
        public string? SaveProject(string suggestedName) => null;
        public void ShowError(string message) => throw new InvalidOperationException(message);
        public void OpenExternal(string path) { }
    }
}
