using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public abstract class PropertiesViewModel : ObservableObject
{
    public abstract string Title { get; }
}

/// <summary>Bearbeitbare Variablendefinition einer Gruppe.</summary>
public sealed class VariableRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private string _name;
    private VarType _type;
    private string _initial;
    private string? _error;

    public VariableRowViewModel(VariableDefinition definition, Action changed)
    {
        _changed = changed;
        _name = definition.Name;
        _type = definition.Type;
        _initial = definition.Initial switch
        {
            null => "",
            string s => s,
            var v => TypedValue.Format(v),
        };
    }

    public static IReadOnlyList<VarType> Types { get; } = Enum.GetValues<VarType>();

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value.Trim())) _changed(); }
    }

    public VarType Type
    {
        get => _type;
        set { if (SetProperty(ref _type, value)) _changed(); }
    }

    public string Initial
    {
        get => _initial;
        set { if (SetProperty(ref _initial, value)) _changed(); }
    }

    public string? Error
    {
        get => _error;
        set => SetProperty(ref _error, value);
    }

    public VariableDefinition? ToDefinition()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Contains('/') || Name.Contains(' '))
        {
            Error = "Ungültiger Name";
            return null;
        }
        try
        {
            var initial = string.IsNullOrEmpty(Initial) ? null : TypedValue.Parse(Initial, Type);
            Error = null;
            return new VariableDefinition(Name, Type, initial);
        }
        catch (TypeMismatchException ex)
        {
            Error = ex.Message;
            return null;
        }
    }
}

/// <summary>Projekt-Eigenschaften und Variablen des Hauptablaufs (gemeinsame Auftragsdaten).</summary>
public sealed class ProjectPropertiesViewModel : PropertiesViewModel
{
    private readonly EditorViewModel _editor;

    public ProjectPropertiesViewModel(EditorViewModel editor)
    {
        _editor = editor;
        Variables = new VariableListViewModel(editor.Project.Definition.Root, editor.MarkChanged);
    }

    public override string Title => "Projekt";

    public string Name => _editor.Project.Definition.Name;
    public string Version => _editor.Project.Definition.Version;

    public string Description
    {
        get => _editor.Project.Description ?? "";
        set
        {
            _editor.Project.Description = value;
            _editor.MarkChanged();
            OnPropertyChanged();
        }
    }

    public VariableListViewModel Variables { get; }

    // ------------------------------------------------------------------ Kommunikations-Simulation

    private ObservableCollection<CommSimulationRow>? _comm;
    private CommSimulationRow? _selectedComm;

    /// <summary>Simulierte Prüflingsschnittstellen (Byte-Strom, Modbus, CAN) des Projekts.</summary>
    public ObservableCollection<CommSimulationRow> CommSimulations
    {
        get
        {
            if (_comm is not null) return _comm;
            _comm = [];
            for (var i = 0; i < _editor.Project.SimulationComm.Count; i++) _comm.Add(new CommSimulationRow(_editor, _editor.Project.SimulationComm[i]));
            return _comm;
        }
    }

    public IReadOnlyList<Testumgebung.Hardware.CommKind> CommKinds { get; } =
        [Testumgebung.Hardware.CommKind.Stream, Testumgebung.Hardware.CommKind.Modbus, Testumgebung.Hardware.CommKind.Can, Testumgebung.Hardware.CommKind.Lin, Testumgebung.Hardware.CommKind.DoIp, Testumgebung.Hardware.CommKind.OpcUa];

    public CommSimulationRow? SelectedComm
    {
        get => _selectedComm;
        set => SetProperty(ref _selectedComm, value);
    }

    private RelayCommand? _addComm;
    private RelayCommand? _removeComm;

    public RelayCommand AddCommCommand => _addComm ??= new(() =>
    {
        var n = 1;
        while (_editor.Project.SimulationComm.Any(c => c.Name == $"Pruefling_Komm{n}")) n++;
        var sim = new Persistence.SimulatedComm($"Pruefling_Komm{n}", $"komm{n}", Testumgebung.Hardware.CommKind.Stream,
            "# MUSTER => ANTWORT (regulärer Ausdruck, $1 = Gruppe, {zufall:min:max:dez})\n\\*IDN\\? => ACME,PRUEFLING,0001,1.0");
        _editor.Project.SimulationComm.Add(sim);
        var row = new CommSimulationRow(_editor, sim);
        CommSimulations.Add(row);
        SelectedComm = row;
        _editor.MarkChanged();
    });

