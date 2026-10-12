using System.IO.Compression;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public sealed class ManagedAssemblyTests
{
    [Fact]
    public async Task Bibliotheksport_Auftrag_wird_aktuell_uebergeben_und_ungueltiger_Vertrag_blockiert()
    {
        var project = SampleProjects.AcPruefung();
        var editor = new EditorViewModel(project) { CodeRuntime = CodeBlockTests.SharedRuntime.Value };
        var library = new LibraryViewModel(editor);
        library.AddCSharpCommand.Execute(null);
        var block = library.Selected!;
        foreach (var existing in block.Ports.ToArray()) block.RemovePortCommand.Execute(existing);
        block.AddPortCommand.Execute(null);
        var port = Assert.Single(block.Ports);
        port.Name = "Auftrag";
        port.Type = VarType.String;
        port.TestValue = "A123";
        block.Source = "return Pass(Auftrag);";
        await block.BuildAsync().Within();
        Assert.True(block.BuildOk);
        await block.TestAsync().Within();
        Assert.Contains("A123", block.TestResult);

        port.Name = "Auftrag ungueltig";
        await block.BuildAsync().Within();
        Assert.False(block.BuildOk);
        Assert.Contains("Schnittstelle ungültig", block.BuildStatus);
        await block.TestAsync().Within();
        Assert.Contains("Schnittstelle ungültig", block.TestResult);

        port.Name = "Auftrag";
        await block.BuildAsync().Within();
        Assert.True(block.BuildOk);
    }

    private static List<ManagedAssembly> Assemblies() =>
    [
        ManagedAssemblyImport.Read(typeof(OrderLibrary.OrderEvaluation).Assembly.Location),
        ManagedAssemblyImport.Read(typeof(OrderHelper.Rules).Assembly.Location),
    ];

    private static StepDefinition Code(string source) => BlockCatalog.Default.Create(BlockCatalog.Code, "code", "DLL-Aufruf",
        new Dictionary<string, string> { ["Sprache"] = "C#", ["Quelltext"] = source, ["Timeout"] = "30" });

    [Fact]
    public async Task DLL_mit_Abhaengigkeit_waehlt_A123_und_wertet_Testmessung_aus()
    {
        var selection = new TestProject(Test(Group("loop"), Group("init").Add(Code(
            "SelectTest(OrderLibrary.OrderEvaluation.Select(Auftrag), null, Auftrag, null); return Pass();"))));
        selection.Definition.Root.Var("Auftrag", VarType.String);
        selection.Definition.Assemblies.AddRange(Assemblies());
        var cloned = new ProjectSerializer().Clone(selection);
        var (value, error) = await TestSelector.RunCustomAsync(cloned, "A123", "A123", new TestRunOptions { CodeRuntime = CodeBlockTests.SharedRuntime.Value }).Within();
        Assert.Null(error);
        Assert.Equal("Programm A", value!.Test);
        Assert.Equal("A123", value.Order);

        var definition = Test(Group("loop").Add(Code("return Measured(OrderLibrary.OrderEvaluation.Measure(115), 220, 240, \"V\");")));
        definition.Assemblies.AddRange(Assemblies());
        var run = new TestRun(definition, new TestRunOptions { CodeRuntime = CodeBlockTests.SharedRuntime.Value });
        await run.RunAsync().Within();
        Assert.Equal(230.0, Assert.Single(run.Steps("code")).Value);
        Assert.Equal(ResultRating.Pass, Assert.Single(run.Steps("code")).Rating);

        definition.Library.Add(new CodeBlockDefinition("dll-block", "DLL-Prüfung")
        {
            Source = "return Measured(OrderLibrary.OrderEvaluation.Measure(115), 220, 240, \"V\");",
        });
        definition.Loop.Children.Clear();
        definition.Loop.Add(BlockCatalog.Default.Create(BlockCatalog.LibraryBlock, "library-call", "DLL-Baustein",
            new Dictionary<string, string> { ["Baustein"] = "dll-block" }));
        var libraryRun = new TestRun(definition, new TestRunOptions { CodeRuntime = CodeBlockTests.SharedRuntime.Value });
        await libraryRun.RunAsync().Within();
        Assert.Equal(230.0, Assert.Single(libraryRun.Steps("library-call")).Value);
        var editor = new EditorViewModel(new TestProject(definition)) { CodeRuntime = CodeBlockTests.SharedRuntime.Value };
        var library = new LibraryViewModel(editor);
        await library.Selected!.BuildAsync().Within();
        Assert.True(library.Selected.BuildOk);
        await library.Selected.TestAsync().Within();
        Assert.Contains("230", library.Selected.TestResult);
    }

    [Fact]
    public async Task Fehlende_Abhaengigkeit_wird_nicht_aus_vorherigem_Dll_Cache_uebernommen()
    {
        async Task<TestRun> Run(IReadOnlyList<ManagedAssembly> assemblies)
        {
            var definition = Test(Group("loop").Add(Code("return Pass(OrderLibrary.OrderEvaluation.Select(\"A123\"));")));
            definition.Assemblies.AddRange(assemblies);
            var run = new TestRun(definition, new TestRunOptions { CodeRuntime = CodeBlockTests.SharedRuntime.Value });
            await run.RunAsync().Within();
            return run;
        }
        Assert.Equal(ResultRating.Pass, Assert.Single((await Run(Assemblies())).Steps("code")).Rating);
        Assert.Equal(ResultRating.Error, Assert.Single((await Run(Assemblies().Take(1).ToList())).Steps("code")).Rating);
    }

    [Fact]
    public void Import_Projekt_Freigabe_und_Hash_binden_Dll_Inhalte()
    {
        var project = SampleProjects.AcPruefung();
        var editor = new EditorViewModel(project);
        var library = new LibraryViewModel(editor);
        foreach (var path in new[] { typeof(OrderLibrary.OrderEvaluation).Assembly.Location, typeof(OrderHelper.Rules).Assembly.Location }) library.ImportAssembly(path);
        Assert.True(editor.IsDirty);
        Assert.Equal(2, library.Assemblies.Count);
        library.ImportAssembly(typeof(OrderLibrary.OrderEvaluation).Assembly.Location);
        Assert.Equal(2, project.Definition.Assemblies.Count);
        var clone = new ProjectSerializer().Clone(project);
        var (bytes, manifest) = ReleasePackage.Build(clone, "test", DateTimeOffset.Now);
        Assert.Equal(2, manifest.Entries.Count(e => e.Name.StartsWith("DLL/")));
        var opened = ReleasePackage.Open(bytes);
        Assert.True(opened.IsIntact);
        Assert.Equal(project.Definition.Assemblies[0].Data, opened.Project.Definition.Assemblies.Single(a => a.Name == project.Definition.Assemblies[0].Name).Data);
        // Replace a valid DLL with other valid contents and matching file name, leaving the manifest unchanged.
        using var zipBytes = new MemoryStream();
        zipBytes.Write(bytes);
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Update, true))
        {
            var entry = zip.GetEntry("projekt.tproj.json")!;
            string json;
            using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
            var replacement = new ProjectSerializer().Deserialize(json);
            replacement.Definition.Assemblies.RemoveAt(0);
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("projekt.tproj.json").Open(), Encoding.UTF8);
            writer.Write(new ProjectSerializer().Serialize(replacement));
        }
        Assert.False(ReleasePackage.Open(zipBytes.ToArray()).IsIntact);
        clone.Definition.Assemblies.RemoveAt(0);
        Assert.False(DefinitionCompatibility.CanSwapAtDut(project, clone, out _));
        Assert.Throws<ArgumentException>(() => ManagedAssemblyImport.Validate(new("native.dll", [1, 2, 3])));
    }
}

