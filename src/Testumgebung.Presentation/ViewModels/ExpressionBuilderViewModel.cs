/**
 * @file
 * @brief Verbindet Ausdruckssteckplätze, lokale Bearbeitungshistorie und die vorhandene Ausdruckssprache.
 */
using Testumgebung.Contracts;
using Testumgebung.Domain.Expressions;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed record ExpressionPaletteGroup(string Name, IReadOnlyList<ExpressionTile> Items);

/// <summary>Dialogmodell. Erst das ausdrückliche Übernehmen schreibt in den Projekteditor.</summary>
public sealed class ExpressionBuilderViewModel : ObservableObject
{
    private sealed record SavedNode(VisualExpressionKind Kind, string Value, SavedNode? Left, SavedNode? Right);
    private readonly List<SavedNode> _undo = [], _redo = [];
    private VisualExpressionNode _root = null!, _selected = null!;
    private string _error = "", _source = "", _resultType = "?", _notice = "";
    public const int MaximumDepth = 32;
    public const int MaximumNodes = 256;

    public ExpressionBuilderViewModel(string original, IReadOnlyList<ExpressionVariable> variables,
        VarType? expectedType = null, bool variablesOnly = false, bool allowEmpty = false)
    {
        Original = original; Variables = variables; ExpectedType = expectedType; VariablesOnly = variablesOnly; AllowEmpty = allowEmpty;
        InsertCommand = new RelayCommand(parameter => { if (parameter is ExpressionTile tile) Insert(tile); });
        WrapCommand = new RelayCommand(parameter => { if (parameter is ExpressionTile tile) Insert(tile, true); });
        DeleteCommand = new RelayCommand(() => ReplaceSelected(new(this, VisualExpressionKind.Empty)));
        UndoCommand = new RelayCommand(() => Restore(_undo, _redo), () => _undo.Count > 0);
        RedoCommand = new RelayCommand(() => Restore(_redo, _undo), () => _redo.Count > 0);
        _root = new(this, VisualExpressionKind.Empty); _selected = _root;
        try
        {
            if (!string.IsNullOrWhiteSpace(original))
            {
                if (original.Length > 32768) throw new ExpressionException("Der bestehende Ausdruck ist für die Blockansicht zu groß. Er bleibt im Texteditor erhalten.");
                _root = FromSyntax(Expression.Parse(original, MaximumNodes * 2 + 1, MaximumDepth).ToSyntax(), 0); _selected = _root;
            }
            Revalidate();
        }
        catch (ExpressionException ex) { Error = ex.Message; }
    }

    public string Original { get; }
    public IReadOnlyList<ExpressionVariable> Variables { get; }
    public VarType? ExpectedType { get; }
    public bool VariablesOnly { get; }
    public bool AllowEmpty { get; }
    public VisualExpressionNode Root => _root;
    public VisualExpressionNode Selected
    {
        get => _selected;
        set { if (ReferenceEquals(_selected, value) || !ReferenceEquals(value.Owner, this) || !Root.Walk().Contains(value)) return; var old = _selected; _selected = value; old?.NotifySelection(); value.NotifySelection(); OnPropertyChanged(); }
    }
    public string Notice { get => _notice; private set => SetProperty(ref _notice, value); }
    public string Error { get => _error; private set { SetProperty(ref _error, value); OnPropertyChanged(nameof(CanApply)); } }
    public string Source { get => _source; private set => SetProperty(ref _source, value); }
    public string ResultType { get => _resultType; private set => SetProperty(ref _resultType, value); }
    public bool CanApply => Error.Length == 0 && Source.Length > 0;
    public RelayCommand InsertCommand { get; }
    public RelayCommand WrapCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public IReadOnlyList<ExpressionPaletteGroup> Palette => Tiles.Where(expressionTile => !VariablesOnly || expressionTile.Kind == VisualExpressionKind.Variable)
        .GroupBy(expressionTile => expressionTile.Category).Select(group => new ExpressionPaletteGroup(group.Key, group.ToList())).ToList();

    public void Insert(ExpressionTile tile, bool wrap = false)
    {
        if (VariablesOnly && tile.Kind != VisualExpressionKind.Variable) return;
        if (tile.Kind == VisualExpressionKind.Variable && Variables.Count == 0) { Notice = "In diesem Kontext ist keine passende Variable deklariert."; return; }
        var next = CreateTile(tile);
        if (wrap && next.HasLeft) next.SetChildren(Clone(Selected), next.Right);
        else if (next.Kind == Selected.Kind && next.HasLeft)
            next.SetChildren(Clone(Selected.Left!), Selected.Right is null ? null : Clone(Selected.Right));
        if (!ReplaceSelected(next)) return;
        if (next.Left is { Kind: VisualExpressionKind.Empty } left) Selected = left;
    }