    public RelayCommand RemoveCommCommand => _removeComm ??= new(() =>
    {
        if (_selectedComm is not { } row) return;
        _editor.Project.SimulationComm.RemoveAll(c => c.Name == row.Name);
        CommSimulations.Remove(row);
        SelectedComm = null;
        _editor.MarkChanged();
    });

    // ------------------------------------------------------------------ Busdatenbanken (DBC, LDF)

    public const string BusFileFilter = "Busbeschreibung (*.dbc;*.ldf)|*.dbc;*.ldf|CAN-Datenbank (*.dbc)|*.dbc|LIN Description File (*.ldf)|*.ldf|Alle Dateien (*.*)|*.*";

    private ObservableCollection<BusDatabaseRow>? _buses;
    private BusDatabaseRow? _selectedBus;
    private string? _busStatus;
    private RelayCommand? _importBus;
    private RelayCommand? _removeBus;

    /// <summary>Importierte DBC-/LDF-Dateien (im Projekt gespeichert, Teil des Release-Pakets).</summary>
    public ObservableCollection<BusDatabaseRow> BusDatabases
        => _buses ??= new ObservableCollection<BusDatabaseRow>(_editor.Project.Definition.BusDatabases.Select(d => new BusDatabaseRow(_editor, d)));

    public BusDatabaseRow? SelectedBus
    {
        get => _selectedBus;
        set => SetProperty(ref _selectedBus, value);
    }

    public string? BusStatus
    {
        get => _busStatus;
        private set => SetProperty(ref _busStatus, value);
    }

    public RelayCommand ImportBusCommand => _importBus ??= new(() =>
    {
        if (_editor.Dialogs?.OpenFile("Busbeschreibung importieren", BusFileFilter) is { } path) ImportBusDatabase(path);
    });

    public RelayCommand RemoveBusCommand => _removeBus ??= new(() =>
    {
        if (_selectedBus is not { } row) return;
        _editor.Project.Definition.BusDatabases.RemoveAll(d => d.Name == row.Name);
        BusDatabases.Remove(row);
        SelectedBus = null;
        BusStatus = $"„{row.Name}“ entfernt.";
        _editor.MarkStructureChanged();
    });

