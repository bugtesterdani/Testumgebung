/**
 * @file
 * @brief Verbindet Codeeditor, Build-Prüfung und Bibliotheksports mit dem geöffneten Projekt.
 */

using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

internal static class CodeHelp
{
    public static string For(CodeLanguage language) => language switch
    {
        CodeLanguage.Python =>
            "Rückgabe: Pass(wert), Fail(meldung, wert), Error(meldung), Measured(wert, min, max, einheit). " +
            "Hardware: read(res), write(res, wert). Werker: ask(text), ask_number(text, min, max, einheit), confirm(text). " +
            "Sonstiges: log(text), var(\"../Name\"), set_var(\"Name\", wert), sleep(s), cancelled().",
        CodeLanguage.Cpp =>
            "C++17-Funktionsrumpf. Rückgabe: Pass(wert), Fail(meldung, wert), Error(meldung), Measured(wert, min, max, einheit). " +
            "Hardware: Read(res), Write(res, wert). Werker: AskNumber(text, min, max, einheit), Confirm(text). " +
            "Sonstiges: Log(text), Var(\"../Name\"), VarText(…), SetVar(…), Sleep(ms), IsCancelled(). Typen: double, long long, bool, std::string.",
        CodeLanguage.LabView =>
            "Prototyp: VI mit dem Application Builder als DLL bauen. Connector = Portliste in Reihenfolge: Eingänge als Wert " +
            "(double, int32, LVBoolean, C-String), Ausgänge als Zeiger, String-Ausgang als (Puffer, Länge). " +
            "Optional: Int-Ausgang „Bewertung“ (0–3) und String-Ausgang „Fehler“. Bearbeitung extern in LabVIEW.",
        _ =>
            "Rückgabe: Pass(wert), Fail(meldung, wert), Error(meldung), Measured(wert, min, max, einheit). " +
            "Hardware: Read(res), Write(res, wert). Werker: Ask(text), AskNumber(text, min, max, einheit), Confirm(text). " +
            "Sonstiges: Log(text), Var<T>(\"../Name\"), SetVar(\"Name\", wert), Sleep(ms), IsCancelled.",
    };

    public static string Template(CodeLanguage language) => language switch
    {
        CodeLanguage.Python => "# Ein- und Ausgänge sind lokale Namen (siehe Schnittstelle).\nIstWertOK = Minimum <= Spannung <= Maximum\nreturn Pass(Spannung) if IstWertOK else Fail(\"Spannung außerhalb\", Spannung)",
        CodeLanguage.Cpp => "// Ein- und Ausgänge sind lokale Variablen (siehe Schnittstelle).\nIstWertOK = Spannung >= Minimum && Spannung <= Maximum;\nreturn IstWertOK ? Pass(Spannung) : Fail(\"Spannung außerhalb\", Spannung);",
        CodeLanguage.LabView => Testumgebung.Workers.LabViewSettings.Template,
        _ => "// Ein- und Ausgänge sind Felder (siehe Schnittstelle).\nIstWertOK = Spannung >= Minimum && Spannung <= Maximum;\nreturn IstWertOK ? Pass(Spannung) : Fail(\"Spannung außerhalb\", Spannung);",
    };

    public static string BuildText(CompileResult result)
        => result.Success
            ? $"Build erfolgreich – {result.Errors} Fehler, {result.Warnings} Warnungen"
            : $"Build fehlgeschlagen – {result.Errors} Fehler, {result.Warnings} Warnungen";
}

/// <summary>Werkzeuge für einen CodeBlock im Ablauf: Build prüfen, sichtbare Variablen, Umwandeln.</summary>
public sealed class CodeToolsViewModel : ObservableObject
{
    private readonly EditorViewModel _editor;
    private readonly NodeViewModel _node;
    private string _buildStatus = "Noch nicht geprüft";
    private bool? _buildOk;
    private bool _isBusy;
    private string _runtimeInfo = "";

    public CodeToolsViewModel(EditorViewModel editor, NodeViewModel node)
    {
        _editor = editor;
        _node = node;
        Visible = CodeBlockConverter.VisibleVariables(editor.Project.Definition, node.Id);
        VisibleVariables = Visible.Select(v => $"{v.Name} : {v.Type.ToString().ToLowerInvariant()}").ToList();
        BuildCommand = new RelayCommand(() => _ = BuildAsync(), () => !IsBusy);
        ConvertCommand = new RelayCommand(Convert);
    }

