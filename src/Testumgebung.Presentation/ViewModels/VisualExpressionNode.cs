/**
 * @file
 * @brief Veränderliche Ausdrucksblöcke; gespeichert wird weiterhin ausschließlich der normale Prüfausdruck.
 */
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Expressions;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public enum VisualExpressionKind { Empty, Number, Text, Boolean, Variable, Unary, Binary }
public sealed record ExpressionVariable(string Path, VarType Type, string Origin)
{
    public string Label => Path.Contains("_BlockZweig_", StringComparison.Ordinal) ? $"Vorlagenentscheidung · {Origin}" : $"{Path} · {Type} · {Origin}";
}
public sealed record ExpressionTile(string Label, VisualExpressionKind Kind, string Value, string Category)
{
    public bool IsOperator => Kind is VisualExpressionKind.Unary or VisualExpressionKind.Binary;
}

/// <summary>Ein Steckplatz oder Ausdrucksblock; Elternbezüge verhindern zyklische Drag-and-drop-Verbindungen.</summary>
public sealed class VisualExpressionNode : ObservableObject
{
    private string _value;
    private VisualExpressionNode? _left, _right;
    internal VisualExpressionNode(ExpressionBuilderViewModel owner, VisualExpressionKind kind, string value = "")
    { Owner = owner; Kind = kind; _value = value; }
    public ExpressionBuilderViewModel Owner { get; }
    public VisualExpressionKind Kind { get; }
    public VisualExpressionNode? Parent { get; private set; }
    public string Value
    {
        get => _value;
        set
        {
            value ??= "";
            if (_value == value || !Owner.Contains(this)) return;
            Owner.Remember();
            if (SetProperty(ref _value, value)) { OnPropertiesChanged(nameof(VariableChoice), nameof(BooleanValue)); Owner.Revalidate(); }
        }
    }
    public ExpressionVariable? VariableChoice
    {
        get => IsVariable ? Owner.Variables.FirstOrDefault(variable => variable.Path == Value) : null;
        set { if (IsVariable && value is not null) Value = value.Path; }
    }
    public VisualExpressionNode? Left => _left;
    public VisualExpressionNode? Right => _right;
    public bool HasLeft => _left is not null;
    public bool HasRight => _right is not null;
    public bool IsNumber => Kind == VisualExpressionKind.Number;
    public bool IsText => Kind == VisualExpressionKind.Text;
    public bool IsBoolean => Kind == VisualExpressionKind.Boolean;
    public bool IsVariable => Kind == VisualExpressionKind.Variable;
    public bool BooleanValue { get => Value == "true"; set => Value = value ? "true" : "false"; }
    public bool IsSelected => ReferenceEquals(Owner.Selected, this);
    public string Label => Kind == VisualExpressionKind.Empty ? "Hier Block einsetzen" : Owner.LabelFor(this);
    public string TypeText => Owner.TypeFor(this);
    internal void NotifyType() => OnPropertiesChanged(nameof(TypeText), nameof(Color));
    public string Color => Kind is VisualExpressionKind.Unary or VisualExpressionKind.Binary && TypeText == "Bool" ? "#D7F3D9" : Kind switch
    {
        VisualExpressionKind.Empty => "#EEF2F7", VisualExpressionKind.Text => "#F1E3FF",
        VisualExpressionKind.Boolean => "#D7F3D9", VisualExpressionKind.Variable => "#FFE3B8", _ => "#D8EBFF"
    };
    internal void NotifySelection() => OnPropertyChanged(nameof(IsSelected));
    internal void SetChildren(VisualExpressionNode? left, VisualExpressionNode? right)
    {
        _left = left; _right = right;
        if (left is not null) left.Parent = this;
        if (right is not null) right.Parent = this;
        OnPropertiesChanged(nameof(Left), nameof(Right), nameof(HasLeft), nameof(HasRight));
    }
    internal void ReplaceChild(VisualExpressionNode old, VisualExpressionNode next)
    {
        if (ReferenceEquals(_left, old)) SetChildren(next, _right);
        else if (ReferenceEquals(_right, old)) SetChildren(_left, next);
        else throw new InvalidOperationException("Steckplatz gehört nicht zu diesem Block.");
        old.Parent = null;
    }
    internal IEnumerable<VisualExpressionNode> Walk()
    {
        yield return this;
        if (Left is not null) foreach (var visualExpressionNode in Left.Walk()) yield return visualExpressionNode;
        if (Right is not null) foreach (var visualExpressionNode2 in Right.Walk()) yield return visualExpressionNode2;
    }
    internal string Source() => Kind switch
    {
        VisualExpressionKind.Number => NumberSource(Value),
        VisualExpressionKind.Text => "\"" + Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        VisualExpressionKind.Boolean => BooleanValue ? "true" : "false",
        VisualExpressionKind.Variable => Value,
        VisualExpressionKind.Unary => "(" + Value + Left!.Source() + ")",
        VisualExpressionKind.Binary => "(" + Left!.Source() + " " + Value + " " + Right!.Source() + ")",
        _ => throw new ExpressionException("Ein Steckplatz ist noch leer.")
    };
    internal static string NumberSource(string text)
    {
        var source = text.Trim().Replace(',', '.');
        if (source.StartsWith('+')) source = source[1..];
        if (long.TryParse(source, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) return source;
        if (double.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)) return source;
        throw new ExpressionException("Eine endliche Zahl eingeben, z. B. 12 oder 3,5.");
    }
}