    /// <summary>DBC/LDF einlesen und prüfen; gleicher Name ersetzt den bisherigen Stand (neue Version der Datei).</summary>
    public BusDatabaseRow? ImportBusDatabase(string path)
    {
        string text;
        try
        {
            var bytes = File.ReadAllBytes(path);
            try
            {
                text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                text = System.Text.Encoding.Latin1.GetString(bytes); // ältere DBC-Dateien (Windows-1252)
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BusStatus = "Datei nicht lesbar: " + ex.Message;
            return null;
        }
        var file = Path.GetFileName(path);
        var format = BusDatabase.DetectFormat(file, text);
        BusDatabase parsed;
        try
        {
            parsed = BusDatabase.Parse(text, format);
        }
        catch (BusDatabaseException ex)
        {
            BusStatus = $"Import abgelehnt: {ex.Message}";
            return null;
        }
        var name = new string(Path.GetFileNameWithoutExtension(path).Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        if (name.Length == 0) name = "Bus";
        _ = BusDatabases; // Zeilen vor der Änderung der Liste aufbauen
        var list = _editor.Project.Definition.BusDatabases;
        var definition = new BusDatabaseDefinition(name, format, text, file);
        var index = list.FindIndex(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        BusDatabaseRow row;
        if (index >= 0)
        {
            list[index] = definition.WithName(list[index].Name);
            row = new BusDatabaseRow(_editor, list[index]);
            BusDatabases[BusDatabases.IndexOf(BusDatabases.First(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))] = row;
            BusStatus = $"„{row.Name}“ aktualisiert: {parsed.Summary}.";
        }
        else
        {
            list.Add(definition);
            row = new BusDatabaseRow(_editor, definition);
            BusDatabases.Add(row);
            BusStatus = $"„{name}“ importiert: {parsed.Summary}.";
        }
        if (parsed.Warnings.Count > 0) BusStatus += $" {parsed.Warnings.Count} Hinweis(e).";
        SelectedBus = row;
        _editor.MarkStructureChanged();
        _editor.Validate();
        return row;
    }

    // ------------------------------------------------------------------ Werkeransicht (Abschnitt 4)

    private OperatorLayout Layout => _editor.Project.Definition.Layout;

    public IReadOnlyList<ProgressPosition> ProgressPositions { get; } = [ProgressPosition.Top, ProgressPosition.Left, ProgressPosition.Bottom];

    /// <summary>Position „Aktueller Schritt und Fortschritt“: oben, links (Ablaufübersicht) oder unten.</summary>
    public ProgressPosition ProgressPosition
    {
        get => Layout.ProgressPosition;
        set => SetLayout(() => Layout.ProgressPosition = value, Layout.ProgressPosition != value);
    }

    public bool ShowOverview
    {
        get => Layout.ShowOverview;
        set => SetLayout(() => Layout.ShowOverview = value, Layout.ShowOverview != value);
    }

    public bool ShowProtocol
    {
        get => Layout.ShowProtocol;
        set => SetLayout(() => Layout.ShowProtocol = value, Layout.ShowProtocol != value);
    }

    public bool ShowAllMeasurements
    {
        get => Layout.ShowAllMeasurements;
        set => SetLayout(() => Layout.ShowAllMeasurements = value, Layout.ShowAllMeasurements != value);
    }

    public string LayoutHint => "Einzelne Schritte und Messwerte blenden Sie am Schritt bzw. an der Gruppe über den Filter „Anzeigen (am Prüfplatz)“ aus. "
        + "Ohne Ablaufübersicht steht der Fortschritt oben, wenn „links“ gewählt ist.";

    private void SetLayout(Action apply, bool changed, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!changed) return;
        apply();
        _editor.MarkChanged();
        _editor.RaiseLayoutChanged();
        OnPropertyChanged(name);
    }
}

/// <summary>Eine importierte Busdatenbank: Name (in Bausteinen verwendet), Inhalt zur Übersicht.</summary>
public sealed class BusDatabaseRow(EditorViewModel editor, BusDatabaseDefinition initial) : ObservableObject
{
    private BusDatabaseDefinition _def = initial;

    public string Name
    {
        get => _def.Name;
        set
        {
            var v = (value ?? "").Trim();
            var list = editor.Project.Definition.BusDatabases;
            if (v.Length == 0 || v == _def.Name || v.Contains(' ') || list.Any(d => string.Equals(d.Name, v, StringComparison.OrdinalIgnoreCase) && !ReferenceEquals(d, _def)))
            {
                OnPropertyChanged(nameof(Name));
                return;
            }
            var old = _def.Name;
            var index = list.FindIndex(d => ReferenceEquals(d, _def));
            _def = _def.WithName(v);
            if (index >= 0) list[index] = _def;
            // Verweise in Bausteinen mitziehen
            foreach (var step in editor.Project.Definition.AllNodes().OfType<StepDefinition>())
                if (step.Parameters.TryGetValue("Datenbank", out var db) && db.Trim() == old) step.Parameters["Datenbank"] = v;
            editor.MarkStructureChanged();
            editor.Validate();
            OnPropertyChanged(nameof(Name));
        }
    }

    public string Format => _def.Format == BusDatabaseFormat.Ldf ? "LDF (LIN)" : "DBC (CAN)";
    public string FileName => _def.FileName ?? "";

    private BusDatabase? Parsed
    {
        get
        {
            try
            {
                return _def.Parsed;
            }
            catch (BusDatabaseException)
            {
                return null;
            }
        }
    }

    public string Summary
    {
        get
        {
            try
            {
                return _def.Parsed.Summary;
            }
            catch (BusDatabaseException ex)
            {
                return "Fehler: " + ex.Message;
            }
        }
    }

    public IReadOnlyList<string> Warnings => Parsed?.Warnings ?? [];

    /// <summary>Botschaften mit Signalen (Übersicht zum Nachschlagen der Namen für die Bausteine).</summary>
    public IReadOnlyList<string> Overview
        => Parsed is { } db
            ? db.Messages.Select(m =>
                $"{m.Name} (0x{m.Id:X}{(m.Extended ? db.IsJ1939 ? $", PGN 0x{m.Pgn:X4}" : ", 29 Bit" : "")}, {m.Length} Bytes{(m.CycleMs is { } c ? $", {c} ms" : "")}{(db.Format == BusDatabaseFormat.Ldf ? ", " + m.Sender : "")}): "
                + string.Join(", ", m.Signals.Select(s => string.IsNullOrEmpty(s.Unit) ? s.Name : $"{s.Name} [{s.Unit}]"))).ToList()
            : [];
}

/// <summary>Eine simulierte Prüflingsschnittstelle (bearbeitet den Eintrag im Projekt).</summary>
public sealed class CommSimulationRow(EditorViewModel editor, Persistence.SimulatedComm initial) : ObservableObject
{
    private Persistence.SimulatedComm _sim = initial;

    private void Replace(Persistence.SimulatedComm next, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (next == _sim) return;
        var list = editor.Project.SimulationComm;
        var index = list.FindIndex(c => c.Name == _sim.Name);
        if (index >= 0) list[index] = next;
        _sim = next;
        editor.MarkChanged();
        OnPropertiesChanged(name!, nameof(Problems), nameof(HasProblems), nameof(Help));
    }

    public string Name
    {
        get => _sim.Name;
        set
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0 || v.Contains(' ') || editor.Project.SimulationComm.Any(c => c.Name == v && !ReferenceEquals(c, _sim)))
            {
                OnPropertyChanged(nameof(Name)); // abgelehnte Eingabe zurücksetzen
                return;
            }
            Replace(_sim with { Name = v });
        }
    }

