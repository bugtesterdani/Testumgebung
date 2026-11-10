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
        var parser = new Parser(source);
        var callback = parser.ParseExpression();
        parser.ExpectEnd();
        // Probelauf: Syntax- und Laufzeitfehler früh melden
        _ = callback(0);
        return callback;
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
                if (Peek('+')) { _pos++; var left2 = left; var callback = ParseTerm(); left = time => left2(time) + callback(time); }
                else if (Peek('-')) { _pos++; var left3 = left; var callback2 = ParseTerm(); left = time => left3(time) - callback2(time); }
                else return left;
            }
        }

        private Func<double, double> ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                Skip();
                if (Peek('*')) { _pos++; var left2 = left; var callback = ParseUnary(); left = time => left2(time) * callback(time); }
                else if (Peek('/')) { _pos++; var left3 = left; var callback2 = ParseUnary(); left = time => left3(time) / callback2(time); }
                else return left;
            }
        }

        private Func<double, double> ParseUnary()
        {
            Skip();
            if (Peek('-')) { _pos++; var callback = ParseUnary(); return time => -callback(time); }
            if (Peek('+')) { _pos++; return ParseUnary(); }
            return ParsePower();
        }

        private Func<double, double> ParsePower()
        {
            var callback = ParsePrimary();
            Skip();
            if (!Peek('^')) return callback;
            _pos++;
            var callback2 = ParseUnary(); // rechtsassoziativ
            return time => Math.Pow(callback(time), callback2(time));
        }

        private Func<double, double> ParsePrimary()
        {
            Skip();
            if (_pos >= src.Length) throw Error("Ausdruck unvollständig");
            var character = src[_pos];
            if (character == '(')
            {
                _pos++;
                var inner = ParseExpression();
                Expect(')');
                return inner;
            }
            if (char.IsDigit(character) || character == '.')
            {
                return ParseNumber();
            }
            if (char.IsLetter(character))
            {
                return ParseNamedValue();
            }
            throw Error($"Unerwartetes Zeichen '{character}'");
        }

        private Func<double, double> ParseNumber()
        {
            var start = _pos;
            while (_pos < src.Length && (char.IsDigit(src[_pos]) || src[_pos] == '.')) _pos++;
            ParseExponent();
            var text = src[start.._pos];
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw Error($"Zahl '{text}' ungültig");
            return _ => value;
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
                "t" => time => time,
                "pi" => _ => Math.PI,
                "e" => _ => Math.E,
                _ => throw Error($"Unbekannter Name '{name}' (erlaubt: t, pi, e)"),
            };
        }

        private Func<double, double> Function(string name, List<Func<double, double>> items)
        {
            void Args(int count)
            {
                if (items.Count != count) throw Error($"{name} erwartet {count} Argument(e)");
            }
            switch (name)
            {
                case "min": Args(2); return time => Math.Min(items[0](time), items[1](time));
                case "max": Args(2); return time => Math.Max(items[0](time), items[1](time));
                case "pow": Args(2); return time => Math.Pow(items[0](time), items[1](time));
                case "mod": Args(2); return time => { var measurement = items[1](time); var ratio = items[0](time) % measurement; return ratio < 0 ? ratio + measurement : ratio; };
                case "clamp": Args(3); return time => Math.Clamp(items[0](time), items[1](time), items[2](time));
            }
            Args(1);
            var callback = items[0];
            if (!UnaryFunctions.TryGetValue(name, out var callback2))
                throw Error($"Unbekannte Funktion '{name}'");
            return time => callback2(callback(time));
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
                ["round"] = value => Math.Round(value, MidpointRounding.AwayFromZero),
                ["sign"] = value => Math.Sign(value),
                ["step"] = value => value < 0 ? 0 : 1,
            };

        public void ExpectEnd()
        {
            Skip();
            if (_pos < src.Length) throw Error($"Unerwartetes '{src[_pos..]}'");
        }

        private void Expect(char expectedCharacter)
        {
            Skip();
            if (!Peek(expectedCharacter)) throw Error($"'{expectedCharacter}' erwartet");
            _pos++;
        }

        private bool Peek(char expectedCharacter) => _pos < src.Length && src[_pos] == expectedCharacter;

        private void Skip()
        {
            while (_pos < src.Length && char.IsWhiteSpace(src[_pos])) _pos++;
        }

        private CurveFormulaException Error(string message) => new($"Formel: {message} (Position {_pos + 1}).");
    }
}
