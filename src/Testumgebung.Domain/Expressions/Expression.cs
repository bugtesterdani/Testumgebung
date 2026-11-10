/**
 * @file
 * @brief Parst und wertet Prüfausdrücke gegen einen Variablenresolver aus, ohne unbekannte Variablen still anzulegen.
 */

using System.Globalization;
using System.Text;
using Testumgebung.Contracts;

namespace Testumgebung.Domain.Expressions;

/// <summary>Fehler beim Parsen oder Auswerten eines Ausdrucks (Diagnose, kein stilles Weiterlaufen).</summary>
public sealed class ExpressionException(string message) : Exception(message);

/// <summary>Liefert den Wert einer Variable anhand ihres (ggf. mit <c>../</c> präfixierten) Namens.</summary>
public interface IValueResolver
{
    object Resolve(string name);
}

/// <summary>
/// Typisierte, sprachunabhängige Ausdruckssprache für Gruppenbedingungen (Abschnitt 11).
/// Keine Abhängigkeit von C#/Python-eval.
/// <para>
/// Unterstützt: Literale (<c>12</c>, <c>3.5</c>, <c>"text"</c>, <c>true</c>, <c>false</c>),
/// Variablen inkl. <c>../Name</c> und Punktnamen (<c>Auftrag.Nennspannung</c>),
/// <c>! - + * / %</c>, Vergleiche <c>== != &lt; &lt;= &gt; &gt;=</c>, <c>&amp;&amp; ||</c> (kurzschließend), Klammern.
/// </para>
/// </summary>
public sealed class Expression
{
    private readonly Node _root;

    private Expression(string source, Node root)
    {
        Source = source;
        _root = root;
    }

    public string Source { get; }

    /// <summary>Alle referenzierten Variablennamen (für statische Prüfung).</summary>
    public IReadOnlyList<string> ReferencedNames => _root.Names().Distinct().ToList();

    public static Expression Parse(string source) => ParseTokens(source, Lexer.Tokenize(source));

