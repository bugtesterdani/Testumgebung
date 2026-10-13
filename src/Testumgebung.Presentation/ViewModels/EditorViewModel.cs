using System.Collections.ObjectModel;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Engine;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed record PaletteItem(string? BlockType, string DisplayName, string Description, NodeIcon Icon);

public sealed record PaletteCategory(string Name, IReadOnlyList<PaletteItem> Items);

/// <summary>
/// Ablaufeditor (Abschnitt 2.1): hierarchische, eingerückte Liste grafischer Karten.
/// Keine freie Positionierung; Einfügen/Verschieben nur an festen Positionen.
/// </summary>
public sealed partial class EditorViewModel : ObservableObject
{
    private readonly ProjectSerializer _serializer;
    private readonly IFileDialogService? _dialogs;
    private NodeViewModel? _selected;
    private PropertiesViewModel? _properties;
    private bool _isDirty;
    private DateTime _changedAt = DateTime.Now;
    private string? _filePath;
    private DebugController? _breakpoints;
    private int _idCounter;

    public EditorViewModel(TestProject project, BlockCatalog? catalog = null, ProjectSerializer? serializer = null, IFileDialogService? dialogs = null)
    {
        Catalog = catalog ?? BlockCatalog.Default;
        _serializer = serializer ?? new ProjectSerializer(Catalog);
        _dialogs = dialogs;
        Project = project;

        Palette = Catalog.All
            .GroupBy(b => b.Category)
            .Select(g => new PaletteCategory(g.Key, g.Select(b => new PaletteItem(b.Type, b.DisplayName, b.Description, IconFor(b.Type))).ToList()))
            .Append(new PaletteCategory("Struktur", [new PaletteItem(null, "Gruppe", "Untergruppe mit Variablen, Bedingung, Wiederholung und Exit-Gruppe.", NodeIcon.Group)]))
            .ToList();

        InsertCommand = new RelayCommand(p => { if (p is PaletteItem item) Insert(item); });
        DeleteCommand = new RelayCommand(() => Delete(SelectedNode), () => CanDelete(SelectedNode));
        MoveUpCommand = new RelayCommand(() => MoveBy(SelectedNode, -1), () => SelectedNode?.CanMove == true);
        MoveDownCommand = new RelayCommand(() => MoveBy(SelectedNode, +1), () => SelectedNode?.CanMove == true);
        IndentCommand = new RelayCommand(() => Indent(SelectedNode), () => SelectedNode?.CanMove == true);
        OutdentCommand = new RelayCommand(() => Outdent(SelectedNode), () => SelectedNode?.CanMove == true);
        ToggleBreakpointCommand = new RelayCommand(p => ToggleBreakpoint(p as NodeViewModel ?? SelectedNode), _ => _breakpoints is not null);
        ShowProjectCommand = new RelayCommand(() => SelectedNode = null);
        NewCommand = new RelayCommand(NewProject);
        OpenCommand = new RelayCommand(Open, () => _dialogs is not null);
        SaveCommand = new RelayCommand(() => Save(FilePath), () => _dialogs is not null || FilePath is not null);
        SaveAsCommand = new RelayCommand(() => Save(null), () => _dialogs is not null);
        UndoCommand = new RelayCommand(Undo, () => _undo.Count > 0);
        RedoCommand = new RelayCommand(Redo, () => _redo.Count > 0);
        CopyCommand = new RelayCommand(() => Copy(SelectedNode), () => SelectedNode is { IsSection: false });
        CutCommand = new RelayCommand(() => Cut(SelectedNode), () => CanDelete(SelectedNode));
        PasteCommand = new RelayCommand(() => Paste(), () => HasClipboard);
        DuplicateCommand = new RelayCommand(() => Duplicate(SelectedNode), () => SelectedNode is { IsSection: false, IsExit: false });
        FindNextCommand = new RelayCommand(() => FindNext(+1));
        FindPreviousCommand = new RelayCommand(() => FindNext(-1));

        RebuildTree();
        ResetHistory();
    }