    public string Channel
    {
        get => _sim.Channel;
        set => Replace(_sim with { Channel = string.IsNullOrWhiteSpace(value) ? _sim.Channel : value.Trim() });
    }

    public Testumgebung.Hardware.CommKind Kind
    {
        get => _sim.Kind;
        set => Replace(_sim with { Kind = value });
    }

    public string Rules
    {
        get => _sim.Rules;
        set => Replace(_sim with { Rules = value ?? "" });
    }

    public bool RequiresLock
    {
        get => _sim.RequiresLock;
        set => Replace(_sim with { RequiresLock = value });
    }

    public IReadOnlyList<Testumgebung.Hardware.CommKind> Kinds { get; } =
        [Testumgebung.Hardware.CommKind.Stream, Testumgebung.Hardware.CommKind.Modbus, Testumgebung.Hardware.CommKind.Can, Testumgebung.Hardware.CommKind.Lin, Testumgebung.Hardware.CommKind.DoIp, Testumgebung.Hardware.CommKind.OpcUa];

    public IReadOnlyList<string> Problems => Testumgebung.Hardware.CommSimulation.Validate(_sim.Kind, _sim.Rules);
    public bool HasProblems => Problems.Count > 0;

    public string Help => _sim.Kind switch
    {
        Testumgebung.Hardware.CommKind.Stream => "Je Zeile MUSTER => ANTWORT. MUSTER: regulärer Ausdruck über die Anfrage ohne Zeilenende; ANTWORT mit \\r \\n \\xHH, $1…$9, {zufall:min:max:dez}. Binär: hex: 01 03 ?? => hex: 01 03 02 00 64. antwortende=\\r\\n. Keine Regel = Prüfling schweigt.",
        Testumgebung.Hardware.CommKind.Modbus => "Je Zeile ADRESSE[:TYP]=WERT, z. B. HR0=1234, IR10:float32=230±2, C3=1, HR30:text=FW1.2; wortfolge=niedrig; alle=0 (sonst Ausnahme 02 für fehlende Adressen).",
        Testumgebung.Hardware.CommKind.Lin => "Je Zeile ID => DATEN (Slave-Antwort), z. B. 0x21 => 01 02 03 04; ID => @ID2 antwortet mit den zuletzt vom Master auf ID2 gesendeten Daten. Ohne Regel antwortet kein Slave.",
        Testumgebung.Hardware.CommKind.DoIp => "DoIP-Steuergerät: doip 0x1000 (logische Adresse), routing=abgelehnt (optional), dann uds: ANFRAGE => ANTWORT wie bei CAN.",
        Testumgebung.Hardware.CommKind.OpcUa => "OPC-UA-Server: je Zeile ns=2;s=Name = Wert (Startwert, Zahl/true/false/\"Text\"); Methoden: methode ns=2;s=Obj ns=2;s=M => summe|spiegeln|konstante:V.",
        _ => "Antwort: 0x7E0 02 01 0C => 0x7E8 04 41 0C $3 F8 (** = beliebig, $n = n-tes Anfragebyte, 8 Hexziffern bzw. x = 29 Bit, fd nach der ID); mehrere Antwortrahmen mit ; trennen. zyklisch 0x100 01 02 03. UDS: uds 0x7E0 0x7E8, dann uds: 22 F1 90 => 62 F1 90 \"TEXT\" (ISO-TP automatisch; 7F 31 78; 71 … = „Antwort folgt“).",
    };
}

