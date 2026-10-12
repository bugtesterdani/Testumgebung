using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Workers;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>Etappe 4: CodeBlocks in isolierten C#- und Python-Workern (gleicher Vertrag für beide Sprachen).</summary>
#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public class CodeBlockTests
{
    internal static readonly Lazy<CodeRuntime> SharedRuntime = new(() => new CodeRuntime(new CodeRuntimeOptions
    {
        WorkerDirectory = Path.Combine(Path.GetDirectoryName(typeof(CodeBlockTests).Assembly.Location)!, "workers"),
        PythonExecutable = Environment.GetEnvironmentVariable("TESTUMGEBUNG_PYTHON") ?? CodeRuntimeOptions.DefaultPython,
    }));

    private static CodeRuntime Runtime => SharedRuntime.Value;
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(60);

    private static StepDefinition Code(string id, string language, string source, string timeout = "5")
        => BlockCatalog.Default.Create(BlockCatalog.Code, id, id, new Dictionary<string, string>
        {
            ["Sprache"] = language,
            ["Quelltext"] = source,
            ["Timeout"] = timeout,
        });

    private static TestRun Run(TestDefinition def, HardwareRegistry? hw = null, IOperatorInterface? op = null)
        => new(def, new TestRunOptions { Clock = new VirtualClock(), CodeRuntime = Runtime, Hardware = hw, Operator = op });

    private static string Src(string language, string csharp, string python) => language == "C#" ? csharp : python;

    [Fact]
    public async Task CSharp_Compilerdiagnosen_beziehen_sich_auf_Zeilen_des_Nutzercodes()
    {
        var vars = new List<CodePort> { new("Spannung", VarType.Double, PortDirection.InOut) };
        var bad = await Runtime.CompileAsync(new CodeRequest(CodeLanguage.CSharp, "var x = Spannung * 2;\nreturn y;", vars), CancellationToken.None).WaitAsync(Slow);
        Assert.False(bad.Success);
        var error = Assert.Single(bad.Diagnostics, d => d.IsError);
        Assert.Equal(2, error.Line);
        Assert.Equal("CS0103", error.Code);
        Assert.StartsWith("Isolierter .NET-Worker", bad.RuntimeInfo);

        var ok = await Runtime.CompileAsync(new CodeRequest(CodeLanguage.CSharp, "return Measured(Spannung, 220, 240, \"V\");", vars), CancellationToken.None).WaitAsync(Slow);
        Assert.True(ok.Success);
        Assert.Equal(0, ok.Errors);
    }

    [Fact]
    public async Task Python_Syntaxfehler_mit_Zeilenangabe()
    {
        // Token-Syntaxfehler (nicht eine offene Klammer – deren gemeldete Zeile hängt von der Python-Version ab).
        var bad = await Runtime.CompileAsync(new CodeRequest(CodeLanguage.Python, "x = 1\ny = 1 2", []), CancellationToken.None).WaitAsync(Slow);
        Assert.False(bad.Success);
        Assert.Equal(2, Assert.Single(bad.Diagnostics).Line);
        Assert.StartsWith("Python 3", bad.RuntimeInfo);
        Assert.True((await Runtime.CompileAsync(new CodeRequest(CodeLanguage.Python, "return Pass()", []), CancellationToken.None).WaitAsync(Slow)).Success);
    }

    [Theory]
    [InlineData("C#")]
    [InlineData("Python")]
    public async Task CodeBlock_im_Ablauf_liest_und_schreibt_Ordnervariablen(string language)
    {
        var source = Src(language,
            "Spannung = Spannung + 1;\nZaehler++;\nSetVar(\"../Spannung\", 1.5);\nreturn Measured(Spannung, GrenzwertMin, GrenzwertMax, \"V\");",
            "Spannung = Spannung + 1\nZaehler += 1\nset_var(\"../Spannung\", 1.5)\nreturn Measured(Spannung, GrenzwertMin, GrenzwertMax, \"V\")");
        var dut = Group("dut", "Prüfling")
            .Var("Spannung", VarType.Double, 238.5).Var("GrenzwertMin", VarType.Double, 220.0).Var("GrenzwertMax", VarType.Double, 240.0)
            .Add(Code("code", language, source), Code("code2", language, Src(language, "return Spannung == 239.5 ? Pass() : Fail();", "return Pass() if Spannung == 239.5 else Fail()")));
        var loop = Group("loop").Var("Zaehler", VarType.Int, 41).Var("Spannung", VarType.Double, 0.0).Add(dut,
            Steps.Code("check", "Prüfen", ctx => StepOutcome.Unrated($"{ctx.Get("Zaehler")}|{ctx.Get("Spannung")}")));

        var run = Run(Test(loop));
        var status = await run.RunAsync().WaitAsync(Slow);

        var first = run.Steps("code").Single();
        Assert.Equal(ResultRating.Pass, first.Rating);
        Assert.Equal(239.5, first.Value);
        Assert.Equal("V", first.Unit);
        Assert.Equal(ResultRating.Pass, run.Steps("code2").Single().Rating); // geänderter Wert wurde zurückgeschrieben
        Assert.Equal("42|1.5", ((string)run.Steps("check").Single().Value!).Replace(',', '.'));
        Assert.Equal(ResultRating.Pass, status.Rating);
    }

    [Theory]
    [InlineData("C#")]
    [InlineData("Python")]
    public async Task Hardware_ueber_zentrale_Vermittlung_Locks_gelten_sprachuebergreifend(string language)
    {
        var sim = new SimulationAdapter();
        sim.SetInput("ai0", 230.25);
        var hw = new HardwareRegistry().Bind("U", sim, "ai0").Bind("AC_Quelle", sim, "ao0", requiresLock: true);
        var measure = Code("measure", language, Src(language,
            "var u = Read(\"U\");\nLog($\"U={u}\");\nreturn Measured(u, 220, 240, \"V\");",
            "u = read(\"U\")\nlog(f\"U={u}\")\nreturn Measured(u, 220, 240, \"V\")"));
        var unreserved = Code("unreserved", language, Src(language, "Write(\"AC_Quelle\", 230);", "write(\"AC_Quelle\", 230)"));
        var locked = new GroupDefinition("locked", "Mit Sperre").Add(Code("reserved", language, Src(language, "Write(\"AC_Quelle\", 115);", "write(\"AC_Quelle\", 115)")));
        locked.Resources.Add("AC_Quelle");

        var run = Run(Test(Group("loop").Add(measure, unreserved, locked)), hw);
        await run.RunAsync().WaitAsync(Slow);

        var m = run.Steps("measure").Single();
        Assert.Equal(ResultRating.Pass, m.Rating);
        Assert.Equal(230.25, m.Value);
        Assert.Contains(run.Events.OfType<Diagnostic>(), d => d.Message.StartsWith("U=230"));
        var u = run.Steps("unreserved").Single();
        Assert.Equal(ResultRating.Error, u.Rating);
        Assert.Contains("ohne Reservierung", u.Message);
        Assert.Equal(ResultRating.NResult, run.Steps("reserved").Single().Rating);
        Assert.Equal(115.0, sim.GetOutput("ao0"));
    }

    [Theory]
    [InlineData("C#", "int a = 0;\nint b = 1 / a;", "DivideByZeroException")]
    [InlineData("Python", "a = 0\nb = 1 / a", "ZeroDivisionError")]
    public async Task Ausnahmen_im_Code_ergeben_ERROR_mit_Zeile(string language, string source, string exception)
    {
        var run = Run(Test(Group("loop").Add(Code("boom", language, source), Steps.Rate("next", ResultRating.Pass))));
        var status = await run.RunAsync().WaitAsync(Slow);
        var step = run.Steps("boom").Single();
        Assert.Equal(ResultRating.Error, step.Rating);
        Assert.Contains(exception, step.Message);
        Assert.Contains("(Zeile 2)", step.Message);
        Assert.Single(run.Steps("next"));               // ERROR stoppt den Ablauf nicht
        Assert.Equal(ResultRating.Error, status.Rating);
    }

    [Theory]
    [InlineData("C#", "while (true) { Sleep(50); }", "0.5")]
    [InlineData("Python", "while True:\n    sleep(0.05)", "0.5")]
    [InlineData("C#", "while (true) { }", "0.5")]
    [InlineData("Python", "while True:\n    pass", "0.5")]
    public async Task Werkerabbruch_bricht_Code_kooperativ_oder_per_Prozessabbruch_ab(string language, string source, string timeout)
    {
        var probe = new Probe();
        var group = Group("g").Add(Code("loop.code", language, source, timeout), probe.Mark("after"));
        group.ExitGroup = Group("g.exit").Add(probe.Mark("exit"));
        var loop = new GroupDefinition("loop", "Loop") { Repetition = Repetition.Endless }.Add(group);
        var run = Run(Test(loop));
        var started = run.WaitFor<StepStarted>(s => s.NodeId == "loop.code");
        var task = run.RunAsync();
        await started.WaitAsync(Slow);
        await Task.Delay(1500); // Worker gestartet, Code läuft
        var sw = System.Diagnostics.Stopwatch.StartNew();
        run.RequestAbort();
        var status = await task.WaitAsync(Slow);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"Abbruch dauerte {sw.Elapsed}");
        Assert.True(run.Steps("loop.code").Single().Cancelled);
        Assert.Equal(["exit"], probe.Marks);
        Assert.True(status.OperatorAbort);
    }

    [Theory]
    [InlineData("C#")]
    [InlineData("Python")]
    public async Task Bibliotheksbaustein_mit_Eingangs_und_Ausgangsbindungen(string language)
    {
        var block = new CodeBlockDefinition("spannungsbewertung", "Spannungsbewertung") { Language = BlockCatalog.ParseLanguage(language), Version = "0.3" };
        block.Ports.AddRange(
        [
            new CodePort("Spannung", VarType.Double, PortDirection.Input, "V"),
            new CodePort("Minimum", VarType.Double, PortDirection.Input, "V"),
            new CodePort("Maximum", VarType.Double, PortDirection.Input, "V"),
            new CodePort("IstWertOK", VarType.Bool, PortDirection.Output),
        ]);
        block.Source = Src(language,
            "IstWertOK = Spannung >= Minimum && Spannung <= Maximum;\nreturn IstWertOK ? Pass(Spannung) : Fail(\"außerhalb\", Spannung);",
            "IstWertOK = Minimum <= Spannung <= Maximum\nreturn Pass(Spannung) if IstWertOK else Fail(\"außerhalb\", Spannung)");

        var step = BlockCatalog.Default.Create(BlockCatalog.LibraryBlock, "use", "Spannung bewerten", new Dictionary<string, string>
        {
            ["Baustein"] = "spannungsbewertung",
            ["in.Spannung"] = "../Spannung",
            ["in.Minimum"] = "220",
            ["in.Maximum"] = "Nenn * 1.04",
            ["out.IstWertOK"] = "Ok",
        });
        var boundary = BlockCatalog.Default.Create(BlockCatalog.LibraryBlock, "boundary", "Grenzverletzung", new Dictionary<string, string> { ["Baustein"] = "grenze" });
        var grenze = new CodeBlockDefinition("grenze", "Grenze") { Language = block.Language, Source = Src(language, "var x = Var<double>(\"Nenn\");", "x = var(\"Nenn\")") };

        var inner = Group("inner").Var("Ok", VarType.Bool).Var("Spannung", VarType.Double, 0.0).Add(step, boundary,
            Steps.Code("read", "Ok lesen", ctx => StepOutcome.Unrated(ctx.Get("Ok"))));
        var loop = Group("loop").Var("Spannung", VarType.Double, 245.0).Var("Nenn", VarType.Double, 230.0).Add(inner);
        var def = Test(loop);
        def.Library.Add(block);
        def.Library.Add(grenze);

        var run = Run(def);
        await run.RunAsync().WaitAsync(Slow);

        var use = run.Steps("use").Single();
        Assert.Equal(ResultRating.Fail, use.Rating);                    // 245 > 239,2
        Assert.Equal(245.0, use.Value);
        Assert.Equal(false, run.Steps("read").Single().Value);           // Ausgang → Zielvariable
        var b = run.Steps("boundary").Single();
        Assert.Equal(ResultRating.Error, b.Rating);
        Assert.Contains("Bausteingrenze", b.Message);
    }

    [Theory]
    [InlineData("C#", "var t = AskNumber(\"Temperatur\", 0, 50, \"°C\");\nreturn Measured(t, 0, 45, \"°C\");")]
    [InlineData("Python", "t = ask_number(\"Temperatur\", 0, 50, \"°C\")\nreturn Measured(t, 0, 45, \"°C\")")]
    public async Task Werkerinteraktion_aus_dem_Code(string language, string source)
    {
        var op = new ScriptedOperator().Answer("ask", p =>
        {
            Assert.Equal(OperatorPromptKind.Input, p.Kind);
            Assert.Equal(50.0, p.Max);
            return "46,5";
        });
        var run = Run(Test(Group("loop").Add(Code("ask", language, source))), op: op);
        await run.RunAsync().WaitAsync(Slow);
        var s = run.Steps("ask").Single();
        Assert.Equal(ResultRating.Fail, s.Rating);
        Assert.Equal(46.5, s.Value);
    }

    [Fact]
    public void Abhaengigkeitsanalyse_erkennt_Ein_und_Ausgaenge()
    {
        var visible = new List<(string, VarType)>
        {
            ("Spannung", VarType.Double), ("GrenzwertMin", VarType.Double), ("Ok", VarType.Bool), ("Zaehler", VarType.Int), ("Unbenutzt", VarType.Double),
        };
        var deps = CodeBlockConverter.Analyze(CodeLanguage.CSharp,
            "// Unbenutzt wird nur im Kommentar erwähnt\nOk = Spannung >= GrenzwertMin;\nZaehler++;\nLog(\"Unbenutzt\");\nvar x = Var<double>(\"../Spannung\");",
            visible);
        Assert.Equal(["Spannung:Input", "GrenzwertMin:Input", "Ok:Output", "Zaehler:InOut"], deps.Ports.Select(p => $"{p.Name}:{p.Direction}"));
        Assert.Single(deps.Warnings);

        var py = CodeBlockConverter.Analyze(CodeLanguage.Python, "# Ok\nOk = Spannung == GrenzwertMin\nZaehler += 1", visible);
        Assert.Equal(["Spannung:Input", "GrenzwertMin:Input", "Ok:Output", "Zaehler:InOut"], py.Ports.Select(p => $"{p.Name}:{p.Direction}"));
    }

    [Theory]
    [InlineData("C#")]
    [InlineData("Python")]
    public async Task Umwandeln_in_Baustein_verhaelt_sich_gleich(string language)
    {
        var source = Src(language,
            "Ok = Spannung >= GrenzwertMin;\nZaehler++;\nreturn Ok ? Pass(Spannung) : Fail(null, Spannung);",
            "Ok = Spannung >= GrenzwertMin\nZaehler += 1\nreturn Pass(Spannung) if Ok else Fail(None, Spannung)");
        TestDefinition Build()
        {
            var g = Group("g").Var("Spannung", VarType.Double, 230.0).Var("GrenzwertMin", VarType.Double, 220.0).Var("Ok", VarType.Bool).Var("Zaehler", VarType.Int, 1)
                .Add(Code("c", language, source), Steps.Code("read", "lesen", ctx => StepOutcome.Unrated($"{ctx.Get("Ok")}|{ctx.Get("Zaehler")}")));
            return Test(Group("loop").Add(g));
        }

        var original = Build();
        var runA = Run(original);
        await runA.RunAsync().WaitAsync(Slow);

        var converted = Build();
        var codeStep = (StepDefinition)converted.Find("c")!;
        var visible = CodeBlockConverter.VisibleVariables(converted, "c");
        var (block, step, _) = CodeBlockConverter.Convert(codeStep, visible, "bewertung", BlockCatalog.Default);
        var parent = (GroupDefinition)converted.Find("g")!;
        parent.Children[parent.Children.IndexOf(codeStep)] = step;
        converted.Library.Add(block);
        // Ok wird geschrieben und danach gelesen → Ein-/Ausgang
        Assert.Equal(["Spannung:Input", "GrenzwertMin:Input", "Ok:InOut", "Zaehler:InOut"], block.Ports.Select(p => $"{p.Name}:{p.Direction}"));
        Assert.Equal("c", step.Id); // Knoten-ID bleibt erhalten

        var runB = Run(converted);
        await runB.RunAsync().WaitAsync(Slow);

        Assert.Equal(runA.Steps("c").Single().Rating, runB.Steps("c").Single().Rating);
        Assert.Equal(runA.Steps("c").Single().Value, runB.Steps("c").Single().Value);
        Assert.Equal("True|2", runA.Steps("read").Single().Value);
        Assert.Equal(runA.Steps("read").Single().Value, runB.Steps("read").Single().Value);
    }

    [Fact]
    public void Bibliothek_und_CodeBlocks_werden_gespeichert()
    {
        var project = SampleProjects.AcPruefung();
        var block = new CodeBlockDefinition("b1", "Spannungsbewertung") { Language = CodeLanguage.Python, Version = "0.3", Source = "return Pass()" };
        block.Ports.Add(new CodePort("Spannung", VarType.Double, PortDirection.Input, "V", "Zu bewertende Spannung", 230.0));
        project.Definition.Library.Add(block);
        ((GroupDefinition)project.Definition.Find("pruefling")!).Children.Add(Code("cb", "C#", "return Pass();\n// \"Ümläüte\""));

        var serializer = new ProjectSerializer();
        var json = serializer.Serialize(project);
        var loaded = serializer.Deserialize(json);
        Assert.Equal(json, serializer.Serialize(loaded));
        var lb = Assert.Single(loaded.Definition.Library);
        Assert.Equal(CodeLanguage.Python, lb.Language);
        Assert.Equal(230.0, lb.Ports[0].TestValue);
        var cb = (StepDefinition)loaded.Definition.Find("cb")!;
        Assert.IsType<CodeBlockAction>(cb.Action);
        Assert.Contains("Ümläüte", ((CodeBlockAction)cb.Action).Source);
    }
}