    internal IReadOnlyList<(string Name, VarType Type)> Visible { get; }

    public IReadOnlyList<string> VisibleVariables { get; }
    public ObservableCollection<CodeDiagnostic> Diagnostics { get; } = [];
    public RelayCommand BuildCommand { get; }
    public RelayCommand ConvertCommand { get; }

    private StepDefinition Step => _node.Step!;
    public CodeLanguage Language => BlockCatalog.ParseLanguage(Step.Parameters.GetValueOrDefault("Sprache") ?? "C#");
    public string ApiHelp => CodeHelp.For(Language);

    public string BuildStatus
    {
        get => _buildStatus;
        private set => SetProperty(ref _buildStatus, value);
    }

    public bool? BuildOk
    {
        get => _buildOk;
        private set => SetProperty(ref _buildOk, value);
    }

    public string RuntimeInfo
    {
        get => _runtimeInfo;
        private set => SetProperty(ref _runtimeInfo, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            SetProperty(ref _isBusy, value);
            BuildCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Übersetzt den Code im Worker (C#: Roslyn, Python: compile) mit den sichtbaren Ordnervariablen.</summary>
    public async Task BuildAsync()
    {
        if (_editor.CodeRuntime is not { } runtime)
        {
            BuildStatus = "Keine Ausführungsumgebung konfiguriert.";
            BuildOk = false;
            return;
        }
        IsBusy = true;
        BuildStatus = "Build läuft …";
        try
        {
            var ports = Visible.Select(v => new CodePort(v.Name, v.Type, PortDirection.InOut)).ToList();
            var result = await runtime.CompileAsync(new CodeRequest(Language, Step.Parameters.GetValueOrDefault("Quelltext") ?? "", ports) { Assemblies = _editor.Project.Definition.Assemblies }, CancellationToken.None);
            Diagnostics.Clear();
            foreach (var d in result.Diagnostics) Diagnostics.Add(d);
            BuildOk = result.Success;
            BuildStatus = CodeHelp.BuildText(result);
            RuntimeInfo = result.RuntimeInfo;
        }
        catch (Exception ex) when (ex is CodeExecutionException or TimeoutException or InvalidOperationException)
        {
            BuildOk = false;
            BuildStatus = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Convert()
    {
        var warnings = _editor.ConvertToLibraryBlock(_node);
        _editor.Notice = warnings.Count == 0
            ? "CodeBlock in Baustein umgewandelt – Ein-/Ausgänge aus verwendeten Variablen erzeugt."
            : string.Join(" ", warnings);
    }
}

/// <summary>Bindung eines Ports: Eingang = Ausdruck (Konstante oder Variable), Ausgang = Zielvariable.</summary>
public sealed class PortBindingViewModel(StepPropertiesViewModel owner, CodePort port) : ObservableObject
{
    public CodePort Port { get; } = port;
    public string Name => Port.Name;
    public string TypeText => Port.Type.ToString().ToLowerInvariant();
    public string Unit => Port.Unit ?? "";

    public string DirectionText => Port.Direction switch
    {
        PortDirection.Input => "Eingang",
        PortDirection.Output => "Ausgang",
        _ => "Ein-/Ausgang",
    };

    public string Hint => Port.Direction switch
    {
        PortDirection.Input => "Ausdruck: Konstante oder Variable, z. B. 220 oder ../Spannung",
        PortDirection.Output => "Zielvariable (leer = verwerfen)",
        _ => "Variable (wird gelesen und zurückgeschrieben)",
    };

    private string Key => (Port.Direction == PortDirection.Output ? BlockCatalog.OutputBindingPrefix : BlockCatalog.InputBindingPrefix) + Port.Name;

    public string Value
    {
        get => owner.Step.Parameters.GetValueOrDefault(Key) ?? "";
        set
        {
            if (Value == value) return;
            owner.Step.Parameters[Key] = value;
            OnPropertyChanged();
            owner.ParametersChanged();
        }
    }
}

/// <summary>Bausteinauswahl und Bindungen eines Baustein-Schritts.</summary>
public sealed class LibraryBindingsViewModel : ObservableObject
{
    private readonly EditorViewModel _editor;
    private readonly StepPropertiesViewModel _owner;

    public LibraryBindingsViewModel(EditorViewModel editor, StepPropertiesViewModel owner)
    {
        _editor = editor;
        _owner = owner;
        RebuildRows();
    }

    /// <summary>Auswählbare Bausteine: Codebausteine bzw. grafische Bausteine je nach Schritttyp.</summary>
    public IReadOnlyList<BlockRef> Blocks => _editor.BlockRefsFor(_owner.Step.BlockType);
    public ObservableCollection<PortBindingViewModel> Rows { get; } = [];

    public BlockRef? SelectedBlock
    {
        get => Blocks.FirstOrDefault(b => b.Id == _owner.Step.Parameters.GetValueOrDefault("Baustein"));
        set
        {
            if (value is null || value.Id == SelectedBlock?.Id) return;
            _owner.Step.Parameters["Baustein"] = value.Id;
            RebuildRows();
            OnPropertyChanged();
            OnPropertyChanged(nameof(BlockInfo));
            _owner.ParametersChanged();
        }
    }

    public string BlockInfo => SelectedBlock?.Info ?? "Kein Baustein gewählt";

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var p in SelectedBlock?.Ports ?? []) Rows.Add(new PortBindingViewModel(_owner, p));
    }
}

/// <summary>Auswahleintrag eines Bausteins (Code oder grafisch) mit Schnittstelle.</summary>
public sealed record BlockRef(string Id, string Name, string Info, IReadOnlyList<CodePort> Ports)
{
    public override string ToString() => Name;
}

/// <summary>Bearbeitbarer Port eines Bibliotheksbausteins.</summary>
public sealed class PortRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private string _name;
    private VarType _type;
    private PortDirection _direction;
    private string _unit;
    private string _description;
    private string _testValue;
    private string? _error;

    public PortRowViewModel(Action changed, CodePort port)
    {
        _changed = changed;
        _name = port.Name;
        _type = port.Type;
        _direction = port.Direction;
        _unit = port.Unit ?? "";
        _description = port.Description ?? "";
        _testValue = port.TestValue switch
        {
            null => "",
            string s => s,
            var v => TypedValue.Format(v),
        };
    }

    public static IReadOnlyList<VarType> Types { get; } = Enum.GetValues<VarType>();
    public static IReadOnlyList<PortDirection> Directions { get; } = Enum.GetValues<PortDirection>();

    public string Name { get => _name; set { if (SetProperty(ref _name, value.Trim())) _changed(); } }
    public VarType Type { get => _type; set { if (SetProperty(ref _type, value)) _changed(); } }
    public PortDirection Direction { get => _direction; set { if (SetProperty(ref _direction, value)) _changed(); } }
    public string Unit { get => _unit; set { if (SetProperty(ref _unit, value)) _changed(); } }
    public string Description { get => _description; set { if (SetProperty(ref _description, value)) _changed(); } }
    public string TestValue { get => _testValue; set { if (SetProperty(ref _testValue, value)) _changed(); } }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public CodePort? ToPort()
    {
        if (string.IsNullOrWhiteSpace(Name) || !(char.IsLetter(Name[0]) || Name[0] == '_') || !Name.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            Error = "Ungültiger Name";
            return null;
        }
        object? test = null;
        if (TestValue.Length > 0)
        {
            try { test = TypedValue.Parse(TestValue, Type); }
            catch (TypeMismatchException ex)
            {
                Error = "Testwert: " + ex.Message;
                return null;
            }
        }
        Error = null;
        return new CodePort(Name, Type, Direction, Unit.Length == 0 ? null : Unit, Description.Length == 0 ? null : Description, test);
    }
}

/// <summary>Host für Testläufe in der Bibliothek: Simulation, kein Variablenzugriff (Bausteingrenze).</summary>
internal sealed class LibraryTestHost(TestProject project, List<string> log) : ICodeHost
{
    private readonly Testumgebung.Hardware.HardwareRegistry _hardware = SampleProjects.CreateSimulation(project).Registry;

    public object Get(string path) => throw new InvalidOperationException("Bausteingrenze: kein impliziter Zugriff auf äußere Variablen.");
    public void Set(string path, object? value) => throw new InvalidOperationException("Bausteingrenze: kein impliziter Zugriff auf äußere Variablen.");

    public Task<double> ReadAsync(string resource)
    {
        var b = _hardware.Resolve(resource);
        return b.Adapter.ReadAsync(b.Channel, CancellationToken.None);
    }

    public Task WriteAsync(string resource, double value)
    {
        var b = _hardware.Resolve(resource);
        return b.Adapter.WriteAsync(b.Channel, value, CancellationToken.None);
    }

    public Task<object?> AskAsync(OperatorPrompt prompt)
        => Task.FromResult<object?>(prompt.DefaultValue ?? (prompt.InputType is { } t ? TypedValue.Default(t) : true));

    public Task<object?> DeviceAsync(string operation, IReadOnlyDictionary<string, object?> arguments)
    {
        var resource = arguments.TryGetValue("resource", out var r) && r is string s ? s : throw new InvalidOperationException("Argument „resource“ fehlt.");
        var b = _hardware.Resolve(resource);
        return Testumgebung.Engine.Blocks.CodeDevices.ExecuteAsync(b.Adapter, b.Channel, operation, arguments, CancellationToken.None);
    }

    public void Log(string message)
    {
        lock (log) log.Add(message);
    }
}

/// <summary>Ein Bibliotheksbaustein (Abbildung 2): Code, Schnittstelle, Build und Testausführung.</summary>
public sealed class LibraryBlockViewModel : ObservableObject
{
    private static int _testCounter;
    private readonly EditorViewModel _editor;
    private string _buildStatus = "Noch nicht geprüft";
    private bool? _buildOk;
    private string _testResult = "";
    private bool _isBusy;
    private string _runtimeInfo = "";
    private string _invocationId = "";

    public LibraryBlockViewModel(EditorViewModel editor, CodeBlockDefinition block)
    {
        _editor = editor;
        Block = block;
        foreach (var p in block.Ports) Ports.Add(new PortRowViewModel(PortsChanged, p));
        AddPortCommand = new RelayCommand(AddPort);
        RemovePortCommand = new RelayCommand(p => { if (p is PortRowViewModel row) { Ports.Remove(row); PortsChanged(); } });
        BuildCommand = new RelayCommand(() => _ = BuildAsync(), () => !IsBusy);
        TestCommand = new RelayCommand(() => _ = TestAsync(), () => !IsBusy);
        OpenInLabViewCommand = new RelayCommand(OpenInLabView, () => IsLabView);
    }

    /// <summary>VI extern in LabVIEW öffnen (Bearbeitung erfolgt in LabVIEW).</summary>
    private void OpenInLabView()
    {
        var vi = Testumgebung.Workers.LabViewSettings.Parse(Block.Source).ViPath;
        if (string.IsNullOrWhiteSpace(vi)) BuildStatus = "Kein VI angegeben (vi = …).";
        else if (!File.Exists(vi)) BuildStatus = $"VI '{vi}' nicht gefunden.";
        else _editor.Dialogs?.OpenExternal(vi);
    }

    public CodeBlockDefinition Block { get; }
    public ObservableCollection<PortRowViewModel> Ports { get; } = [];
    public ObservableCollection<CodeDiagnostic> Diagnostics { get; } = [];
    public ObservableCollection<string> TestLog { get; } = [];
    public RelayCommand AddPortCommand { get; }
    public RelayCommand RemovePortCommand { get; }
    public RelayCommand BuildCommand { get; }
    public RelayCommand TestCommand { get; }

    public string Id => Block.Id;
    public IReadOnlyList<string> Languages => BlockCatalog.LibraryLanguages;
    public bool IsLabView => Block.Language == CodeLanguage.LabView;
    public RelayCommand OpenInLabViewCommand { get; }
    public string ApiHelp => CodeHelp.For(Block.Language);

    public string Name
    {
        get => Block.Name;
        set { if (string.IsNullOrWhiteSpace(value)) return; Block.Name = value; Changed(); OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
    }

    public string DisplayName => $"{Block.Name}  v{Block.Version}";

    public string Version
    {
        get => Block.Version;
        set { Block.Version = value; Changed(); OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
    }

    public string Description
    {
        get => Block.Description ?? "";
        set { Block.Description = value; Changed(); OnPropertyChanged(); }
    }

    public string Language
    {
        get => BlockCatalog.LanguageText(Block.Language);
        set
        {
            var lang = BlockCatalog.ParseLanguage(value);
            if (lang == Block.Language) return;
            var wasTemplate = string.IsNullOrWhiteSpace(Block.Source) || Block.Source == CodeHelp.Template(Block.Language);
            Block.Language = lang;
            if (wasTemplate || lang == CodeLanguage.LabView) Block.Source = CodeHelp.Template(lang);
            Changed();
            OnPropertiesChanged(nameof(Language), nameof(ApiHelp), nameof(Source), nameof(IsLabView));
            OpenInLabViewCommand.RaiseCanExecuteChanged();
        }
    }

    public string Source
    {
        get => Block.Source;
        set { if (Block.Source == value) return; Block.Source = value; Changed(); OnPropertyChanged(); }
    }

    public IReadOnlyList<string> UsedIn => _editor.Project.Definition.AllNodes().OfType<StepDefinition>()
        .Where(s => s.BlockType == BlockCatalog.LibraryBlock && s.Parameters.GetValueOrDefault("Baustein") == Block.Id)
        .Select(s => s.Name).ToList();

    public string BuildStatus { get => _buildStatus; private set => SetProperty(ref _buildStatus, value); }
    public bool? BuildOk { get => _buildOk; private set => SetProperty(ref _buildOk, value); }
    public string TestResult { get => _testResult; private set => SetProperty(ref _testResult, value); }
    public string RuntimeInfo { get => _runtimeInfo; private set => SetProperty(ref _runtimeInfo, value); }
    public string InvocationId { get => _invocationId; private set => SetProperty(ref _invocationId, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            SetProperty(ref _isBusy, value);
            BuildCommand.RaiseCanExecuteChanged();
            TestCommand.RaiseCanExecuteChanged();
        }
    }

    private void AddPort()
    {
        var n = 1;
        while (Ports.Any(p => p.Name == $"Eingang{n}")) n++;
        Ports.Add(new PortRowViewModel(PortsChanged, new CodePort($"Eingang{n}", VarType.Double, PortDirection.Input)));
        PortsChanged();
    }

    internal void PortsChanged()
        => SynchronizePorts();

    private bool SynchronizePorts()
    {
        var ports = Ports.Select(p => p.ToPort()).ToList();
        foreach (var dup in Ports.GroupBy(p => p.Name).Where(g => g.Count() > 1).SelectMany(g => g)) dup.ToPort();
        if (ports.Any(p => p is null) || Ports.GroupBy(p => p.Name).Any(g => g.Count() > 1)) return false;
        Block.Ports.Clear();
        Block.Ports.AddRange(ports!);
        _editor.RaiseLibraryChanged();
        return true;
    }

    private void Changed() => _editor.MarkChanged();

    internal void OnUsageChanged() => OnPropertyChanged(nameof(UsedIn));

    public async Task BuildAsync()
    {
        if (_editor.CodeRuntime is not { } runtime)
        {
            BuildOk = false;
            BuildStatus = "Keine Ausführungsumgebung konfiguriert.";
            return;
        }
        IsBusy = true;
        BuildStatus = "Build läuft …";
        try
        {
            if (!SynchronizePorts())
            {
                Diagnostics.Clear();
                BuildOk = false;
                BuildStatus = "Schnittstelle ungültig: Portnamen, doppelte Namen und Testwerte prüfen.";
                return;
            }
            var result = await runtime.CompileAsync(new CodeRequest(Block.Language, Block.Source, Block.Ports) { Assemblies = _editor.Project.Definition.Assemblies }, CancellationToken.None);
            Diagnostics.Clear();
            foreach (var d in result.Diagnostics) Diagnostics.Add(d);
            BuildOk = result.Success;
            BuildStatus = CodeHelp.BuildText(result);
            RuntimeInfo = result.RuntimeInfo;
        }
        catch (Exception ex) when (ex is CodeExecutionException or TimeoutException or InvalidOperationException)
        {
            BuildOk = false;
            BuildStatus = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Testausführung mit den Testwerten der Eingänge (Simulation, kein Variablenzugriff).</summary>
    public async Task TestAsync()
    {
        if (_editor.CodeRuntime is not { } runtime)
        {
            TestResult = "Keine Ausführungsumgebung konfiguriert.";
            return;
        }
        IsBusy = true;
        TestLog.Clear();
        InvocationId = $"CB-{DateTime.Now:yyyyMMdd}-{Interlocked.Increment(ref _testCounter):D3}";
        TestResult = "Ausführung läuft …";
        var log = new List<string>();
        try
        {
            if (!SynchronizePorts())
            {
                TestResult = "ERROR · Schnittstelle ungültig: Portnamen, doppelte Namen und Testwerte prüfen.";
                return;
            }
            var values = Block.Ports.Where(p => p.IsInput)
                .ToDictionary(p => p.Name, p => (object?)(p.TestValue ?? TypedValue.Default(p.Type)));
            var result = await runtime.InvokeAsync(new CodeRequest(Block.Language, Block.Source, Block.Ports, InvocationId) { Assemblies = _editor.Project.Definition.Assemblies }, values,
                new LibraryTestHost(_editor.Project, log), TimeSpan.FromSeconds(5), CancellationToken.None);
            var outputs = Block.Ports.Where(p => p.IsOutput && result.Values.ContainsKey(p.Name))
                .Select(p => $"{p.Name} = {Show(result.Values[p.Name])}");
            TestResult = $"{result.Rating.ToDisplay()}"
                         + (result.Value is null ? "" : $" · Wert {Show(result.Value)}{(result.Unit is null ? "" : " " + result.Unit)}")
                         + (result.Message is null ? "" : $" · {result.Message}")
                         + (outputs.Any() ? " · " + string.Join(", ", outputs) : "");
        }
        catch (Exception ex) when (ex is CodeExecutionException or TimeoutException or InvalidOperationException or KeyNotFoundException)
        {
            TestResult = "ERROR · " + ex.Message;
        }
        finally
        {
            foreach (var l in log) TestLog.Add(l);
            IsBusy = false;
        }
    }

    private static string Show(object? value) => value switch
    {
        null => "—",
        double d => d.ToString("0.###", CultureInfo.GetCultureInfo("de-DE")),
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? "",
    };
}

/// <summary>Grafischer Baustein in der Bibliothek: Schnittstelle bearbeiten, Körper im Ablaufeditor.</summary>
public sealed class GroupBlockViewModel : ObservableObject
{
    private readonly EditorViewModel _editor;

    public GroupBlockViewModel(EditorViewModel editor, GroupBlockDefinition block)
    {
        _editor = editor;
        Block = block;
        foreach (var p in block.Ports) Ports.Add(new PortRowViewModel(PortsChanged, p));
        AddPortCommand = new RelayCommand(() =>
        {
            var n = 1;
            while (Ports.Any(p => p.Name == $"Eingang{n}")) n++;
            Ports.Add(new PortRowViewModel(PortsChanged, new CodePort($"Eingang{n}", VarType.Double, PortDirection.Input)));
            PortsChanged();
        });
        RemovePortCommand = new RelayCommand(p => { if (p is PortRowViewModel row) { Ports.Remove(row); PortsChanged(); } });
        OpenInEditorCommand = new RelayCommand(() => OpenInEditor?.Invoke(this));
    }

    public GroupBlockDefinition Block { get; }
    public ObservableCollection<PortRowViewModel> Ports { get; } = [];
    public RelayCommand AddPortCommand { get; }
    public RelayCommand RemovePortCommand { get; }
    public RelayCommand OpenInEditorCommand { get; }

    /// <summary>Wird von der Bibliothek an die Shell weitergereicht (Navigation zum Körper).</summary>
    public Action<GroupBlockViewModel>? OpenInEditor { get; set; }

    public string Id => Block.Id;
    public string Language => "Grafisch";
    public string DisplayName => $"{Block.Name}  v{Block.Version}";

    public string Name
    {
        get => Block.Name;
        set { if (string.IsNullOrWhiteSpace(value)) return; Block.Name = value; _editor.MarkChanged(); OnPropertiesChanged(nameof(Name), nameof(DisplayName)); }
    }

    public string Version
    {
        get => Block.Version;
        set { Block.Version = value; _editor.MarkChanged(); OnPropertiesChanged(nameof(Version), nameof(DisplayName)); }
    }

    public string Description
    {
        get => Block.Description ?? "";
        set { Block.Description = value; _editor.MarkChanged(); OnPropertyChanged(); }
    }

    /// <summary>Struktur des Körpers (eingerückt) zur Übersicht.</summary>
    public IReadOnlyList<string> Structure
    {
        get
        {
            var lines = new List<string>();
            void Walk(GroupDefinition g, int depth)
            {
                lines.Add(new string(' ', depth * 3) + "▸ " + g.Name);
                foreach (var c in g.Children)
                {
                    if (c is GroupDefinition cg) Walk(cg, depth + 1);
                    else lines.Add(new string(' ', (depth + 1) * 3) + "• " + c.Name);
                }
                if (g.ExitGroup is { } exit) Walk(exit, depth + 1);
            }
            Walk(Block.Body, 0);
            return lines;
        }
    }

    public IReadOnlyList<string> UsedIn => _editor.Project.Definition.AllNodes().OfType<StepDefinition>()
        .Where(s => s.BlockType == BlockCatalog.GroupBlock && s.Parameters.GetValueOrDefault("Baustein") == Block.Id)
        .Select(s => s.Name).ToList();

    internal void OnUsageChanged() => OnPropertiesChanged(nameof(UsedIn), nameof(Structure));

    private void PortsChanged()
    {
        var ports = Ports.Select(p => p.ToPort()).ToList();
        if (ports.Any(p => p is null) || Ports.GroupBy(p => p.Name).Any(g => g.Count() > 1)) return;
        Block.Ports.Clear();
        Block.Ports.AddRange(ports!);
        _editor.RaiseLibraryChanged();
        _editor.RebuildTree();
    }
}

/// <summary>Baustein-Bibliothek des Projekts (Seite „CodeBlocks“): Code- und grafische Bausteine.</summary>
public sealed class LibraryViewModel : ObservableObject
{
    private readonly EditorViewModel _editor;
    private LibraryBlockViewModel? _selected;
    private GroupBlockViewModel? _selectedGroup;
    private string? _message;

    public LibraryViewModel(EditorViewModel editor)
    {
        _editor = editor;
        AddCSharpCommand = new RelayCommand(() => Add(CodeLanguage.CSharp));
        AddPythonCommand = new RelayCommand(() => Add(CodeLanguage.Python));
        AddCppCommand = new RelayCommand(() => Add(CodeLanguage.Cpp));
        AddLabViewCommand = new RelayCommand(() => Add(CodeLanguage.LabView));
        DeleteCommand = new RelayCommand(Delete, () => SelectedItem is not null);
        ImportAssemblyCommand = new RelayCommand(() =>
        {
            var path = _editor.Dialogs?.OpenFile(".NET-DLL ins Projekt importieren", ".NET-Bibliothek|*.dll");
            if (path is null) return;
            try { ImportAssembly(path); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException) { Message = ex.Message; }
        });
        RemoveAssemblyCommand = new RelayCommand(() =>
        {
            if (SelectedAssembly is not { } selected) return;
            _editor.Project.Definition.Assemblies.Remove(selected);
            Assemblies.Remove(selected);
            SelectedAssembly = null;
            _editor.MarkChanged();
        });
        editor.ProjectReplaced += Reload;
        editor.LibraryChanged += () =>
        {
            if (Blocks.Count != editor.Library.Count || GroupBlocks.Count != editor.Project.Definition.GroupLibrary.Count) Reload();
            foreach (var b in Blocks) b.OnUsageChanged();
            foreach (var g in GroupBlocks) g.OnUsageChanged();
        };
        Reload();
    }

    public ObservableCollection<LibraryBlockViewModel> Blocks { get; } = [];
    public ObservableCollection<GroupBlockViewModel> GroupBlocks { get; } = [];
    public ObservableCollection<ManagedAssembly> Assemblies { get; } = [];
    public ManagedAssembly? SelectedAssembly { get; set; }
    public RelayCommand ImportAssemblyCommand { get; }
    public RelayCommand RemoveAssemblyCommand { get; }

    public void ImportAssembly(string path)
    {
        var assembly = ManagedAssemblyImport.Read(path);
        var definition = _editor.Project.Definition;
        var previous = definition.Assemblies.FirstOrDefault(a => a.Name.Equals(assembly.Name, StringComparison.OrdinalIgnoreCase));
        if (previous is not null) { definition.Assemblies.Remove(previous); Assemblies.Remove(previous); }
        definition.Assemblies.Add(assembly);
        Assemblies.Add(assembly);
        _editor.MarkChanged();
        Message = "DLL eingebettet: " + assembly.Name;
    }

    /// <summary>Alle Einträge für die Liste (Code und grafisch).</summary>
    public ObservableCollection<object> Items { get; } = [];

    public RelayCommand AddCSharpCommand { get; }
    public RelayCommand AddPythonCommand { get; }
    public RelayCommand AddCppCommand { get; }
    public RelayCommand AddLabViewCommand { get; }
    public RelayCommand DeleteCommand { get; }

    /// <summary>Navigation zum Körper eines grafischen Bausteins (von der Shell gesetzt).</summary>
    public Action<GroupBlockViewModel>? OpenGroupBlock { get; set; }

    public LibraryBlockViewModel? Selected
    {
        get => _selected;
        set
        {
            SetProperty(ref _selected, value);
            if (value is not null) SelectedGroup = null;
            OnPropertyChanged(nameof(SelectedItem));
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }

    public GroupBlockViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            SetProperty(ref _selectedGroup, value);
            if (value is not null) Selected = null;
            OnPropertyChanged(nameof(SelectedItem));
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }

    public object? SelectedItem
    {
        get => (object?)_selected ?? _selectedGroup;
        set
        {
            if (value is LibraryBlockViewModel l) Selected = l;
            else if (value is GroupBlockViewModel g) SelectedGroup = g;
        }
    }

    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    private void Reload()
    {
        var selectedId = Selected?.Id ?? SelectedGroup?.Id;
        Assemblies.Clear();
        foreach (var assembly in _editor.Project.Definition.Assemblies) Assemblies.Add(assembly);
        SelectedAssembly = null;
        Blocks.Clear();
        GroupBlocks.Clear();
        Items.Clear();
        foreach (var b in _editor.Library) Blocks.Add(new LibraryBlockViewModel(_editor, b));
        foreach (var g in _editor.Project.Definition.GroupLibrary)
            GroupBlocks.Add(new GroupBlockViewModel(_editor, g) { OpenInEditor = vm => OpenGroupBlock?.Invoke(vm) });
        foreach (var b in Blocks) Items.Add(b);
        foreach (var g in GroupBlocks) Items.Add(g);
        _selected = null;
        _selectedGroup = null;
        SelectedItem = Items.FirstOrDefault(i => (i as LibraryBlockViewModel)?.Id == selectedId || (i as GroupBlockViewModel)?.Id == selectedId)
                       ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectedGroup));
    }

    public LibraryBlockViewModel Add(CodeLanguage language)
    {
        var name = $"Neuer {BlockCatalog.LanguageText(language)}-Baustein";
        var block = new CodeBlockDefinition(_editor.NewLibraryId(name), name) { Language = language, Source = CodeHelp.Template(language) };
        block.Ports.AddRange(
        [
            new CodePort("Spannung", VarType.Double, PortDirection.Input, "V", "Zu bewertende Spannung", 230.0),
            new CodePort("Minimum", VarType.Double, PortDirection.Input, "V", "Unterer Grenzwert", 220.0),
            new CodePort("Maximum", VarType.Double, PortDirection.Input, "V", "Oberer Grenzwert", 240.0),
            new CodePort("IstWertOK", VarType.Bool, PortDirection.Output, null, "True wenn Wert im Bereich"),
        ]);
        _editor.Library.Add(block);
        _editor.RaiseLibraryChanged();
        Reload();
        Selected = Blocks.First(b => b.Id == block.Id);
        Message = null;
        return Selected;
    }

    private void Delete()
    {
        if (Selected is { } s)
        {
            if (s.UsedIn.Count > 0)
            {
                Message = $"„{s.Name}“ wird noch verwendet: {string.Join(", ", s.UsedIn)}.";
                return;
            }
            _editor.Library.Remove(s.Block);
        }
        else if (SelectedGroup is { } g)
        {
            if (g.UsedIn.Count > 0)
            {
                Message = $"„{g.Name}“ wird noch verwendet: {string.Join(", ", g.UsedIn)}.";
                return;
            }
            _editor.Project.Definition.GroupLibrary.Remove(g.Block);
            _editor.RebuildTree();
        }
        else return;
        _editor.RaiseLibraryChanged();
        Reload();
        Message = null;
    }
}