/// <summary>Variablenliste mit Hinzufügen/Entfernen, schreibt in die Gruppendefinition.</summary>
public sealed class VariableListViewModel : ObservableObject
{
    private readonly GroupDefinition _group;
    private readonly Action _changed;

    public VariableListViewModel(GroupDefinition group, Action changed)
    {
        _group = group;
        _changed = changed;
        foreach (var v in group.Variables) Rows.Add(new VariableRowViewModel(v, Apply));
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(p => Remove(p as VariableRowViewModel));
    }

    public ObservableCollection<VariableRowViewModel> Rows { get; } = [];
    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public void Add()
    {
        var n = 1;
        while (Rows.Any(r => r.Name == $"Variable{n}")) n++;
        Rows.Add(new VariableRowViewModel(new VariableDefinition($"Variable{n}", VarType.Double, 0.0), Apply));
        Apply();
    }

    public void Remove(VariableRowViewModel? row)
    {
        if (row is null) return;
        Rows.Remove(row);
        Apply();
    }

    private void Apply()
    {
        var defs = Rows.Select(r => r.ToDefinition()).ToList();
        foreach (var dup in Rows.GroupBy(r => r.Name).Where(g => g.Count() > 1).SelectMany(g => g))
            dup.Error = "Name doppelt";
        if (defs.Any(d => d is null) || Rows.Any(r => r.Error is not null)) return;
        _group.Variables.Clear();
        _group.Variables.AddRange(defs!);
        _changed();
    }
}

/// <summary>Eigenschaften einer Gruppe (Abbildung 9: Bezeichnung, Bedingung, Wiederholung, Exit, Variablen).</summary>
public sealed class GroupPropertiesViewModel : PropertiesViewModel
{
    private readonly EditorViewModel _editor;
    private readonly NodeViewModel _node;
    private string _conditionText;
    private string? _conditionError;
    private string _repeatExpression;
    private string? _repeatError;
    private string _resourcesText;

    public GroupPropertiesViewModel(EditorViewModel editor, NodeViewModel node)
    {
        _editor = editor;
        _node = node;
        _conditionText = Group.EntryCondition ?? "";
        _repeatExpression = Group.Repetition.Expression ?? "";
        _resourcesText = string.Join(", ", Group.Resources);
        Variables = new VariableListViewModel(Group, Changed);
        CreateBlockCommand = new RelayCommand(() => editor.CreateGroupBlock(_node), () => CanCreateBlock);
        Storage = new StorageSettingsViewModel(editor, node.Definition);
    }

    /// <summary>Filter Anzeige/Datei/Datenbank (Gruppendefault).</summary>
    public StorageSettingsViewModel Storage { get; }

    private GroupDefinition Group => _node.Group!;

    /// <summary>„Aus Gruppe erstellen“: wiederverwendbarer grafischer Baustein.</summary>
    public RelayCommand CreateBlockCommand { get; }

    public bool CanCreateBlock => _editor.CanCreateGroupBlock(_node);

    public override string Title => _node.IsExit ? "Exit-Gruppe" : "Gruppe";

    public static IReadOnlyList<RepeatMode> RepeatModes { get; } = Enum.GetValues<RepeatMode>();
    public static IReadOnlyList<RepeatResultPolicy> ResultPolicies { get; } = Enum.GetValues<RepeatResultPolicy>();

    public bool IsSection => _node.IsSection;

    public string Name
    {
        get => _node.Name;
        set { _node.Name = value; Changed(); OnPropertyChanged(); }
    }

    public string Description
    {
        get => Group.Description ?? "";
        set { Group.Description = value; Changed(); OnPropertyChanged(); }
    }

    public string EntryCondition
    {
        get => _conditionText;
        set
        {
            SetProperty(ref _conditionText, value);
            try
            {
                Group.EntryCondition = string.IsNullOrWhiteSpace(value) ? null : value;
                ConditionError = null;
                Changed();
            }
            catch (ExpressionException ex)
            {
                ConditionError = ex.Message;
            }
        }
    }

    public string? ConditionError
    {
        get => _conditionError;
        private set => SetProperty(ref _conditionError, value);
    }

