using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Workers;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>
/// C++-Worker (stabile C-Schnittstelle tu_invoke) und LabVIEW-VI-Prototyp (gebaute DLL über C++-Glue).
/// Ohne installierte C++-Toolchain werden die Tests übersprungen (Hinweis in der Ausgabe).
/// </summary>
#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public class CppAndLabViewTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(3);
    private static readonly string Cache = Path.Combine(Path.GetTempPath(), "Testumgebung-Tests", "cpp");

    private static readonly Lazy<CodeRuntime> Runtime = new(() => new CodeRuntime(new CodeRuntimeOptions
    {
        WorkerDirectory = Path.Combine(Path.GetDirectoryName(typeof(CppAndLabViewTests).Assembly.Location)!, "workers"),
        CppCacheDirectory = Cache,
    }));

    private static readonly Lazy<string> Info = new(() => Runtime.Value.GetRuntimeInfoAsync(CodeLanguage.Cpp, CancellationToken.None).GetAwaiter().GetResult());

    internal static void DisposeSharedRuntime()
    {
        if (Runtime.IsValueCreated) Runtime.Value.Dispose();
    }

    /// <summary>true, wenn keine Toolchain vorhanden ist (Test wird dann übersprungen).</summary>
    private static bool NoToolchain()
    {
        if (!Info.Value.Contains("Keine C++-Toolchain", StringComparison.Ordinal)) return false;
        Console.WriteLine($"Übersprungen: {Info.Value}");
        return true;
    }

    private static StepDefinition Cpp(string id, string source, string timeout = "5")
        => BlockCatalog.Default.Create(BlockCatalog.Code, id, id, new Dictionary<string, string>
        {
            ["Sprache"] = "C++",
            ["Quelltext"] = source,
            ["Timeout"] = timeout,
        });

    private static TestRun Run(TestDefinition def, HardwareRegistry? hw = null)
        => new(def, new TestRunOptions { Clock = new VirtualClock(), CodeRuntime = Runtime.Value, Hardware = hw });

    private sealed class MacroDebugHost : ICodeHost
    {
        public List<CodeDebugEvent> Stops { get; } = [];
        public object Get(string path) => throw new NotSupportedException();
        public void Set(string path, object? value) => throw new NotSupportedException();
        public Task<double> ReadAsync(string resource) => throw new NotSupportedException();
        public Task WriteAsync(string resource, double value) => throw new NotSupportedException();
        public Task<object?> AskAsync(OperatorPrompt prompt) => throw new NotSupportedException();
        public void Log(string message) { }
        public Task<CodeDebugStop> DebugAsync(CodeDebugEvent e)
        {
            Stops.Add(e);
            return Task.FromResult(new CodeDebugStop("continue", [], true));
        }
    }

    [CppFact]
    public async Task Cpp_Debugger_erreicht_Codezeilen_nach_einfachen_und_mehrzeiligen_Makros()
    {
        Assert.False(NoToolchain(), Info.Value);
        foreach (var macro in new[] { "#define VALUE 42", "#define VALUE \\\n 42" })
        {
            var host = new MacroDebugHost();
            var source = macro + "\ndouble value = VALUE;\nreturn Pass(value);";
            var request = new CodeRequest(CodeLanguage.Cpp, source, [], Debug: new CodeDebugConfig(true, [], false, true));
            var result = await Runtime.Value.InvokeAsync(request, new Dictionary<string, object?>(), host, TimeSpan.FromSeconds(20), default).WaitAsync(Slow);
            Assert.Equal(ResultRating.Pass, result.Rating); Assert.Equal(42.0, Convert.ToDouble(result.Value));
            var firstLine = macro.Split('\n').Length + 1;
            Assert.Contains(host.Stops, stop => stop.Line == firstLine && stop.Kind == "line");
            Assert.Contains(host.Stops, stop => stop.Line == firstLine + 1 && stop.Kind == "line");
        }
    }

    [CppFact]
    public async Task Cpp_Diagnosen_mit_Zeile_und_Toolchain_in_der_Laufzeitinfo()
    {
        Assert.False(NoToolchain(), Info.Value);
        var vars = new List<CodePort> { new("Spannung", VarType.Double, PortDirection.InOut) };
        var bad = await Runtime.Value.CompileAsync(new CodeRequest(CodeLanguage.Cpp, "double x = Spannung * 2;\nreturn y;", vars), CancellationToken.None).WaitAsync(Slow);
        Assert.False(bad.Success);
        Assert.Equal(2, bad.Diagnostics.First(d => d.IsError).Line);
        Assert.StartsWith("C++-Worker (", bad.RuntimeInfo);
        Assert.Contains("ABI 3", bad.RuntimeInfo);

        var ok = await Runtime.Value.CompileAsync(new CodeRequest(CodeLanguage.Cpp, "return Measured(Spannung, 220, 240, \"V\");", vars), CancellationToken.None).WaitAsync(Slow);
        Assert.True(ok.Success, string.Join("; ", ok.Diagnostics));
    }

    [CppFact]
    public async Task Cpp_CodeBlock_mit_Ordnervariablen_Hardware_und_Fehlern()
    {
        Assert.False(NoToolchain(), Info.Value);
        var sim = new SimulationAdapter();
        sim.SetInput("ai0", 230.25);
        var hw = new HardwareRegistry().Bind("U", sim, "ai0").Bind("AC_Quelle", sim, "ao0", requiresLock: true);
        var group = Group("g").Var("Spannung", VarType.Double, 1.0).Var("Kennung", VarType.String, "SN").Var("Ok", VarType.Bool).Add(
            Cpp("measure", "Spannung = Read(\"U\");\nKennung += \"-1\";\nOk = Spannung > 200;\nLog(\"U gelesen\");\nZaehler++;\nreturn Measured(Spannung, 220, 240, \"V\");"),
            Cpp("unreserved", "Write(\"AC_Quelle\", 230);"),
            Cpp("throws", "throw std::runtime_error(\"kaputt\");"),
            Steps.Code("read", "lesen", ctx => StepOutcome.Unrated($"{ctx.Get("Kennung")}|{ctx.Get("Ok")}|{ctx.Get("Zaehler")}")));
        var run = Run(Test(Group("loop").Var("Zaehler", VarType.Int, 41).Add(group)), hw);
        await run.RunAsync().WaitAsync(Slow);

        var m = run.Steps("measure").Single();
        Assert.True(m.Rating == ResultRating.Pass, m.Message);
        Assert.Equal(230.25, m.Value);
        Assert.Equal("SN-1|True|42", run.Steps("read").Single().Value);
        Assert.Contains(run.Events.OfType<Diagnostic>(), d => d.Message == "U gelesen");
        var u = run.Steps("unreserved").Single();
        Assert.Equal(ResultRating.Error, u.Rating);
        Assert.Contains("ohne Reservierung", u.Message);
        Assert.Equal("C++-Ausnahme: kaputt", run.Steps("throws").Single().Message);
    }

    [CppFact]
    public async Task Absturz_im_nativen_Code_betrifft_nur_den_Worker()
    {
        Assert.False(NoToolchain(), Info.Value);
        var run = Run(Test(Group("loop").Add(
            Cpp("crash", "volatile int* p = nullptr;\n*p = 42;"),
            Cpp("after", "return Pass();"))));
        await run.RunAsync().WaitAsync(Slow);
        var crash = run.Steps("crash").Single();
        Assert.Equal(ResultRating.Error, crash.Rating);
        Assert.Contains("unerwartet beendet", crash.Message);
        Assert.Equal(ResultRating.Pass, run.Steps("after").Single().Rating); // neuer Worker
    }

    [CppTheory]
    [InlineData("while (true) { Sleep(50); }")]
    [InlineData("volatile long long x = 0;\nwhile (true) { x = x + 1; }")]
    public async Task Werkerabbruch_kooperativ_oder_per_Prozessabbruch(string source)
    {
        Assert.False(NoToolchain(), Info.Value);
        var probe = new Probe();
        var g = Group("g").Add(Cpp("loop.code", source, "0.5"));
        g.ExitGroup = Group("g.exit").Add(probe.Mark("exit"));
        var run = Run(Test(new GroupDefinition("loop", "Loop") { Repetition = Repetition.Endless }.Add(g)));
        await Runtime.Value.CompileAsync(new CodeRequest(CodeLanguage.Cpp, source, []), CancellationToken.None).WaitAsync(Slow); // vorab bauen
        var started = run.WaitFor<StepStarted>(s => s.NodeId == "loop.code");
        var task = run.RunAsync();
        await started.WaitAsync(Slow);
        await Task.Delay(1500);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        run.RequestAbort();
        var status = await task.WaitAsync(Slow);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Abbruch dauerte {sw.Elapsed}");
        Assert.True(run.Steps("loop.code").Single().Cancelled);
        Assert.Equal(["exit"], probe.Marks);
        Assert.True(status.OperatorAbort);
    }

    [CppFact]
    public async Task Cpp_Bibliotheksbaustein_mit_Ports()
    {
        Assert.False(NoToolchain(), Info.Value);
        var block = new CodeBlockDefinition("cpp", "Spannung C++") { Language = CodeLanguage.Cpp };
        block.Ports.AddRange(
        [
            new CodePort("Spannung", VarType.Double, PortDirection.Input),
            new CodePort("Maximum", VarType.Double, PortDirection.Input),
            new CodePort("IstWertOK", VarType.Bool, PortDirection.Output),
        ]);
        block.Source = "IstWertOK = Spannung <= Maximum;\nreturn IstWertOK ? Pass(Spannung) : Fail(\"zu hoch\", Spannung);";
        var step = BlockCatalog.Default.Create(BlockCatalog.LibraryBlock, "use", "use", new Dictionary<string, string>
        {
            ["Baustein"] = "cpp",
            ["in.Spannung"] = "245",
            ["in.Maximum"] = "240",
            ["out.IstWertOK"] = "Ok",
        });
        var def = Test(Group("loop").Var("Ok", VarType.Bool, true).Add(step, Steps.Code("read", "r", ctx => StepOutcome.Unrated(ctx.Get("Ok")))));
        def.Library.Add(block);
        var run = Run(def);
        await run.RunAsync().WaitAsync(Slow);
        Assert.Equal(ResultRating.Fail, run.Steps("use").Single().Rating);
        Assert.Equal(false, run.Steps("read").Single().Value);
    }

    /// <summary>Baut eine Bibliothek, die eine mit LabVIEW gebaute VI-DLL nachbildet (Export im C-Format).</summary>
    private static async Task<string> FakeLabViewDll()
    {
        const string marker = "FakeLabViewSpannungsbewertung";
        var source = "//#tu-raw\n" +
                     $"// {marker}\n" +
                     "TU_EXPORT void Spannungsbewertung(double U, double Min, double Max, uint8_t* Ok, int32_t* Bewertung, char* Fehler, int32_t len)\n" +
                     "{\n" +
                     "    *Ok = (U >= Min && U <= Max) ? 1 : 0;\n" +
                     "    *Bewertung = *Ok ? 1 : 2;\n" +
                     "    if (U < 0) { const char* m = \"Messung ungültig\"; int i = 0; for (; m[i] && i < len - 1; ++i) Fehler[i] = m[i]; Fehler[i] = 0; }\n" +
                     "}\n" +
                     "TU_EXPORT int tu_invoke(const tu_host*) { return 0; }\n";
        var result = await Runtime.Value.CompileAsync(new CodeRequest(CodeLanguage.Cpp, source, []), CancellationToken.None).WaitAsync(Slow);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var dir = Directory.GetDirectories(Cache).First(d => File.Exists(Path.Combine(d, "block.cpp")) && File.ReadAllText(Path.Combine(d, "block.cpp")).Contains(marker));
        return Directory.GetFiles(dir, "block.*").First(f => !f.EndsWith(".cpp", StringComparison.Ordinal) && !f.EndsWith(".obj") && !f.EndsWith(".exp") && !f.EndsWith(".lib"));
    }

    private static CodeBlockDefinition LabViewBlock(string dll, string function = "Spannungsbewertung")
    {
        var block = new CodeBlockDefinition("vi", "Spannungsbewertung (VI)")
        {
            Language = CodeLanguage.LabView,
            Source = $"vi = C:\\gibt\\es\\nicht.vi\ndll = {dll}\nfunktion = {function}\naufruf = C\n",
        };
        block.Ports.AddRange(
        [
            new CodePort("U", VarType.Double, PortDirection.Input),
            new CodePort("Min", VarType.Double, PortDirection.Input),
            new CodePort("Max", VarType.Double, PortDirection.Input),
            new CodePort("Ok", VarType.Bool, PortDirection.Output),
            new CodePort("Bewertung", VarType.Int, PortDirection.Output),
            new CodePort("Fehler", VarType.String, PortDirection.Output),
        ]);
        return block;
    }

    [CppFact]
    public async Task LabVIEW_VI_als_DLL_ueber_definierten_Connector()
    {
        Assert.False(NoToolchain(), Info.Value);
        var dll = await FakeLabViewDll();
        var block = LabViewBlock(dll);

        var check = await Runtime.Value.CompileAsync(new CodeRequest(CodeLanguage.LabView, block.Source, block.Ports), CancellationToken.None).WaitAsync(Slow);
        Assert.True(check.Success, string.Join("; ", check.Diagnostics));
        Assert.Contains(check.Diagnostics, d => !d.IsError && d.Message.Contains("VI"));   // VI-Datei fehlt → nur Warnung
        Assert.StartsWith("LabVIEW-Prototyp über C++-Worker", check.RuntimeInfo);

        StepDefinition Use(string id, string u) => BlockCatalog.Default.Create(BlockCatalog.LibraryBlock, id, id, new Dictionary<string, string>
        {
            ["Baustein"] = "vi",
            ["in.U"] = u,
            ["in.Min"] = "220",
            ["in.Max"] = "240",
            ["out.Ok"] = "Ok",
        });
        var def = Test(Group("loop").Var("Ok", VarType.Bool).Add(Use("ok", "230"), Use("fail", "250"), Use("err", "-1"),
            Steps.Code("read", "r", ctx => StepOutcome.Unrated(ctx.Get("Ok")))));
        def.Library.Add(block);
        var missing = LabViewBlock(Path.Combine(Cache, "fehlt.dll"));
        def.Library.Add(new CodeBlockDefinition("vi2", "fehlend") { Language = CodeLanguage.LabView, Source = missing.Source });
        def.Library[^1].Ports.AddRange(missing.Ports);
        ((GroupDefinition)def.Find("loop")!).Add(BlockCatalog.Default.Create(BlockCatalog.LibraryBlock, "missing", "missing", new Dictionary<string, string>
        {
            ["Baustein"] = "vi2",
            ["in.U"] = "1",
            ["in.Min"] = "0",
            ["in.Max"] = "2",
        }));

        var run = Run(def);
        await run.RunAsync().WaitAsync(Slow);
        Assert.Equal(ResultRating.Pass, run.Steps("ok").Single().Rating);
        Assert.Equal(ResultRating.Fail, run.Steps("fail").Single().Rating);
        var err = run.Steps("err").Single();
        Assert.Equal(ResultRating.Error, err.Rating);
        Assert.Equal("Messung ungültig", err.Message);
        Assert.Equal(false, run.Steps("read").Single().Value);
        var m = run.Steps("missing").Single();
        Assert.Equal(ResultRating.Error, m.Rating);
        Assert.Contains("LabVIEW-DLL nicht ladbar", m.Message);
    }

    [CppFact]
    public async Task LabVIEW_Konfigurationsfehler_werden_vor_dem_Aufruf_gemeldet()
    {
        Assert.False(NoToolchain(), Info.Value);
        var block = LabViewBlock("", "");
        var r = await Runtime.Value.CompileAsync(new CodeRequest(CodeLanguage.LabView, block.Source, block.Ports), CancellationToken.None).WaitAsync(Slow);
        Assert.False(r.Success);
        Assert.Contains(r.Diagnostics, d => d.IsError && d.Message.Contains("DLL"));
        Assert.Contains(r.Diagnostics, d => d.IsError && d.Message.Contains("Funktionsname"));
        var ex = await Assert.ThrowsAsync<CodeExecutionException>(() => Runtime.Value.InvokeAsync(
            new CodeRequest(CodeLanguage.LabView, block.Source, block.Ports), new Dictionary<string, object?>(), null!, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Contains("DLL", ex.Message);
    }
}