    public BlockCatalog Catalog { get; }

    /// <summary>Dateidialoge und externes Öffnen (in Tests ersetzt).</summary>
    public IFileDialogService? Dialogs => _dialogs;

    /// <summary>Worker für „Build prüfen“ und Testausführung (von der Shell gesetzt).</summary>
    public ICodeRuntime? CodeRuntime { get; set; }

    /// <summary>Bibliothek wiederverwendbarer Codebausteine des Projekts.</summary>
    public List<CodeBlockDefinition> Library => Project.Definition.Library;
    public TestProject Project { get; private set; }
    public ObservableCollection<NodeViewModel> Nodes { get; } = [];
    public IReadOnlyList<PaletteCategory> Palette { get; }
    public ObservableCollection<string> Diagnostics { get; } = [];

    public RelayCommand InsertCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand IndentCommand { get; }
    public RelayCommand OutdentCommand { get; }
    public RelayCommand ToggleBreakpointCommand { get; }
    public RelayCommand ShowProjectCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }

    /// <summary>Projekt wurde ersetzt (Neu/Öffnen).</summary>
    public event Action? ProjectReplaced;

    /// <summary>Layout der Werkeransicht geändert (ruhende Prüfplätze übernehmen es sofort).</summary>
    public event Action? LayoutChanged;

    internal void RaiseLayoutChanged() => LayoutChanged?.Invoke();

    /// <summary>Definition geändert (für Anzeige „Geändert …“).</summary>
    public event Action? Changed;

    public string ProjectName => Project.Definition.Name;
    public string Version => Project.Definition.Version;

    public string? FilePath
    {
        get => _filePath;
        private set
        {
            SetProperty(ref _filePath, value);
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public DateTime ChangedAt
    {
        get => _changedAt;
        private set
        {
            SetProperty(ref _changedAt, value);
            OnPropertyChanged(nameof(ChangedText));
        }
    }

    public string ChangedText => ChangedAt.Date == DateTime.Today
        ? $"Geändert heute, {ChangedAt:HH:mm}"
        : $"Geändert {ChangedAt:dd.MM.yyyy HH:mm}";

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public NodeViewModel? SelectedNode
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            if (_selected is not null) _selected.IsSelected = false;
            _selected = value;
            if (_selected is not null) _selected.IsSelected = true;
            OnPropertyChanged();
            Properties = _selected switch
            {
                null => new ProjectPropertiesViewModel(this),
                { IsGroup: true } n => new GroupPropertiesViewModel(this, n),
                var n => new StepPropertiesViewModel(this, n),
            };
            foreach (var c in new[] { DeleteCommand, MoveUpCommand, MoveDownCommand, IndentCommand, OutdentCommand, ToggleBreakpointCommand, CopyCommand, CutCommand, DuplicateCommand })
                c.RaiseCanExecuteChanged();
        }
    }

    public PropertiesViewModel? Properties
    {
        get => _properties;
        private set => SetProperty(ref _properties, value);
    }

    /// <summary>Haltepunkte des gewählten Prüfplatzes (Entwickleransicht).</summary>
    public DebugController? Breakpoints
    {
        get => _breakpoints;
        set
        {
            _breakpoints = value;
            foreach (var n in AllNodes()) n.HasBreakpoint = value?.HasBreakpoint(n.Id) ?? false;
            ToggleBreakpointCommand.RaiseCanExecuteChanged();
        }
    }

    public IEnumerable<NodeViewModel> AllNodes() => Nodes.SelectMany(n => n.SelfAndDescendants());

    public NodeViewModel? FindNode(string id) => AllNodes().FirstOrDefault(n => n.Id == id);

    // ------------------------------------------------------------------ Struktur

    /// <summary>Baut die Kartenliste aus der Definition neu auf; Auf-/Zuklappzustand und Auswahl bleiben erhalten.</summary>
    public void RebuildTree(string? selectId = null)
    {
        var expanded = AllNodes().ToDictionary(n => n.Id, n => n.IsExpanded);
        selectId ??= SelectedNode?.Id;
        _selected = null;
        Nodes.Clear();
        var def = Project.Definition;
        foreach (var section in new[] { def.Init, def.Loop, def.Finalization })
        {
            var node = new NodeViewModel(section, null, isSection: true);
            AddChildren(node);
            Nodes.Add(node);
        }
        foreach (var block in def.GroupLibrary)
        {
            var node = new NodeViewModel(block.Body, null, isSection: true)
            {
                Note = $"Baustein v{block.Version} · " + (block.Ports.Count == 0 ? "ohne Ports" : string.Join(", ", block.Ports.Select(p => p.Name))),
            };
            AddChildren(node);
            Nodes.Add(node);
        }
        foreach (var n in AllNodes())
        {
            if (expanded.TryGetValue(n.Id, out var e)) n.IsExpanded = e;
            n.HasBreakpoint = _breakpoints?.HasBreakpoint(n.Id) ?? false;
        }
        Renumber();
        Validate();
        SelectedNode = selectId is null ? null : FindNode(selectId);
        if (SelectedNode is null) Properties = new ProjectPropertiesViewModel(this);
    }

    private static void AddChildren(NodeViewModel node)
    {
        if (node.Group is not { } g) return;
        foreach (var child in g.Children)
        {
            var c = new NodeViewModel(child, node);
            AddChildren(c);
            node.Children.Add(c);
        }
        if (g.ExitGroup is { } exit)
        {
            var e = new NodeViewModel(exit, node, isExit: true);
            AddChildren(e);
            node.Children.Add(e);
        }
    }

    private void Renumber()
    {
        foreach (var section in Nodes)
        {
            section.Number = "";
            Number(section, "");
        }

        static void Number(NodeViewModel parent, string prefix)
        {
            var i = 0;
            foreach (var c in parent.Children)
            {
                c.Number = c.IsExit ? $"{prefix}E" : $"{prefix}{++i}";
                Number(c, c.Number + ".");
            }
        }
    }

    /// <summary>Statische Prüfung: Definition und Bausteinkonfiguration.</summary>
    public void Validate()
    {
        Diagnostics.Clear();
        foreach (var d in Project.Definition.Validate()) Diagnostics.Add(d);
        foreach (var n in AllNodes())
        {
            n.Diagnostic = null;
            if (n.Step is { } s)
            {
                var diag = DiagnosticsFor(s).FirstOrDefault();
                n.Diagnostic = diag;
                if (diag is not null) Diagnostics.Add($"{n.Number} {n.Name}: {diag}");
            }
        }
        OnPropertyChanged(nameof(HasDiagnostics));
    }

    /// <summary>Konfigurationsdiagnosen eines Schritts inkl. Prüfung der Bausteinbindungen gegen die Bibliothek.</summary>
    public IReadOnlyList<string> DiagnosticsFor(StepDefinition step)
    {
        var list = Catalog.Rebuild(step).ToList();
        if (list.Count == 0) list.AddRange(BusBlocks.CheckReferences(step, Project.Definition));
        if (step.BlockType is not (BlockCatalog.LibraryBlock or BlockCatalog.GroupBlock)) return list;
        var id = step.Parameters.GetValueOrDefault("Baustein");
        if (string.IsNullOrWhiteSpace(id)) return ["Kein Baustein aus der Bibliothek gewählt."];
        if (list.Count > 0) return list;
        var block = BlockRefsFor(step.BlockType).FirstOrDefault(b => b.Id == id);
        if (block is null) return [$"Baustein '{id}' ist nicht in der Bibliothek."];
        if (step.BlockType == BlockCatalog.GroupBlock && Project.Definition.OwningGroupBlock(step.Id) is { } owner && owner.Id == id)
            return ["Ein grafischer Baustein darf sich nicht selbst aufrufen."];
        CheckPortBindings(step, block.Ports, list);
        return list;
    }

    private static void CheckPortBindings(StepDefinition step, IReadOnlyList<CodePort> ports, List<string> list)
    {
        foreach (var port in ports)
        {
            var key = (port.Direction == PortDirection.Output ? BlockCatalog.OutputBindingPrefix : BlockCatalog.InputBindingPrefix) + port.Name;
            var text = step.Parameters.GetValueOrDefault(key);
            if (string.IsNullOrWhiteSpace(text))
            {
                if (port.IsInput) list.Add($"Eingang „{port.Name}“ ist nicht verbunden.");
                continue;
            }
            try
            {
                var expr = Expression.Parse(text);
                if (port.IsOutput && (expr.ReferencedNames.Count != 1 || expr.ReferencedNames[0] != text.Trim()))
                    list.Add($"„{port.Name}“: Ziel muss ein Variablenname sein.");
            }
            catch (ExpressionException ex)
            {
                list.Add($"„{port.Name}“: {ex.Message}");
            }
        }
    }

    /// <summary>Auswählbare Bausteine für einen Baustein-Schritt (Code- oder Gruppenbausteine).</summary>
    public IReadOnlyList<BlockRef> BlockRefsFor(string? blockType) => blockType == BlockCatalog.GroupBlock
        ? Project.Definition.GroupLibrary.Select(b => new BlockRef(b.Id, b.Name,
            $"Grafisch · v{b.Version}{(string.IsNullOrWhiteSpace(b.Description) ? "" : " · " + b.Description)}", b.Ports)).ToList()
        : Library.Select(b => new BlockRef(b.Id, b.Name,
            $"{BlockCatalog.LanguageText(b.Language)} · v{b.Version}{(string.IsNullOrWhiteSpace(b.Description) ? "" : " · " + b.Description)}", b.Ports)).ToList();

    public bool CanCreateGroupBlock(NodeViewModel? node)
        => node is { IsGroup: true, IsSection: false, IsExit: false, Parent: not null };

    /// <summary>
    /// „Aus Gruppe erstellen“: Gruppe wird grafischer Baustein (Körper in der Bibliothek, im Editor als eigener
    /// Abschnitt bearbeitbar); an ihrer Stelle steht ein Gruppenbaustein-Schritt mit gleichwertigen Bindungen.
    /// </summary>
    public IReadOnlyList<string> CreateGroupBlock(NodeViewModel node)
    {
        if (!CanCreateGroupBlock(node)) return ["Diese Gruppe kann nicht umgewandelt werden."];
        var id = NewLibraryId(node.Name);
        while (Project.Definition.GroupLibrary.Any(b => b.Id == id) || Project.Definition.Find(id + ".koerper") is not null) id += "-g";
        var (_, step, warnings) = GroupBlockConverter.Convert(Project.Definition, node.Group!, id, Catalog);
        MarkChanged();
        RebuildTree(selectId: step.Id);
        LibraryChanged?.Invoke();
        Notice = warnings.Count == 0
            ? $"Grafischer Baustein „{node.Name}“ erstellt. Der Körper ist unten als eigener Abschnitt bearbeitbar."
            : string.Join(" ", warnings);
        return warnings;
    }

    /// <summary>
    /// CodeBlock in wiederverwendbaren Baustein umwandeln: verwendete Ordnervariablen werden Ein-/Ausgänge,
    /// der Schritt wird durch einen Baustein-Schritt mit gleichwertigen Bindungen ersetzt (ID bleibt).
    /// </summary>
    public IReadOnlyList<string> ConvertToLibraryBlock(NodeViewModel node)
    {
        if (node.Step is not { BlockType: BlockCatalog.Code } step || node.Parent?.Group is not { } parent) return ["Nur CodeBlocks können umgewandelt werden."];
        var visible = CodeBlockConverter.VisibleVariables(Project.Definition, step.Id);
        var blockId = NewLibraryId(step.Name);
        var (block, replacement, warnings) = CodeBlockConverter.Convert(step, visible, blockId, Catalog);
        Library.Add(block);
        parent.Children[parent.Children.IndexOf(step)] = replacement;
        MarkChanged();
        RebuildTree(selectId: replacement.Id);
        LibraryChanged?.Invoke();
        return warnings;
    }

    private string? _notice;

    /// <summary>Hinweis für den Entwickler (z. B. nach dem Umwandeln).</summary>
    public string? Notice
    {
        get => _notice;
        set => SetProperty(ref _notice, value);
    }

    /// <summary>Bibliothek geändert (Bausteine, Ports).</summary>
    public event Action? LibraryChanged;

    public void RaiseLibraryChanged()
    {
        MarkChanged();
        Validate();
        LibraryChanged?.Invoke();
    }

    public string NewLibraryId(string name)
    {
        var baseId = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (baseId.Length == 0) baseId = "baustein";
        var id = baseId;
        var n = 1;
        while (Library.Any(b => b.Id == id)) id = $"{baseId}-{++n}";
        return id;
    }

    public string NewId(string prefix)
    {
        var existing = new HashSet<string>(Project.Definition.AllNodes().Select(n => n.Id));
        string id;
        do id = $"{prefix}{++_idCounter}";
        while (existing.Contains(id));
        return id;
    }

    /// <summary>Neuen Baustein bzw. Gruppe an der festen Position relativ zur Auswahl einfügen.</summary>
    public NodeViewModel Insert(PaletteItem item)
    {
        NodeDefinition node = item.BlockType is null
            ? new GroupDefinition(NewId("gruppe"), "Neue Gruppe")
            : Catalog.Create(item.BlockType, NewId("schritt"), item.DisplayName);
        var (parent, index) = InsertPosition();
        parent.Group!.Children.Insert(index, node);
        parent.IsExpanded = true;
        MarkStructureChanged();
        RebuildTree(selectId: node.Id);
        return SelectedNode!;
    }

    private (NodeViewModel Parent, int Index) InsertPosition()
    {
        var sel = SelectedNode;
        if (sel is null) return (Nodes[1], Nodes[1].Group!.Children.Count);
        if (sel.IsGroup && (sel.IsExpanded || sel.IsSection || sel.Children.Count == 0))
            return (sel, sel.Group!.Children.Count); // ans Ende der Gruppe (vor Exit)
        if (sel.IsExit) return (sel, sel.Group!.Children.Count);
        var parent = sel.Parent!;
        return (parent, parent.Group!.Children.IndexOf(sel.Definition) + 1);
    }

    public bool CanDelete(NodeViewModel? node) => node is not null && !node.IsSection;

    public void Delete(NodeViewModel? node)
    {
        if (!CanDelete(node)) return;
        var parentGroup = node!.Parent!.Group!;
        if (node.IsExit) parentGroup.ExitGroup = null;
        else parentGroup.Children.Remove(node.Definition);
        _breakpoints?.ClearBreakpoint(node.Id);
        MarkStructureChanged();
        RebuildTree(selectId: node.Parent.Id);
    }

    /// <summary>Feste Einfügeposition für Drag-and-drop prüfen.</summary>
    public bool CanMove(NodeViewModel node, NodeViewModel targetParent, int index)
    {
        if (!node.CanMove || targetParent.Group is null) return false;
        if (node.SelfAndDescendants().Contains(targetParent)) return false; // nicht in sich selbst
        return index >= 0 && index <= targetParent.Group.Children.Count;
    }

    /// <summary>Verschiebt eine Karte an eine feste Position; die Knoten-ID bleibt erhalten.</summary>
    public bool Move(NodeViewModel node, NodeViewModel targetParent, int index)
    {
        if (!CanMove(node, targetParent, index)) return false;
        var source = node.Parent!.Group!.Children;
        var target = targetParent.Group!.Children;
        var oldIndex = source.IndexOf(node.Definition);
        if (ReferenceEquals(source, target) && oldIndex < index) index--;
        source.RemoveAt(oldIndex);
        target.Insert(Math.Min(index, target.Count), node.Definition);
        targetParent.IsExpanded = true;
        MarkStructureChanged();
        RebuildTree(selectId: node.Id);
        return true;
    }

    public void MoveBy(NodeViewModel? node, int delta)
    {
        if (node?.CanMove != true) return;
        var list = node.Parent!.Group!.Children;
        var i = list.IndexOf(node.Definition) + delta;
        if (i < 0 || i >= list.Count) return;
        Move(node, node.Parent, delta > 0 ? i + 1 : i);
    }

    /// <summary>In die vorherige Geschwistergruppe einrücken.</summary>
    public void Indent(NodeViewModel? node)
    {
        if (node?.CanMove != true) return;
        var list = node.Parent!.Group!.Children;
        var i = list.IndexOf(node.Definition);
        if (i <= 0 || list[i - 1] is not GroupDefinition) return;
        var target = node.Parent.Children.First(c => c.Definition == list[i - 1]);
        Move(node, target, target.Group!.Children.Count);
    }

    /// <summary>Aus der Gruppe heraus hinter die Elterngruppe verschieben.</summary>
    public void Outdent(NodeViewModel? node)
    {
        if (node?.CanMove != true) return;
        var parent = node.Parent!;
        if (parent.IsSection || parent.IsExit || parent.Parent is null) return;
        var grand = parent.Parent;
        Move(node, grand, grand.Group!.Children.IndexOf(parent.Definition) + 1);
    }

    public void ToggleBreakpoint(NodeViewModel? node)
    {
        if (node is null || _breakpoints is null || node.IsSection) return;
        if (_breakpoints.HasBreakpoint(node.Id)) _breakpoints.ClearBreakpoint(node.Id);
        else _breakpoints.SetBreakpoint(node.Id);
        node.HasBreakpoint = _breakpoints.HasBreakpoint(node.Id);
    }

    public void MarkChanged()
    {
        IsDirty = true;
        ChangedAt = DateTime.Now;
        RecordHistory();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ Dateien

    public void Load(TestProject project, string? path = null)
    {
        Project = project;
        FilePath = path;
        IsDirty = false;
        _idCounter = 0;
        RebuildTree();
        ResetHistory();
        OnPropertiesChanged(nameof(ProjectName), nameof(Version));
        ProjectReplaced?.Invoke();
    }

    private void NewProject()
    {
        var def = new TestDefinition("Neues Projekt", "0.1",
            new GroupDefinition("init", "Test Start / Init"),
            new GroupDefinition("loop", "Test Loop / Testing") { Repetition = Repetition.Endless },
            new GroupDefinition("fin", "Test End / Finalization"));
        Load(new TestProject(def));
    }

    private void Open()
    {
        var path = _dialogs?.OpenProject();
        if (path is null) return;
        try
        {
            Load(_serializer.Load(path), path);
        }
        catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException)
        {
            _dialogs!.ShowError($"Projekt konnte nicht geöffnet werden: {ex.Message}");
        }
    }

    public bool Save(string? path)
    {
        path ??= _dialogs?.SaveProject($"{Project.Definition.Name}.tproj.json");
        if (path is null) return false;
        try
        {
            _serializer.Save(Project, path);
            FilePath = path;
            IsDirty = false;
            return true;
        }
        catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException)
        {
            _dialogs?.ShowError($"Projekt konnte nicht gespeichert werden: {ex.Message}");
            return false;
        }
    }

    private static NodeIcon IconFor(string type) => NodeViewModel.IconFor(type);
}
