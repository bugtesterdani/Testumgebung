using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Scopes;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Curves;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

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

    public IReadOnlyList<string> PythonEnvironments => ["", Testumgebung.Workers.PythonEnvironments.BundledId,
        .. new Testumgebung.Workers.PythonEnvironments().List().Select(item => item.Selection)];
    public string PythonEnvironmentId
    {
        get => _editor.Project.Definition.PythonEnvironmentId ?? "";
        set
        {
            _editor.Project.Definition.PythonEnvironmentId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _editor.MarkChanged();
            OnPropertyChanged();
        }
    }

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

    public IReadOnlyList<string> LimitSetNames => _editor.Project.LimitSets.Keys.ToArray();

    private RelayCommand? _importLimitSets;
    private RelayCommand? _clearLimitSets;

    public RelayCommand ImportLimitSetsCommand => _importLimitSets ??= new(() =>
    {
        var dialogs = _editor.Dialogs;
        var path = dialogs?.OpenFile("Grenzwertsätze importieren", "Grenzwerttabellen (*.csv;*.xlsx)|*.csv;*.xlsx");
        if (path is null) return;
        try
        {
            var sets = LimitSetTable.Import(_editor.Project, path);
            _editor.Project.LimitSets.Clear();
            foreach (var (name, values) in sets) _editor.Project.LimitSets.Add(name, values);
            LimitSetsChanged();
        }
        catch (Exception ex) when (ex is ProjectFormatException or CurveImportException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            dialogs!.ShowError(ex.Message);
        }
    }, () => _editor.Dialogs is not null);

    public RelayCommand ClearLimitSetsCommand => _clearLimitSets ??= new(() =>
    {
        _editor.Project.LimitSets.Clear();
        LimitSetsChanged();
    }, () => _editor.Project.LimitSets.Count > 0);

    private void LimitSetsChanged()
    {
        _editor.MarkChanged();
        OnPropertyChanged(nameof(LimitSetNames));
        _clearLimitSets?.RaiseCanExecuteChanged();
    }

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
            for (var index = 0; index < _editor.Project.SimulationComm.Count; index++) _comm.Add(new CommSimulationRow(_editor, _editor.Project.SimulationComm[index]));
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
        var index = 1;
        while (_editor.Project.SimulationComm.Any(communication => communication.Name == $"Pruefling_Komm{index}")) index++;
        var sim = new Persistence.SimulatedComm($"Pruefling_Komm{index}", $"komm{index}", Testumgebung.Hardware.CommKind.Stream,
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
        _editor.Project.SimulationComm.RemoveAll(communication => communication.Name == row.Name);
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
        => _buses ??= new ObservableCollection<BusDatabaseRow>(_editor.Project.Definition.BusDatabases.Select(database => new BusDatabaseRow(_editor, database)));

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
        _editor.Project.Definition.BusDatabases.RemoveAll(database => database.Name == row.Name);
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
        var name = new string(Path.GetFileNameWithoutExtension(path).Select(character => char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        if (name.Length == 0) name = "Bus";
        _ = BusDatabases; // Zeilen vor der Änderung der Liste aufbauen
        var list = _editor.Project.Definition.BusDatabases;
        var definition = new BusDatabaseDefinition(name, format, text, file);
        var index = list.FindIndex(database => string.Equals(database.Name, name, StringComparison.OrdinalIgnoreCase));
        BusDatabaseRow row;
        if (index >= 0)
        {
            list[index] = definition.WithName(list[index].Name);
            row = new BusDatabaseRow(_editor, list[index]);
            BusDatabases[BusDatabases.IndexOf(BusDatabases.First(busDatabaseRow => string.Equals(busDatabaseRow.Name, name, StringComparison.OrdinalIgnoreCase)))] = row;
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
            var value2 = (value ?? "").Trim();
            var list = editor.Project.Definition.BusDatabases;
            if (value2.Length == 0 || value2 == _def.Name || value2.Contains(' ') || list.Any(database => string.Equals(database.Name, value2, StringComparison.OrdinalIgnoreCase) && !ReferenceEquals(database, _def)))
            {
                OnPropertyChanged(nameof(Name));
                return;
            }
            var old = _def.Name;
            var index = list.FindIndex(database => ReferenceEquals(database, _def));
            _def = _def.WithName(value2);
            if (index >= 0) list[index] = _def;
            // Verweise in Bausteinen mitziehen
            foreach (var step in editor.Project.Definition.AllNodes().OfType<StepDefinition>())
                if (step.Parameters.TryGetValue("Datenbank", out var db) && db.Trim() == old) step.Parameters["Datenbank"] = value2;
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
            ? db.Messages.Select(busMessage =>
                $"{busMessage.Name} (0x{busMessage.Id:X}{(busMessage.Extended ? db.IsJ1939 ? $", PGN 0x{busMessage.Pgn:X4}" : ", 29 Bit" : "")}, {busMessage.Length} Bytes{(busMessage.CycleMs is { } count ? $", {count} ms" : "")}{(db.Format == BusDatabaseFormat.Ldf ? ", " + busMessage.Sender : "")}): "
                + string.Join(", ", busMessage.Signals.Select(busSignal => string.IsNullOrEmpty(busSignal.Unit) ? busSignal.Name : $"{busSignal.Name} [{busSignal.Unit}]"))).ToList()
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
        var index = list.FindIndex(communication => communication.Name == _sim.Name);
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
            var value2 = (value ?? "").Trim();
            if (value2.Length == 0 || value2.Contains(' ') || editor.Project.SimulationComm.Any(communication => communication.Name == value2 && !ReferenceEquals(communication, _sim)))
            {
                OnPropertyChanged(nameof(Name)); // abgelehnte Eingabe zurücksetzen
                return;
            }
            Replace(_sim with { Name = value2 });
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
