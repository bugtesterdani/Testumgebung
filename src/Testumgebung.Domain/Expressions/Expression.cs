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

    public static Expression Parse(string source)
    {
        var parser = new Parser(Lexer.Tokenize(source));
        var node = parser.ParseExpression();
        parser.ExpectEnd();
        return new Expression(source, node);
    }

    public object Evaluate(IValueResolver resolver) => _root.Eval(resolver);

    public bool EvaluateBool(IValueResolver resolver)
        => Evaluate(resolver) is bool b
            ? b
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
            var i = 0;
            void ReadNumber()
            {
                var start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                {
                    i++;
                    if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }
                list.Add(new Token(T.Number, s[start..i], start));
            }

            void ReadString()
            {
                var start = i++;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; }
                    else sb.Append(s[i++]);
                }
                if (i >= s.Length) throw new ExpressionException($"Nicht abgeschlossene Zeichenkette ab Position {start}.");
                i++;
                list.Add(new Token(T.String, sb.ToString(), start));
            }

            void ReadName()
            {
                var start = i;
                while (i + 2 < s.Length && s[i] == '.' && s[i + 1] == '.' && s[i + 2] == '/') i += 3;
                if (i >= s.Length || !(char.IsLetter(s[i]) || s[i] == '_'))
                    throw new ExpressionException($"Variablenname erwartet an Position {i}.");
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.')) i++;
                list.Add(new Token(T.Ident, s[start..i], start));
            }

            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (char.IsDigit(c))
                {
                    ReadNumber();
                    continue;
                }
                if (c == '"')
                {
                    ReadString();
                    continue;
                }
                if (char.IsLetter(c) || c == '_' || (c == '.' && i + 2 < s.Length && s[i + 1] == '.' && s[i + 2] == '/'))
                {
                    ReadName();
                    continue;
                }
                if (c == '(') { list.Add(new Token(T.LParen, "(", i++)); continue; }
                if (c == ')') { list.Add(new Token(T.RParen, ")", i++)); continue; }
                var op = Ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
                if (op is null) throw new ExpressionException($"Unerwartetes Zeichen '{c}' an Position {i}.");
                list.Add(new Token(T.Op, op, i));
                i += op.Length;
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
            var t = Cur;
            switch (t.Kind)
            {
                case T.Number:
                    _p++;
                    if (t.Text.Contains('.') || t.Text.Contains('e') || t.Text.Contains('E'))
                        return double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                            ? new Literal(d)
                            : throw new ExpressionException($"Ungültige Zahl '{t.Text}'.");
                    return long.TryParse(t.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                        ? new Literal(l)
                        : throw new ExpressionException($"Ungültige Zahl '{t.Text}'.");
                case T.String:
                    _p++;
                    return new Literal(t.Text);
                case T.Ident:
                    _p++;
                    return t.Text switch
                    {
                        "true" => new Literal(true),
                        "false" => new Literal(false),
                        _ => new Variable(t.Text),
                    };
                case T.LParen:
                    _p++;
                    var inner = ParseExpression();
                    if (Cur.Kind != T.RParen) throw new ExpressionException($"')' erwartet an Position {Cur.Pos}.");
                    _p++;
                    return inner;
                default:
                    throw new ExpressionException(t.Kind == T.End
                        ? "Ausdruck unvollständig."
                        : $"Unerwartetes '{t.Text}' an Position {t.Pos}.");
            }
        }
    }

    // ---------------------------------------------------------------- AST

    private abstract class Node
    {
        public abstract object Eval(IValueResolver r);
        public virtual IEnumerable<string> Names() => [];
    }

    private sealed class Literal(object value) : Node
    {
        public override object Eval(IValueResolver r) => value;
    }

    private sealed class Variable(string name) : Node
    {
        public override object Eval(IValueResolver r) => Normalize(r.Resolve(name));
        public override IEnumerable<string> Names() => [name];
    }

    private sealed class Unary(string op, Node operand) : Node
    {
        public override object Eval(IValueResolver r)
        {
            var v = operand.Eval(r);
            return (op, v) switch
            {
                ("!", bool b) => !b,
                ("-", long l) => -l,
                ("-", double d) => -d,
                _ => throw new ExpressionException($"Operator '{op}' nicht anwendbar auf {Describe(v)}."),
            };
        }

        public override IEnumerable<string> Names() => operand.Names();
    }

    private sealed class Binary(string op, Node left, Node right) : Node
    {
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

            var a = left.Eval(r);
            var b = right.Eval(r);

            return EvaluateValues(op, a, b);
        }

        private static object EvaluateValues(string op, object a, object b)
        {
            if (op is "==" or "!=")
            {
                return EvaluateEquality(op, a, b);
            }

            if (op is "<" or "<=" or ">" or ">=")
            {
                return EvaluateComparison(op, a, b);
            }

            return EvaluateArithmetic(op, a, b);
        }

        private static object EvaluateArithmetic(string op, object a, object b)
        {
            if (op == "+" && a is string s1 && b is string s2) return s1 + s2;

            if (!IsNumber(a) || !IsNumber(b))
                throw new ExpressionException($"Operator '{op}' nicht anwendbar auf {Describe(a)} und {Describe(b)}.");

            if (a is long la && b is long lb)
            {
                return EvaluateIntegers(op, la, lb);
            }

            return EvaluateFloatingPoint(op, ToDouble(a), ToDouble(b));
        }

        private static bool EvaluateComparison(string op, object a, object b)
        {
            int cmp;
            if (IsNumber(a) && IsNumber(b)) cmp = ToDouble(a).CompareTo(ToDouble(b));
            else if (a is string sa && b is string sb) cmp = string.CompareOrdinal(sa, sb);
            else if (a is DateTime da && b is DateTime db) cmp = da.CompareTo(db);
            else throw new ExpressionException($"Vergleich '{op}' zwischen {Describe(a)} und {Describe(b)} nicht erlaubt.");
            return op switch { "<" => cmp < 0, "<=" => cmp <= 0, ">" => cmp > 0, _ => cmp >= 0 };
        }

        private static bool EvaluateEquality(string op, object a, object b)
        {
            bool eq;
            if (IsNumber(a) && IsNumber(b)) eq = ToDouble(a) == ToDouble(b);
            else if (a.GetType() == b.GetType()) eq = a.Equals(b);
            else throw new ExpressionException($"Vergleich '{op}' zwischen {Describe(a)} und {Describe(b)} nicht erlaubt.");
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

        private static object EvaluateFloatingPoint(string op, double x, double y)
            => op switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                "/" => x / y,
                "%" => x % y,
                _ => throw new ExpressionException($"Unbekannter Operator '{op}'."),
            };
    }

    private static object Normalize(object v) => v switch
    {
        int i => (long)i,
        float f => (double)f,
        _ => v,
    };

    private static bool IsNumber(object v) => v is long or double;

    private static double ToDouble(object v) => v is long l ? l : (double)v;

    private static string Describe(object v) => $"{TypedValue.TypeOf(v)?.ToString() ?? v.GetType().Name} ({TypedValue.Format(v)})";
}