    private VisualExpressionNode CreateTile(ExpressionTile tile)
    {
        var next = new VisualExpressionNode(this, tile.Kind,
            tile.Kind == VisualExpressionKind.Variable ? DefaultVariable().Path : tile.Value);
        if (tile.Kind == VisualExpressionKind.Unary) next.SetChildren(new(this, VisualExpressionKind.Empty), null);
        if (tile.Kind == VisualExpressionKind.Binary) next.SetChildren(new(this, VisualExpressionKind.Empty), new(this, VisualExpressionKind.Empty));
        return next;
    }

    private ExpressionVariable DefaultVariable()
    {
        var ordinary = Variables.Where(variable => !variable.Path.Contains("_BlockZweig_", StringComparison.Ordinal)).ToList();
        var user = ordinary.Where(variable => !IsStatusVariable(variable.Path)).ToList();
        var expected = SlotType();
        bool Matches(ExpressionVariable variable) => expected is null || variable.Type == expected || expected == VarType.Double && variable.Type == VarType.Int;
        return user.FirstOrDefault(Matches) ?? ordinary.FirstOrDefault(Matches) ?? user.FirstOrDefault() ?? ordinary.FirstOrDefault() ?? Variables[0];
    }
    private static bool IsStatusVariable(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name is Domain.Scopes.Scope.GroupResultVariable or Engine.TestRun.ResourcesAcquiredVariable;
    }
    private VarType? SlotType()
    {
        if (Selected.Parent is not { } parent) return ExpectedType;
        if (parent.Value is "!" or "&&" or "||") return VarType.Bool;
        var other = ReferenceEquals(parent.Left, Selected) ? parent.Right : parent.Left;
        if (other is not null)
        {
            try { return VisualExpressionTypes.Infer(other, Variables); }
            catch (ExpressionException) { /* Leere Eingänge dürfen erst danach einen Wert erhalten. */ }
        }
        return VarType.Double;
    }

    private bool ReplaceSelected(VisualExpressionNode next)
    {
        if (Root.Walk().Count() - Selected.Walk().Count() + next.Walk().Count() > MaximumNodes || DepthOf(Selected) + Height(next) > MaximumDepth)
        { Notice = "Zu viele oder zu tief verschachtelte Blöcke. Ausdruck aufteilen."; return false; }
        Remember(); Replace(Selected, next); Selected = next; Revalidate(); return true;
    }

    /// <summary>Vorhandenen Block in einen Steckplatz bewegen oder kopieren; Zyklen und fremde Dialogmodelle ablehnen.</summary>
    public bool Connect(VisualExpressionNode source, VisualExpressionNode target, bool copy = false)
    {
        if (!CanConnect(source, target, copy)) { Notice = "Dieser Block passt nicht an die gewählte Position."; return false; }
        Remember();
        var next = copy ? Clone(source) : source;
        if (!copy) Replace(source, new(this, VisualExpressionKind.Empty));
        Replace(target, next); Selected = next; Revalidate(); return true;
    }

    /// <summary>Drag-Vorschau ohne Änderung des Ausdrucks.</summary>
    public bool CanConnect(VisualExpressionNode source, VisualExpressionNode target, bool copy = false)
    {
        if (source.Kind == VisualExpressionKind.Empty) return false;
        if (VariablesOnly && source.Kind != VisualExpressionKind.Variable) return false;
        if (!ReferenceEquals(source.Owner, this) || !ReferenceEquals(target.Owner, this) || source.Walk().Contains(target)) return false;
        if (!Root.Walk().Contains(source) || !Root.Walk().Contains(target)) return false;
        if (DepthOf(target) + Height(source) > MaximumDepth || copy && Root.Walk().Count() - target.Walk().Count() + source.Walk().Count() > MaximumNodes) return false;
        return true;
    }

    private void Replace(VisualExpressionNode old, VisualExpressionNode next)
    {
        if (old.Parent is { } parent) parent.ReplaceChild(old, next);
        else { _root = next; OnPropertyChanged(nameof(Root)); }
    }
    internal bool Contains(VisualExpressionNode node) => _root is not null && Root.Walk().Contains(node);
    internal void Remember()
    {
        if (_root is null) return;
        _undo.Add(Save(_root)); if (_undo.Count > 100) _undo.RemoveAt(0); _redo.Clear(); RaiseHistory();
    }
    private void Restore(List<SavedNode> from, List<SavedNode> to)
    {
        if (from.Count == 0) return;
        to.Add(Save(Root)); var saved = from[^1]; from.RemoveAt(from.Count - 1);
        _root = Load(saved); Selected = _root; OnPropertyChanged(nameof(Root)); Revalidate(); RaiseHistory();
    }
    private void RaiseHistory() { UndoCommand.RaiseCanExecuteChanged(); RedoCommand.RaiseCanExecuteChanged(); }
    private static SavedNode Save(VisualExpressionNode node) => new(node.Kind, node.Value, node.Left is null ? null : Save(node.Left), node.Right is null ? null : Save(node.Right));
    private VisualExpressionNode Load(SavedNode saved)
    {
        var node = new VisualExpressionNode(this, saved.Kind, saved.Value);
        node.SetChildren(saved.Left is null ? null : Load(saved.Left), saved.Right is null ? null : Load(saved.Right)); return node;
    }
    private VisualExpressionNode Clone(VisualExpressionNode node) => Load(Save(node));
    private static int DepthOf(VisualExpressionNode node) => node.Parent is null ? 0 : 1 + DepthOf(node.Parent);
    private static int Height(VisualExpressionNode node) => 1 + Math.Max(node.Left is null ? 0 : Height(node.Left), node.Right is null ? 0 : Height(node.Right));

