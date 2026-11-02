namespace Testumgebung.Domain.Expressions;

/// <summary>Unveränderliche, UI-neutrale Struktur eines bereits geparsten Prüfausdrucks.</summary>
public enum ExpressionSyntaxKind { Number, Text, Boolean, Variable, Unary, Binary }

/// <summary>Text ist Literalinhalt, Variablenpfad oder Operator; Kinder bilden die Klammerung ab.</summary>
public sealed record ExpressionSyntax(ExpressionSyntaxKind Kind, string Text, ExpressionSyntax? Left = null, ExpressionSyntax? Right = null);
