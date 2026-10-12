using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Testumgebung.Domain.Definitions;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Suchtreffer im Ablauf.</summary>
public sealed record SearchHit(NodeViewModel Node, string Where);

/// <summary>
/// Editor-Komfort: Rückgängig/Wiederholen (Projektstände als JSON, Tippfolgen werden zusammengefasst), Kopieren/Ausschneiden/
/// Einfügen/Duplizieren (Knoten samt Inhalt mit neuen IDs), Suche über Namen, Bausteine, Parameter und Variablen.
/// </summary>
public sealed partial class EditorViewModel
{
    private const int HistoryLimit = 100;
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>Zwischenablage der Anwendung (über Editor-Instanzen hinweg).</summary>
    private static string? _clipboard;

    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private string? _current;
    private DateTime _lastRecord;
    private string? _lastContext;
    private bool _restoring;
    private string _searchText = "";
    private int _searchIndex = -1;
    private string? _searchStatus;

    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand CutCommand { get; }
    public RelayCommand PasteCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand FindNextCommand { get; }
    public RelayCommand FindPreviousCommand { get; }

    // ------------------------------------------------------------------ Rückgängig / Wiederholen

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string UndoText => _undo.Count == 0 ? "Nichts rückgängig zu machen" : $"Rückgängig ({_undo.Count})";

    private string? Snapshot()
    {
        try
        {
            return _serializer.Serialize(Project);
        }
        catch (ProjectFormatException)
        {
            return null; // Projekt mit Code-Schritten außerhalb des Katalogs: ohne Verlauf
        }
    }