    /// <summary>Begrenztes Parsen für interaktive Eingaben; dieselbe Grammatik, aber begrenzte Token- und Klammertiefe.</summary>
    public static Expression Parse(string source, int maximumTokens, int maximumParenthesesDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumParenthesesDepth);
        var tokens = Lexer.Tokenize(source);
        if (tokens.Count > maximumTokens) throw new ExpressionException("Der Ausdruck enthält zu viele Bestandteile für die Blockansicht.");
        int depth = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == T.LParen && ++depth > maximumParenthesesDepth)
                throw new ExpressionException("Der Ausdruck ist für die Blockansicht zu tief geklammert.");
            if (token.Kind == T.RParen) depth--;
        }
        return ParseTokens(source, tokens);
    }

    private static Expression ParseTokens(string source, List<Token> tokens)
    {
        var parser = new Parser(tokens);
        var node = parser.ParseExpression();
        parser.ExpectEnd();
        return new Expression(source, node);
    }

    /// <summary>Liefert den vorhandenen Syntaxbaum für visuelle Editoren, ohne erneut zu parsen oder auszuwerten.</summary>
    public ExpressionSyntax ToSyntax() => _root.ToSyntax();

    public object Evaluate(IValueResolver resolver) => _root.Eval(resolver);

    public bool EvaluateBool(IValueResolver resolver)
        => Evaluate(resolver) is bool flag
            ? flag
            : throw new ExpressionException($"Bedingung '{Source}' liefert keinen bool-Wert.");

    public override string ToString() => Source;

    // ---------------------------------------------------------------- Lexer

    private enum T { Number, String, Ident, Op, LParen, RParen, End }

    private readonly record struct Token(T Kind, string Text, int Pos);

    private static class Lexer
    {
        private static readonly string[] Ops = ["&&", "||", "==", "!=", "<=", ">=", "<", ">", "+", "-", "*", "/", "%", "!"];

        public static List<Token> Tokenize(string s)
        {
            var list = new List<Token>();
            var index = 0;
            void ReadNumber()
            {
                var start = index;
                while (index < s.Length && (char.IsDigit(s[index]) || s[index] == '.')) index++;
                if (index < s.Length && (s[index] == 'e' || s[index] == 'E'))
                {
                    index++;
                    if (index < s.Length && (s[index] == '+' || s[index] == '-')) index++;
                    while (index < s.Length && char.IsDigit(s[index])) index++;
                }
                list.Add(new Token(T.Number, s[start..index], start));
            }

            void ReadString()
            {
                var start = index++;
                var sb = new StringBuilder();
                while (index < s.Length && s[index] != '"')
                {
                    if (s[index] == '\\' && index + 1 < s.Length) { sb.Append(s[index + 1]); index += 2; }
                    else sb.Append(s[index++]);
                }
                if (index >= s.Length) throw new ExpressionException($"Nicht abgeschlossene Zeichenkette ab Position {start}.");
                index++;
                list.Add(new Token(T.String, sb.ToString(), start));
            }

            void ReadName()
            {
                var start = index;
                while (index + 2 < s.Length && s[index] == '.' && s[index + 1] == '.' && s[index + 2] == '/') index += 3;
                if (index >= s.Length || !(char.IsLetter(s[index]) || s[index] == '_'))
                    throw new ExpressionException($"Variablenname erwartet an Position {index}.");
                while (index < s.Length && (char.IsLetterOrDigit(s[index]) || s[index] == '_' || s[index] == '.')) index++;
                list.Add(new Token(T.Ident, s[start..index], start));
            }

            while (index < s.Length)
            {
                var character = s[index];
                if (char.IsWhiteSpace(character)) { index++; continue; }
                if (char.IsDigit(character))
                {
                    ReadNumber();
                    continue;
                }
                if (character == '"')
                {
                    ReadString();
                    continue;
                }
                if (char.IsLetter(character) || character == '_' || (character == '.' && index + 2 < s.Length && s[index + 1] == '.' && s[index + 2] == '/'))
                {
                    ReadName();
                    continue;
                }
                if (character == '(') { list.Add(new Token(T.LParen, "(", index++)); continue; }
                if (character == ')') { list.Add(new Token(T.RParen, ")", index++)); continue; }
                var op = Ops.FirstOrDefault(option => string.CompareOrdinal(s, index, option, 0, option.Length) == 0);
                if (op is null) throw new ExpressionException($"Unerwartetes Zeichen '{character}' an Position {index}.");
                list.Add(new Token(T.Op, op, index));
                index += op.Length;
            }
            list.Add(new Token(T.End, "", s.Length));
            return list;
        }
    }

    // ---------------------------------------------------------------- Parser

    private sealed class Parser(List<Token> tokens)
    {
        private int _p;
        private Token Cur => tokens[_p];

        public void ExpectEnd()
        {
            if (Cur.Kind != T.End) throw new ExpressionException($"Unerwartetes '{Cur.Text}' an Position {Cur.Pos}.");
        }

        public Node ParseExpression() => ParseBinary(0);

        private static readonly string[][] Levels =
        [
            ["||"],
            ["&&"],
            ["==", "!="],
            ["<", "<=", ">", ">="],
            ["+", "-"],
            ["*", "/", "%"],
        ];

        private Node ParseBinary(int level)
        {
            if (level == Levels.Length) return ParseUnary();
            var left = ParseBinary(level + 1);
            while (Cur.Kind == T.Op && Levels[level].Contains(Cur.Text))
            {
                var op = Cur.Text;
                _p++;
                var right = ParseBinary(level + 1);
                left = new Binary(op, left, right);
            }
            return left;
        }

        private Node ParseUnary()
        {
            if (Cur.Kind == T.Op && (Cur.Text == "!" || Cur.Text == "-"))
            {
                var op = Cur.Text;
                _p++;
                return new Unary(op, ParseUnary());
            }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var cur = Cur;
            switch (cur.Kind)
            {
                case T.Number:
                    _p++;
                    if (cur.Text.Contains('.') || cur.Text.Contains('e') || cur.Text.Contains('E'))
                        return double.TryParse(cur.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                            ? new Literal(number, cur.Text)
                            : throw new ExpressionException($"Ungültige Zahl '{cur.Text}'.");
                    return long.TryParse(cur.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
                        ? new Literal(length, cur.Text)
                        : throw new ExpressionException($"Ungültige Zahl '{cur.Text}'.");
                case T.String:
                    _p++;
                    return new Literal(cur.Text);
                case T.Ident:
                    _p++;
                    return cur.Text switch
                    {
                        "true" => new Literal(true),
                        "false" => new Literal(false),
                        _ => new Variable(cur.Text),
                    };
                case T.LParen:
                    _p++;
                    var inner = ParseExpression();
                    if (Cur.Kind != T.RParen) throw new ExpressionException($"')' erwartet an Position {Cur.Pos}.");
                    _p++;
                    return inner;
                default:
                    throw new ExpressionException(cur.Kind == T.End
                        ? "Ausdruck unvollständig."
                        : $"Unerwartetes '{cur.Text}' an Position {cur.Pos}.");
            }
        }
    }

    // ---------------------------------------------------------------- AST

    private abstract class Node
    {
        public abstract object Eval(IValueResolver r);
        public abstract ExpressionSyntax ToSyntax();
        public virtual IEnumerable<string> Names() => [];
    }

    private sealed class Literal(object value, string? numberSource = null) : Node
    {
        public override object Eval(IValueResolver r) => value;
        public override ExpressionSyntax ToSyntax() => value switch
        {
            string text => new(ExpressionSyntaxKind.Text, text),
            bool flag => new(ExpressionSyntaxKind.Boolean, flag ? "true" : "false"),
            _ => new(ExpressionSyntaxKind.Number, numberSource!)
        };
    }

    private sealed class Variable(string name) : Node
    {
        public override ExpressionSyntax ToSyntax() => new(ExpressionSyntaxKind.Variable, name);
        public override object Eval(IValueResolver r) => Normalize(r.Resolve(name));
        public override IEnumerable<string> Names() => [name];
    }

    private sealed class Unary(string op, Node operand) : Node
    {
        public override ExpressionSyntax ToSyntax() => new(ExpressionSyntaxKind.Unary, op, operand.ToSyntax());
        public override object Eval(IValueResolver r)
        {
            var value = operand.Eval(r);
            return (op, v: value) switch
            {
                ("!", bool flag) => !flag,
                ("-", long integerValue) => -integerValue,
                ("-", double number) => -number,
                _ => throw new ExpressionException($"Operator '{op}' nicht anwendbar auf {Describe(value)}."),
            };
        }

        public override IEnumerable<string> Names() => operand.Names();
    }

    private sealed class Binary(string op, Node left, Node right) : Node
    {
        public override ExpressionSyntax ToSyntax() => new(ExpressionSyntaxKind.Binary, op, left.ToSyntax(), right.ToSyntax());
        public override IEnumerable<string> Names() => left.Names().Concat(right.Names());

        public override object Eval(IValueResolver r)
        {
            if (op is "&&" or "||")
            {
                var lv = left.Eval(r) as bool? ?? throw new ExpressionException($"'{op}' erwartet bool links.");
                if (op == "&&" && !lv) return false;
                if (op == "||" && lv) return true;
                return right.Eval(r) as bool? ?? throw new ExpressionException($"'{op}' erwartet bool rechts.");
            }

            var argument = left.Eval(r);
            var block = right.Eval(r);

            return EvaluateValues(op, argument, block);
        }

        private static object EvaluateValues(string op, object evaluateValue, object evaluateValue2)
        {
            if (op is "==" or "!=")
            {
                return EvaluateEquality(op, evaluateValue, evaluateValue2);
            }

            if (op is "<" or "<=" or ">" or ">=")
            {
                return EvaluateComparison(op, evaluateValue, evaluateValue2);
            }

            return EvaluateArithmetic(op, evaluateValue, evaluateValue2);
        }

        private static object EvaluateArithmetic(string op, object leftOperand, object rightOperand)
        {
            if (op == "+" && leftOperand is string s1 && rightOperand is string s2) return s1 + s2;

            if (!IsNumber(leftOperand) || !IsNumber(rightOperand))
                throw new ExpressionException($"Operator '{op}' nicht anwendbar auf {Describe(leftOperand)} und {Describe(rightOperand)}.");

            if (leftOperand is long la && rightOperand is long lb)
            {
                return EvaluateIntegers(op, la, lb);
            }

            return EvaluateFloatingPoint(op, ToDouble(leftOperand), ToDouble(rightOperand));
        }

        private static bool EvaluateComparison(string op, object leftOperand, object rightOperand)
        {
            int cmp;
            if (IsNumber(leftOperand) && IsNumber(rightOperand)) cmp = ToDouble(leftOperand).CompareTo(ToDouble(rightOperand));
            else if (leftOperand is string sa && rightOperand is string sb) cmp = string.CompareOrdinal(sa, sb);
            else if (leftOperand is DateTime da && rightOperand is DateTime db) cmp = da.CompareTo(db);
            else throw new ExpressionException($"Vergleich '{op}' zwischen {Describe(leftOperand)} und {Describe(rightOperand)} nicht erlaubt.");
            return op switch { "<" => cmp < 0, "<=" => cmp <= 0, ">" => cmp > 0, _ => cmp >= 0 };
        }

        private static bool EvaluateEquality(string op, object leftOperand, object rightOperand)
        {
            bool eq;
            if (IsNumber(leftOperand) && IsNumber(rightOperand)) eq = ToDouble(leftOperand) == ToDouble(rightOperand);
            else if (leftOperand.GetType() == rightOperand.GetType()) eq = leftOperand.Equals(rightOperand);
            else throw new ExpressionException($"Vergleich '{op}' zwischen {Describe(leftOperand)} und {Describe(rightOperand)} nicht erlaubt.");
            return op == "==" ? eq : !eq;
        }

        private static object EvaluateIntegers(string op, long la, long lb)
            => op switch
            {
                "+" => la + lb,
                "-" => la - lb,
                "*" => la * lb,
                "/" => lb == 0 ? throw new ExpressionException("Division durch 0.") : la / lb,
                "%" => lb == 0 ? throw new ExpressionException("Division durch 0.") : la % lb,
                _ => throw new ExpressionException($"Unbekannter Operator '{op}'."),
            };

        private static object EvaluateFloatingPoint(string op, double leftOperand, double rightOperand)
            => op switch
            {
                "+" => leftOperand + rightOperand,
                "-" => leftOperand - rightOperand,
                "*" => leftOperand * rightOperand,
                "/" => leftOperand / rightOperand,
                "%" => leftOperand % rightOperand,
                _ => throw new ExpressionException($"Unbekannter Operator '{op}'."),
            };
    }

    private static object Normalize(object value) => value switch
    {
        int index => (long)index,
        float floatingPointValue => (double)floatingPointValue,
        _ => value,
    };

    private static bool IsNumber(object value) => value is long or double;

    private static double ToDouble(object value) => value is long integerValue ? integerValue : (double)value;

    private static string Describe(object value) => $"{TypedValue.TypeOf(value)?.ToString() ?? value.GetType().Name} ({TypedValue.Format(value)})";
}
