using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Expressions;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Dieselben Typkombinationen wie in der Ausdrucksengine, ohne Ausführung von Operatoren oder Variablenwerten.</summary>
internal static class VisualExpressionTypes
{
    public static VarType Infer(VisualExpressionNode node, IReadOnlyList<ExpressionVariable> variables) => node.Kind switch
    {
        VisualExpressionKind.Number => NumberType(node.Value),
        VisualExpressionKind.Text => VarType.String,
        VisualExpressionKind.Boolean => VarType.Bool,
        VisualExpressionKind.Variable => VariableType(node.Value, variables),
        VisualExpressionKind.Unary => Unary(node.Value, Infer(node.Left!, variables)),
        VisualExpressionKind.Binary => Binary(node.Value, Infer(node.Left!, variables), Infer(node.Right!, variables)),
        _ => throw new ExpressionException("Ein Steckplatz ist noch leer.")
    };
    private static VarType VariableType(string name, IReadOnlyList<ExpressionVariable> variables)
    {
        if (!ExpressionVariableChoices.IsPath(name))
            throw new ExpressionException("Dieser Name kann nicht als Ausdrucksvariable verwendet werden.");
        return variables.FirstOrDefault(v => v.Path == name)?.Type
            ?? throw new ExpressionException($"Variable '{name}' ist hier nicht deklariert oder nicht sichtbar.");
    }
    private static VarType NumberType(string text)
    {
        var source = VisualExpressionNode.NumberSource(text);
        return long.TryParse(source, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ? VarType.Int : VarType.Double;
    }
    private static bool Numeric(VarType type) => type is VarType.Int or VarType.Double;
    private static VarType Unary(string op, VarType operand)
    {
        if (op == "!" && operand == VarType.Bool) return VarType.Bool;
        if (op == "-" && Numeric(operand)) return operand;
        throw new ExpressionException($"'{op}' passt nicht zu {operand}.");
    }
    private static VarType Binary(string op, VarType left, VarType right)
    {
        if (op is "&&" or "||") return BothBoolean(op, left, right);
        if (op is "==" or "!=") return Comparable(op, left, right);
        if (op is "<" or "<=" or ">" or ">=") return Comparable(op, left, right, ordered: true);
        return Arithmetic(op, left, right);
    }
    private static VarType Arithmetic(string op, VarType left, VarType right)
    {
        if (op == "+" && left == VarType.String && right == VarType.String) return VarType.String;
        if (Numeric(left) && Numeric(right)) return left == VarType.Double || right == VarType.Double ? VarType.Double : VarType.Int;
        throw new ExpressionException($"'{op}' erwartet passende Zahlenblöcke; erhalten: {left} und {right}.");
    }
    private static VarType BothBoolean(string op, VarType left, VarType right)
    {
        if (left == VarType.Bool && right == VarType.Bool) return VarType.Bool;
        throw new ExpressionException($"'{op}' erwartet zwei Ja/Nein-Blöcke.");
    }
    private static VarType Comparable(string op, VarType left, VarType right, bool ordered = false)
    {
        if (Numeric(left) && Numeric(right)) return VarType.Bool;
        if (left == right && (!ordered || left is VarType.String or VarType.DateTime)) return VarType.Bool;
        throw new ExpressionException($"'{op}' kann {left} und {right} nicht vergleichen.");
    }
}
