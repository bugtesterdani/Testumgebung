/**
 * @file
 * @brief Parst mathematische Formeln für den Kurvengenerator; Auswertung und Formatfehler werden explizit behandelt.
 */

using System.Globalization;

namespace Testumgebung.Domain.Curves;

public sealed class CurveFormulaException(string message) : Exception(message);

/// <summary>
/// Formeln für den Kurvengenerator, z. B. <c>325*sin(2*pi*50*t - 2*pi/3)</c>. Variablen: <c>t</c> (Sekunden), <c>pi</c>, <c>e</c>.
/// Funktionen: sin, cos, tan, asin, acos, atan, sqrt, abs, exp, ln, log10, floor, ceil, round, sign, min, max, pow,
/// mod, clamp(x, a, b), step(x) (0 für x &lt; 0, sonst 1). Operatoren: + − * / ^ und Klammern.
/// </summary>
public static class CurveFormula
{
    public static Func<double, double> Compile(string source)
    {
        var p = new Parser(source);
        var f = p.ParseExpression();
        p.ExpectEnd();
        // Probelauf: Syntax- und Laufzeitfehler früh melden
        _ = f(0);
        return f;
    }

    private sealed class Parser(string src)
    {
        private int _pos;

        public Func<double, double> ParseExpression()
        {
            var left = ParseTerm();
            while (true)
            {
                Skip();
                if (Peek('+')) { _pos++; var l = left; var r = ParseTerm(); left = t => l(t) + r(t); }
                else if (Peek('-')) { _pos++; var l = left; var r = ParseTerm(); left = t => l(t) - r(t); }
                else return left;
            }
        }

        private Func<double, double> ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                Skip();
                if (Peek('*')) { _pos++; var l = left; var r = ParseUnary(); left = t => l(t) * r(t); }
                else if (Peek('/')) { _pos++; var l = left; var r = ParseUnary(); left = t => l(t) / r(t); }
                else return left;
            }
        }

        private Func<double, double> ParseUnary()
        {
            Skip();
            if (Peek('-')) { _pos++; var x = ParseUnary(); return t => -x(t); }
            if (Peek('+')) { _pos++; return ParseUnary(); }
            return ParsePower();
        }

        private Func<double, double> ParsePower()
        {
            var b = ParsePrimary();
            Skip();
            if (!Peek('^')) return b;
            _pos++;
            var e = ParseUnary(); // rechtsassoziativ
            return t => Math.Pow(b(t), e(t));
        }

        private Func<double, double> ParsePrimary()
        {
            Skip();
            if (_pos >= src.Length) throw Error("Ausdruck unvollständig");
            var c = src[_pos];
            if (c == '(')
            {
                _pos++;
                var inner = ParseExpression();
                Expect(')');
                return inner;
            }
            if (char.IsDigit(c) || c == '.')
            {
                return ParseNumber();
            }
            if (char.IsLetter(c))
            {
                return ParseNamedValue();
            }
            throw Error($"Unerwartetes Zeichen '{c}'");
        }

        private Func<double, double> ParseNumber()
        {
            var start = _pos;
            while (_pos < src.Length && (char.IsDigit(src[_pos]) || src[_pos] == '.')) _pos++;
            ParseExponent();
            var text = src[start.._pos];
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw Error($"Zahl '{text}' ungültig");
            return _ => v;
        }

        private void ParseExponent()
        {
            if (_pos + 1 >= src.Length || src[_pos] is not ('e' or 'E')) return;
            var next = src[_pos + 1];
            if (!char.IsDigit(next) && next is not ('-' or '+')) return;
            _pos += 2;
            while (_pos < src.Length && char.IsDigit(src[_pos])) _pos++;
        }

        private Func<double, double> ParseNamedValue()
        {
            var start = _pos;
            while (_pos < src.Length && (char.IsLetterOrDigit(src[_pos]) || src[_pos] == '_')) _pos++;
            var name = src[start.._pos].ToLowerInvariant();
            Skip();
            if (Peek('('))
            {
                _pos++;
                var args = new List<Func<double, double>>();
                Skip();
                if (!Peek(')'))
                {
                    args.Add(ParseExpression());
                    Skip();
                    while (Peek(',')) { _pos++; args.Add(ParseExpression()); Skip(); }
                }
                Expect(')');
                return Function(name, args);
            }
            return name switch
            {
                "t" => t => t,
                "pi" => _ => Math.PI,
                "e" => _ => Math.E,
                _ => throw Error($"Unbekannter Name '{name}' (erlaubt: t, pi, e)"),
            };
        }

        private Func<double, double> Function(string name, List<Func<double, double>> a)
        {
            void Args(int n)
            {
                if (a.Count != n) throw Error($"{name} erwartet {n} Argument(e)");
            }
            switch (name)
            {
                case "min": Args(2); return t => Math.Min(a[0](t), a[1](t));
                case "max": Args(2); return t => Math.Max(a[0](t), a[1](t));
                case "pow": Args(2); return t => Math.Pow(a[0](t), a[1](t));
                case "mod": Args(2); return t => { var m = a[1](t); var r = a[0](t) % m; return r < 0 ? r + m : r; };
                case "clamp": Args(3); return t => Math.Clamp(a[0](t), a[1](t), a[2](t));
            }
            Args(1);
            var x = a[0];
            if (!UnaryFunctions.TryGetValue(name, out var f))
                throw Error($"Unbekannte Funktion '{name}'");
            return t => f(x(t));
        }

        private static readonly IReadOnlyDictionary<string, Func<double, double>> UnaryFunctions =
            new Dictionary<string, Func<double, double>>(StringComparer.Ordinal)
            {
                ["sin"] = Math.Sin,
                ["cos"] = Math.Cos,
                ["tan"] = Math.Tan,
                ["asin"] = Math.Asin,
                ["acos"] = Math.Acos,
                ["atan"] = Math.Atan,
                ["sqrt"] = Math.Sqrt,
                ["abs"] = Math.Abs,
                ["exp"] = Math.Exp,
                ["ln"] = Math.Log,
                ["log10"] = Math.Log10,
                ["floor"] = Math.Floor,
                ["ceil"] = Math.Ceiling,
                ["round"] = v => Math.Round(v, MidpointRounding.AwayFromZero),
                ["sign"] = v => Math.Sign(v),
                ["step"] = v => v < 0 ? 0 : 1,
            };

        public void ExpectEnd()
        {
            Skip();
            if (_pos < src.Length) throw Error($"Unerwartetes '{src[_pos..]}'");
        }

        private void Expect(char c)
        {
            Skip();
            if (!Peek(c)) throw Error($"'{c}' erwartet");
            _pos++;
        }

        private bool Peek(char c) => _pos < src.Length && src[_pos] == c;

        private void Skip()
        {
            while (_pos < src.Length && char.IsWhiteSpace(src[_pos])) _pos++;
        }

        private CurveFormulaException Error(string message) => new($"Formel: {message} (Position {_pos + 1}).");
    }
}