    private VisualExpressionNode FromSyntax(ExpressionSyntax syntax, int depth)
    {
        if (depth >= MaximumDepth) throw new ExpressionException("Der Ausdruck ist für die Blockansicht zu tief verschachtelt.");
        var node = new VisualExpressionNode(this, Enum.Parse<VisualExpressionKind>(syntax.Kind.ToString()), syntax.Text);
        node.SetChildren(syntax.Left is null ? null : FromSyntax(syntax.Left, depth + 1), syntax.Right is null ? null : FromSyntax(syntax.Right, depth + 1));
        return node;
    }

    /// <summary>Prüft Syntax und statische Steckplatztypen ohne Gerätezugriffe oder Auswertung realer Laufzeitwerte.</summary>
    public void Revalidate()
    {
        Notice = "";
        try
        {
            if (VariablesOnly && Root.Kind != VisualExpressionKind.Variable) throw new ExpressionException("Eine Zielvariable aus der Liste wählen.");
            var type = VisualExpressionTypes.Infer(Root, Variables);
            if (ExpectedType is { } expected && type != expected && !(!VariablesOnly && expected == VarType.Double && type == VarType.Int))
                throw new ExpressionException($"Hier wird {expected} erwartet; der Block liefert {type}.");
            var source = Root.Source();
            if (source.Length > 32768) throw new ExpressionException("Der Ausdruck ist zu lang für die Blockansicht.");
            Expression.Parse(source, MaximumNodes * 2 + 1, MaximumDepth);
            Source = source; ResultType = type.ToString(); Error = "";
        }
        catch (ExpressionException ex) { Error = ex.Message; Source = ""; ResultType = "?"; }
        foreach (var node in Root.Walk()) node.NotifyType();
    }
    internal string TypeFor(VisualExpressionNode node)
    {
        try { return VisualExpressionTypes.Infer(node, Variables).ToString(); }
        catch (ExpressionException) { return "?"; }
    }

    public string LabelFor(VisualExpressionNode node) => node.Kind switch
    {
        VisualExpressionKind.Number => "Zahl", VisualExpressionKind.Text => "Text", VisualExpressionKind.Boolean => "Ja / Nein",
        VisualExpressionKind.Variable => "Variable", _ => OperatorLabels.GetValueOrDefault(node.Value, node.Value)
    };

    private static readonly IReadOnlyDictionary<string, string> OperatorLabels = new Dictionary<string, string>
    {
        ["+"] = "+", ["-"] = "−", ["*"] = "×", ["/"] = "÷", ["%"] = "Rest", ["=="] = "gleich", ["!="] = "ungleich",
        ["<"] = "kleiner", ["<="] = "kleiner / gleich", [">"] = "größer", [">="] = "größer / gleich", ["&&"] = "UND", ["||"] = "ODER", ["!"] = "NICHT"
    };
    private static readonly IReadOnlyList<ExpressionTile> Tiles =
    [
        new("Zahl", VisualExpressionKind.Number, "0", "Werte"), new("Text", VisualExpressionKind.Text, "", "Werte"),
        new("Ja / Nein", VisualExpressionKind.Boolean, "true", "Werte"), new("Variable", VisualExpressionKind.Variable, "", "Werte"),
        .. new[] { "+", "-", "*", "/", "%" }.Select(op => new ExpressionTile(OperatorLabels[op], VisualExpressionKind.Binary, op, "Rechnen")),
        new("Vorzeichen −", VisualExpressionKind.Unary, "-", "Rechnen"),
        .. new[] { "==", "!=", "<", "<=", ">", ">=" }.Select(op => new ExpressionTile(OperatorLabels[op], VisualExpressionKind.Binary, op, "Vergleichen")),
        new("UND", VisualExpressionKind.Binary, "&&", "Logik"), new("ODER", VisualExpressionKind.Binary, "||", "Logik"), new("NICHT", VisualExpressionKind.Unary, "!", "Logik")
    ];
}