    private void ResetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _current = Snapshot();
        _lastContext = null;
        RaiseHistory();
    }

    /// <summary>Nach jeder Änderung: neuer Stand. Änderungen am selben Element kurz hintereinander (Tippen) bilden einen Schritt.</summary>
    private void RecordHistory()
    {
        if (_restoring) return;
        var next = Snapshot();
        if (next is null || next == _current) return;
        var context = SelectedNode?.Id ?? "(Projekt)";
        var now = DateTime.UtcNow;
        var merge = _current is not null && _undo.Count > 0 && context == _lastContext && now - _lastRecord < MergeWindow;
        if (!merge && _current is not null)
        {
            _undo.Add(_current);
            if (_undo.Count > HistoryLimit) _undo.RemoveAt(0);
        }
        _current = next;
        _redo.Clear();
        _lastRecord = now;
        _lastContext = context;
        RaiseHistory();
    }

    /// <summary>Strukturänderung (Einfügen, Löschen, Verschieben) nie mit Tippfolgen zusammenfassen.</summary>
    private void BreakMerge() => _lastContext = null;

    /// <summary>Änderung als eigener Rückgängig-Schritt (Einfügen, Löschen, Import …).</summary>
    public void MarkStructureChanged()
    {
        BreakMerge();
        MarkChanged();
        BreakMerge();
    }

    public void Undo()
    {
        if (_undo.Count == 0 || _current is null) return;
        _redo.Add(_current);
        _current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(_current);
    }

    public void Redo()
    {
        if (_redo.Count == 0 || _current is null) return;
        _undo.Add(_current);
        _current = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(_current);
    }

    private void Restore(string json)
    {
        var selected = SelectedNode?.Id;
        _restoring = true;
        try
        {
            Project = _serializer.Deserialize(json);
            RebuildTree(selected);
            IsDirty = true;
            ChangedAt = DateTime.Now;
            OnPropertiesChanged(nameof(ProjectName), nameof(Version));
            Changed?.Invoke();
            ProjectReplaced?.Invoke();
            RaiseLibraryChanged();
        }
        finally
        {
            _restoring = false;
            _lastContext = null;
            RaiseHistory();
        }
    }

    private void RaiseHistory()
    {
        UndoCommand?.RaiseCanExecuteChanged();
        RedoCommand?.RaiseCanExecuteChanged();
        OnPropertiesChanged(nameof(CanUndo), nameof(CanRedo), nameof(UndoText));
    }

    // ------------------------------------------------------------------ Kopieren / Einfügen

    public bool HasClipboard => _clipboard is not null;

    /// <summary>Knoten samt Inhalt in die Zwischenablage (als Projekt-JSON eines Hilfsablaufs).</summary>
    public void Copy(NodeViewModel? node)
    {
        if (node is null || node.IsSection) return;
        var holder = new GroupDefinition("__ablage", "Ablage");
        holder.Children.Add(node.Definition);
        var temp = new TestDefinition("Ablage", "0", new GroupDefinition("__init", "Init"), holder, new GroupDefinition("__fin", "Fin"));
        var project = new TestProject(temp);
        try
        {
            _clipboard = _serializer.Serialize(project);
            Notice = $"„{node.Name}“ kopiert.";
        }
        catch (ProjectFormatException ex)
        {
            Notice = "Kopieren nicht möglich: " + ex.Message;
        }
        PasteCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasClipboard));
    }

    public void Cut(NodeViewModel? node)
    {
        if (!CanDelete(node)) return;
        Copy(node);
        if (_clipboard is not null) Delete(node);
    }

    /// <summary>Einfügen an der festen Einfügeposition (wie aus der Palette); alle IDs werden neu vergeben.</summary>
    public NodeViewModel? Paste()
    {
        if (_clipboard is null) return null;
        NodeDefinition node;
        try
        {
            var json = JsonNode.Parse(_clipboard)!;
            var loop = json["loop"]!;
            foreach (var child in loop["children"]!.AsArray()) Renumber(child!);
            node = _serializer.Deserialize(json.ToJsonString()).Definition.Loop.Children[0];
        }
        catch (Exception ex) when (ex is ProjectFormatException or InvalidOperationException or NullReferenceException or System.Text.Json.JsonException)
        {
            Notice = "Einfügen nicht möglich: " + ex.Message;
            return null;
        }
        var (parent, index) = InsertPosition();
        parent.Group!.Children.Insert(index, node);
        parent.IsExpanded = true;
        MarkStructureChanged();
        RebuildTree(selectId: node.Id);
        Notice = $"„{node.Name}“ eingefügt.";
        return SelectedNode;
    }

    public NodeViewModel? Duplicate(NodeViewModel? node)
    {
        if (node is null || node.IsSection || node.IsExit) return null;
        var saved = _clipboard;
        Copy(node);
        SelectedNode = node;
        var copy = Paste();
        _clipboard = saved ?? _clipboard;
        return copy;
    }

    /// <summary>Neue IDs für einen eingefügten Teilbaum (Knoten, Kinder, Exit-Gruppen).</summary>
    private void Renumber(JsonNode node)
    {
        if (node is not JsonObject obj) return;
        if (obj["id"] is JsonValue id && id.TryGetValue<string>(out var old))
        {
            var prefix = obj["art"]?.GetValue<string>() == "gruppe" || obj.ContainsKey("children") ? "gruppe" : "schritt";
            obj["id"] = NewId(prefix);
            _ = old;
        }
        if (obj["children"] is JsonArray children)
            foreach (var c in children) Renumber(c!);
        if (obj["exit"] is JsonObject exit) Renumber(exit);
    }

    // ------------------------------------------------------------------ Suche

    public ObservableCollection<SearchHit> SearchHits { get; } = [];

    /// <summary>Suchtext; Treffer in Namen, Beschreibung, Bausteintyp, Parametern (z. B. Ressourcen, Grenzwerte) und Variablen.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            Search();
        }
    }

    public string? SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    public void Search()
    {
        SearchHits.Clear();
        _searchIndex = -1;
        var text = _searchText.Trim();
        if (text.Length == 0)
        {
            SearchStatus = null;
            return;
        }
        bool Has(string? s) => s is not null && s.Contains(text, StringComparison.OrdinalIgnoreCase);
        foreach (var n in AllNodes())
        {
            var where = new List<string>();
            if (Has(n.Name)) where.Add("Name");
            if (Has(n.Definition.Description)) where.Add("Beschreibung");
            if (n.Step is { } s)
            {
                if (s.BlockType is { } t && (Has(t) || (Catalog.TryGet(t, out var d) && Has(d.DisplayName)))) where.Add("Baustein");
                foreach (var (key, value) in s.Parameters)
                    if (Has(value)) where.Add(key == "Code" ? "Code" : $"{key} = {value}");
            }
            if (n.Group is { } g)
            {
                foreach (var v in g.Variables.Where(v => Has(v.Name))) where.Add("Variable " + v.Name);
                if (Has(g.EntryCondition)) where.Add("Bedingung");
                foreach (var r in g.Resources.Where(Has)) where.Add("Reservierung " + r);
            }
            if (where.Count > 0) SearchHits.Add(new SearchHit(n, string.Join(", ", where.Distinct())));
        }
        SearchStatus = SearchHits.Count == 0 ? "Keine Treffer" : $"{SearchHits.Count} Treffer";
    }

    /// <summary>Nächsten/vorherigen Treffer wählen und Gruppen bis dorthin aufklappen.</summary>
    public void FindNext(int direction)
    {
        if (SearchHits.Count == 0) Search();
        if (SearchHits.Count == 0) return;
        _searchIndex = (_searchIndex + direction + SearchHits.Count) % SearchHits.Count;
        Reveal(SearchHits[_searchIndex].Node.Id);
        SearchStatus = $"Treffer {_searchIndex + 1} von {SearchHits.Count}: {SearchHits[_searchIndex].Where}";
    }

    public void Reveal(string id)
    {
        if (FindNode(id) is not { } node) return;
        for (var p = node.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        SelectedNode = node;
    }
}