    public RepeatMode RepeatMode
    {
        get => Group.Repetition.Mode;
        set
        {
            Group.Repetition = Group.Repetition with { Mode = value, Count = Math.Max(1, Group.Repetition.Count) };
            if (value is RepeatMode.While or RepeatMode.Until) RepeatExpression = _repeatExpression;
            if (value == RepeatMode.Duration && Group.Repetition.Duration <= TimeSpan.Zero) DurationSeconds = 10;
            Changed();
            OnPropertiesChanged(nameof(RepeatMode), nameof(ShowCount), nameof(ShowDuration), nameof(ShowExpression));
        }
    }

    public bool ShowCount => RepeatMode == RepeatMode.Count;
    public bool ShowDuration => RepeatMode == RepeatMode.Duration;
    public bool ShowExpression => RepeatMode is RepeatMode.While or RepeatMode.Until;

    public int Count
    {
        get => Group.Repetition.Count;
        set
        {
            Group.Repetition = Group.Repetition with { Count = Math.Max(1, value) };
            Changed();
            OnPropertyChanged();
        }
    }

    /// <summary>Zeitspanne auch innerhalb einer Iteration prüfen (restliche Schritte überspringen).</summary>
    public bool StopWithinIteration
    {
        get => Group.Repetition.StopWithinIteration;
        set
        {
            if (Group.Repetition.StopWithinIteration == value) return;
            Group.Repetition = Group.Repetition with { StopWithinIteration = value };
            _editor.MarkChanged();
            OnPropertyChanged();
        }
    }

    public double DurationSeconds
    {
        get => Group.Repetition.Duration.TotalSeconds;
        set
        {
            Group.Repetition = Group.Repetition with { Duration = TimeSpan.FromSeconds(Math.Max(0.001, value)) };
            Changed();
            OnPropertyChanged();
        }
    }

    public string RepeatExpression
    {
        get => _repeatExpression;
        set
        {
            SetProperty(ref _repeatExpression, value);
            try
            {
                if (string.IsNullOrWhiteSpace(value)) throw new ExpressionException("Ausdruck erforderlich.");
                Expression.Parse(value);
                Group.Repetition = Group.Repetition with { Expression = value };
                RepeatError = null;
                Changed();
            }
            catch (ExpressionException ex)
            {
                RepeatError = ex.Message;
            }
        }
    }

    public string? RepeatError
    {
        get => _repeatError;
        private set => SetProperty(ref _repeatError, value);
    }

    public RepeatResultPolicy ResultBeforeRepeat
    {
        get => Group.ResultBeforeRepeat;
        set { Group.ResultBeforeRepeat = value; Changed(); OnPropertyChanged(); }
    }

    public bool HasExitGroup
    {
        get => Group.ExitGroup is not null;
        set
        {
            if (value == HasExitGroup || _node.IsExit) return;
            if (value) Group.ExitGroup = new GroupDefinition(_editor.NewId("exit"), "Exit – " + Group.Name);
            else Group.ExitGroup = null;
            _editor.RebuildTree(selectId: _node.Id);
            OnPropertyChanged();
        }
    }

    public bool CanHaveExit => !_node.IsExit && _node.Definition != _editor.Project.Definition.Finalization;

    public string Resources
    {
        get => _resourcesText;
        set
        {
            SetProperty(ref _resourcesText, value);
            Group.Resources.Clear();
            Group.Resources.AddRange(value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct());
            Changed();
        }
    }

    public double? ResourceTimeoutSeconds
    {
        get => Group.ResourceTimeout?.TotalSeconds;
        set
        {
            Group.ResourceTimeout = value is > 0 ? TimeSpan.FromSeconds(value.Value) : null;
            Changed();
            OnPropertyChanged();
        }
    }

    public bool IsDutRun
    {
        get => Group.IsDutRun;
        set { Group.IsDutRun = value; Changed(); OnPropertyChanged(); }
    }

    public string DutIdVariable
    {
        get => Group.DutIdVariable;
        set { Group.DutIdVariable = value; Changed(); OnPropertyChanged(); }
    }

    public VariableListViewModel Variables { get; }

    private void Changed()
    {
        _node.Refresh();
        _editor.MarkChanged();
    }
}

/// <summary>Ein Parameter eines Katalogbausteins.</summary>
public sealed class ParameterRowViewModel : ObservableObject
{
    private readonly StepPropertiesViewModel _owner;

    public ParameterRowViewModel(StepPropertiesViewModel owner, ParameterDescriptor descriptor)
    {
        _owner = owner;
        Descriptor = descriptor;
    }

