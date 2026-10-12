using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

/// <summary>Etappe 4 in der Oberfläche: CodeBlock-Editor, Umwandeln, Bibliothek, Bindungen.</summary>
#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public class CodeEditorTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(60);

    private static (ShellViewModel Shell, QueueDispatcher Ui) CreateShell()
    {
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: SampleProjects.AcPruefung(), clock: new VirtualClock(),
            codeRuntime: CodeBlockTests.SharedRuntime.Value);
        shell.Login();
        return (shell, ui);
    }

    private static NodeViewModel InsertCode(EditorViewModel e, string afterId, string language, string source)
    {
        e.SelectedNode = e.FindNode(afterId);
        var node = e.Insert(e.Palette.SelectMany(c => c.Items).First(i => i.BlockType == BlockCatalog.Code));
        var props = (StepPropertiesViewModel)e.Properties!;
        props.Parameters.Single(p => p.Descriptor.Name == "Sprache").Value = language;
        props.Parameters.Single(p => p.Descriptor.Name == "Quelltext").Value = source;
        return node;
    }

    [Theory]
    [InlineData("C#", "return Measured(Spannung, GrenzwertMin, GrenzwertMax, \"V\");", "return Measured(Spannung, GrenzwertMin, Grenze, \"V\");")]
    [InlineData("Python", "return Measured(Spannung, GrenzwertMin, GrenzwertMax, \"V\")", "return Measured(Spannung, GrenzwertMin, Grenze, \"V\")")]
    public async Task CodeBlock_Build_pruefen_mit_sichtbaren_Variablen(string language, string good, string bad)
    {
        var (shell, _) = CreateShell();
        var e = shell.Editor;
        InsertCode(e, "spannung", language, good);
        var props = (StepPropertiesViewModel)e.Properties!;
        var tools = props.CodeTools!;
        Assert.Contains("Spannung : double", tools.VisibleVariables);
        Assert.Contains("Nennspannung : double", tools.VisibleVariables);  // Hauptablauf
        Assert.True(props.Parameters.Single(p => p.Descriptor.Name == "Quelltext").IsCode);

        await tools.BuildAsync().WaitAsync(Slow);
        Assert.True(tools.BuildOk == true, tools.BuildStatus);
        Assert.StartsWith("Build erfolgreich", tools.BuildStatus);

        props.Parameters.Single(p => p.Descriptor.Name == "Quelltext").Value = bad;
        await tools.BuildAsync().WaitAsync(Slow);
        if (language == "C#")
        {
            Assert.False(tools.BuildOk == true);
            Assert.Equal(1, Assert.Single(tools.Diagnostics, d => d.IsError).Line);
        }
        else
        {
            Assert.True(tools.BuildOk == true); // Python prüft Namen erst zur Laufzeit
        }
    }

    [Fact]
    public async Task CodeBlock_laeuft_im_Pruefplatz_und_wird_in_Baustein_umgewandelt()
    {
        var (shell, ui) = CreateShell();
        var e = shell.Editor;
        var node = InsertCode(e, "spannung", "C#", "Spannung = Spannung * 1.0;\nreturn Measured(Spannung, GrenzwertMin, GrenzwertMax, \"V\");");
        node.Name = "Spannungsbewertung";
        var tools = ((StepPropertiesViewModel)e.Properties!).CodeTools!;

        tools.ConvertCommand.Execute(null);
        Assert.Contains("umgewandelt", e.Notice);
        var converted = e.FindNode(node.Id)!;
        Assert.Equal(NodeIcon.LibraryBlock, converted.Icon);
        var block = Assert.Single(e.Library);
        Assert.Equal("spannungsbewertung", block.Id);
        Assert.Equal(["Spannung:InOut", "GrenzwertMin:Input", "GrenzwertMax:Input"], block.Ports.Select(p => $"{p.Name}:{p.Direction}"));
        Assert.Empty(e.Diagnostics);

        var props = (StepPropertiesViewModel)e.Properties!;
        var bindings = props.Bindings!;
        Assert.Equal(block.Id, bindings.SelectedBlock!.Id);
        Assert.Equal(["Spannung", "GrenzwertMin", "GrenzwertMax"], bindings.Rows.Select(r => r.Value));
        Assert.DoesNotContain(props.Parameters, p => p.IsText && p.Descriptor.Name == "Baustein");

        // Bindung ändern → Diagnose bei leerem Eingang
        bindings.Rows.Single(r => r.Name == "GrenzwertMax").Value = "";
        Assert.Contains(props.Diagnostics, d => d.Contains("GrenzwertMax"));
        bindings.Rows.Single(r => r.Name == "GrenzwertMax").Value = "Nennspannung * 1.05";
        Assert.False(props.HasDiagnostics);

        // Ablauf mit dem Baustein ausführen
        var s = shell.SelectedStation;
        _ = s.StartAsync();
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Kennung erfassen" }, "Kennung");
        s.ActiveContent!.Input = "SN-CB";
        s.ActiveContent.Confirm();
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Sichtprüfung" }, "Sicht");
        s.ActiveContent!.ChooseCommand.Execute("i. O.");
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Gehäusetemperatur erfassen" }, "Temp");
        s.ActiveContent!.Confirm();
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Prüfling anschließen" }, "Anschluss");
        s.ActiveContent!.Confirm();
        await ui.Until(() => s.Protocol.Any(p => p.Text == "Spannungsbewertung"), "Baustein ausgeführt");
        var row = s.Protocol.First(p => p.Text == "Spannungsbewertung");
        Assert.Equal(ResultRating.Pass, row.Rating);
        Assert.Equal("V", row.Unit);
        s.RequestAbort();
        await ui.Until(() => !s.IsRunning && s.LastStatus is not null, "Ende");
    }

    [Theory]
    [InlineData(CodeLanguage.CSharp)]
    [InlineData(CodeLanguage.Python)]
    public async Task Bibliothek_neuer_Baustein_Build_und_Testausfuehrung(CodeLanguage language)
    {
        var (shell, _) = CreateShell();
        var lib = shell.Library;
        var block = lib.Add(language);
        Assert.Same(block, lib.Selected);
        Assert.Equal(4, block.Ports.Count);

        await block.BuildAsync().WaitAsync(Slow);
        Assert.True(block.BuildOk == true, block.BuildStatus);

        await block.TestAsync().WaitAsync(Slow);
        Assert.StartsWith("PASS", block.TestResult);
        Assert.Contains("IstWertOK = true", block.TestResult);
        Assert.StartsWith("CB-", block.InvocationId);

        block.Ports.Single(p => p.Name == "Spannung").TestValue = "250";
        await block.TestAsync().WaitAsync(Slow);
        Assert.StartsWith("FAIL", block.TestResult);

        block.Ports.Single(p => p.Name == "Spannung").TestValue = "abc";
        Assert.Equal("Testwert: Wert muss Zahl sein", block.Ports.Single(p => p.Name == "Spannung").Error);

        block.Source = language == CodeLanguage.CSharp ? "return Pass(Var<double>(\"Nennspannung\"));" : "return Pass(var(\"Nennspannung\"))";
        block.Ports.Single(p => p.Name == "Spannung").TestValue = "230";
        await block.TestAsync().WaitAsync(Slow);
        Assert.StartsWith("ERROR", block.TestResult);
        Assert.Contains("Bausteingrenze", block.TestResult);
    }

    [Fact]
    public void Verwendeter_Baustein_kann_nicht_geloescht_werden_und_wird_gespeichert()
    {
        var (shell, _) = CreateShell();
        var e = shell.Editor;
        var lib = shell.Library;
        var block = lib.Add(CodeLanguage.Python);
        e.SelectedNode = e.FindNode("spannung");
        e.Insert(e.Palette.SelectMany(c => c.Items).First(i => i.BlockType == BlockCatalog.LibraryBlock));
        var bindings = ((StepPropertiesViewModel)e.Properties!).Bindings!;
        Assert.Contains(((StepPropertiesViewModel)e.Properties!).Diagnostics, d => d.Contains("Kein Baustein"));
        bindings.SelectedBlock = bindings.Blocks.Single(b => b.Id == block.Id);
        Assert.Equal(4, bindings.Rows.Count);
        Assert.Contains(((StepPropertiesViewModel)e.Properties!).Diagnostics, d => d.Contains("nicht verbunden"));

        Assert.Single(block.UsedIn);
        lib.DeleteCommand.Execute(null);
        Assert.Contains("wird noch verwendet", lib.Message);
        Assert.Single(e.Library);

        bindings.Rows.Single(r => r.Name == "Spannung").Value = "Spannung";
        var json = new ProjectSerializer().Serialize(e.Project);
        Assert.Contains("\"library\"", json);
        Assert.Contains("\"in.Spannung\"", json);
    }
}