    public ParameterDescriptor Descriptor { get; }
    public string Label => Descriptor.Label;
    public string? Hint => Descriptor.Hint;
    public ParameterKind Kind => Descriptor.Kind;
    public IReadOnlyList<string> Choices => Descriptor.Choices ?? [];
    public bool IsChoice => Kind == ParameterKind.Choice;
    public bool IsBool => Kind == ParameterKind.Bool;
    public bool IsCode => Kind == ParameterKind.Code;
    public bool IsText => !IsChoice && !IsBool && !IsCode && !IsHidden;
    public bool IsMultiline => Descriptor.Name is "Verbindungsoptionen" or "Header" or "Body";

    /// <summary>Wird durch eine eigene Oberfläche ersetzt (z. B. Bausteinauswahl).</summary>
    public bool IsHidden { get; init; }
    public bool IsExpression => Kind is ParameterKind.Expression;

    public string Value
    {
        get => _owner.Step.Parameters.TryGetValue(Descriptor.Name, out var v) ? v : Descriptor.Default;
        set
        {
            if (Value == value) return;
            _owner.Step.Parameters[Descriptor.Name] = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BoolValue));
            _owner.ParametersChanged();
        }
    }

    public bool BoolValue
    {
        get => Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }
}

/// <summary>Eigenschaften eines Schritts: Name, Beschreibung und die Bausteinparameter.</summary>
public sealed class StepPropertiesViewModel : PropertiesViewModel
{
    private readonly EditorViewModel _editor;
    private readonly NodeViewModel _node;
    private IReadOnlyList<string> _diagnostics = [];

    public StepPropertiesViewModel(EditorViewModel editor, NodeViewModel node)
    {
        _editor = editor;
        _node = node;
        if (Step.BlockType is { } t && editor.Catalog.TryGet(t, out var d))
        {
            Block = d;
            foreach (var p in d.Parameters)
            {
                var hidden = t is BlockCatalog.LibraryBlock or BlockCatalog.GroupBlock && p.Name == "Baustein";
                Parameters.Add(new ParameterRowViewModel(this, p) { IsHidden = hidden });
            }
        }
        if (Step.BlockType == BlockCatalog.Code) CodeTools = new CodeToolsViewModel(editor, node);
        if (Step.BlockType is BlockCatalog.LibraryBlock or BlockCatalog.GroupBlock) Bindings = new LibraryBindingsViewModel(editor, this);
        _diagnostics = editor.DiagnosticsFor(Step);
        Storage = new StorageSettingsViewModel(editor, node.Definition);
    }

    /// <summary>Filter Anzeige/Datei/Datenbank (Override).</summary>
    public StorageSettingsViewModel Storage { get; }

    /// <summary>Werkzeuge für CodeBlocks im Ablauf (Build prüfen, Variablen, Umwandeln).</summary>
    public CodeToolsViewModel? CodeTools { get; }

    /// <summary>Bausteinauswahl und Ein-/Ausgangsbindungen eines Bibliotheksbausteins.</summary>
    public LibraryBindingsViewModel? Bindings { get; }

    public StepDefinition Step => _node.Step!;
    public BlockDescriptor? Block { get; }
    public override string Title => Block?.DisplayName ?? "Schritt (Code)";
    public string BlockDescription => Block?.Description ?? "Im Code definierter Schritt – nicht im Editor konfigurierbar.";

    public ObservableCollection<ParameterRowViewModel> Parameters { get; } = [];

    public string Name
    {
        get => _node.Name;
        set { _node.Name = value; _editor.MarkChanged(); OnPropertyChanged(); }
    }

    public string Description
    {
        get => Step.Description ?? "";
        set { Step.Description = value; _editor.MarkChanged(); OnPropertyChanged(); }
    }

    public IReadOnlyList<string> Diagnostics
    {
        get => _diagnostics;
        private set
        {
            _diagnostics = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDiagnostics));
        }
    }

    public bool HasDiagnostics => _diagnostics.Count > 0;

    internal void ParametersChanged()
    {
        Diagnostics = _editor.DiagnosticsFor(Step);
        _node.Diagnostic = Diagnostics.FirstOrDefault();
        _node.Refresh();
        _editor.MarkChanged();
    }
}

internal static class Format
{
    public static string Number(double? value, string format = "0.###")
        => value?.ToString(format, CultureInfo.GetCultureInfo("de-DE")) ?? "";
}
