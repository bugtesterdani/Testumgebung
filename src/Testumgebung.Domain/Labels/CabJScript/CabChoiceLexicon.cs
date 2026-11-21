using System.Globalization;
using System.Text.RegularExpressions;

namespace Testumgebung.Domain.Labels.CabJScript;

public enum CabChoiceMode { None, Single, CommaList, PlusList, GraphicList }

internal static class CabChoiceLexicon
{
    private const string Number = @"[+-]?[0-9]+(?:\.[0-9]+)?";
    private static readonly IReadOnlyDictionary<string, string> Templates = new Dictionary<string, string>
    {
        ["frn"] = "fr" + Number, ["fln"] = "fl" + Number, ["fun"] = "fu" + Number, ["fdn"] = "fd" + Number,
        ["qn"] = "q" + Number, ["hn"] = "h" + Number, ["mn"] = "m" + Number,
        ["+WSn"] = @"\+WS[0-9]+", ["+ELx"] = @"\+EL[0-9]+", ["+CCn"] = @"\+CC[0-9]+",
        ["+ROWSx"] = @"\+ROWS[0-9]+", ["+COLSx"] = @"\+COLS[0-9]+", ["+MODELx"] = @"\+MODEL[0-9]+",
        ["+MODEx"] = @"\+MODE[0-9]+", ["+VERSIONx"] = @"\+VERSION[0-9]+", ["+MODxx"] = @"\+MOD[0-9]+",
        ["[D:line,gap]"] = @"\[D:" + Number + "," + Number + @"\]",
        ["[F:value]"] = @"\[F:[^\[\]]+\]",
        ["[ECE:...]"] = @"\[ECE:[^\[\]]+\]",
        ["[APPEND:m,n,id1,id2]"] = @"\[APPEND:[0-9]+,[0-9]+,[^,\[\]]+,[^,\[\]]+\]",
        ["[APPEND:x,id]"] = @"\[APPEND:[0-9]+,[^,\[\]]+\]",
        ["[U:xxxx]"] = @"\[U:[^\[\]]+\]",
        ["[S:%1]"] = @"\[S:" + Number + @"\]",
        ["[S:%1,%2]"] = @"\[S:" + Number + "," + Number + @"\]",
        ["[S:%1,%2,direction]"] = @"\[S:" + Number + "," + Number + "," + Number + @"\]"
    };

    internal static bool Matches(CabParameterRule rule, string input, CabChoiceMode mode)
    {
        var tokens = mode switch
        {
            CabChoiceMode.CommaList => input.Split(','),
            CabChoiceMode.PlusList => PlusTokens(input),
            CabChoiceMode.GraphicList => GraphicTokens(input),
            _ => [input]
        };
        return tokens.Length > 0 && tokens.All(token => MatchesSingle(rule, token));
    }

    private static bool MatchesSingle(CabParameterRule rule, string input)
    {
        foreach (var value in rule.Parameter.Values.Select(v => v.Value).Concat(rule.AdditionalValues))
        {
            var pattern = Template(value);
            if (pattern is not null)
            {
                if (Regex.IsMatch(input, "^(?:" + pattern + ")$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return true;
            }
            else if (value == input || rule.NumericChoices && SameNumber(value, input)) return true;
        }
        return false;
    }

    private static bool SameNumber(string expected, string actual)
        => decimal.TryParse(expected, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var left)
            && decimal.TryParse(actual, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var right) && left == right;

    private static string? Template(string value)
    {
        if (Templates.TryGetValue(value, out var pattern)) return pattern;
        if (value == "<num>") return "[0-9]+";
        if (value == "[<VAR>]") return @"\[\p{L}[\p{L}\p{Nd}]*\]";
        if (value == "[PREVIEW[:y]]") return @"\[PREVIEW(?::(?:[0-9]+|ALL))?\]";
        if (value.EndsWith(";name", StringComparison.Ordinal)) return Regex.Escape(value[..^5]);
        foreach (var verifier in new[] { "GOODBAD", "VERIFY", "EXTERN" })
            if (value.StartsWith("+" + verifier + "a", StringComparison.Ordinal))
                return @"\+" + verifier + "(?:" + Number + "(?::" + Number + "(?::[0-9]+)?)?)?";
        return null;
    }

    internal static bool IsSchematic(string value) => Template(value) is not null;

    private static string[] PlusTokens(string input)
    {
        if (!input.StartsWith('+') || input.EndsWith('+')) return [];
        return input[1..].Split('+').Select(p => "+" + p).ToArray();
    }

    private static string[] GraphicTokens(string input)
    {
        var tokens = new List<string>();
        var start = 0;
        var depth = 0;
        var needsToken = true;
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '[') depth++;
            else if (input[i] == ']')
            {
                if (--depth < 0) return [];
                if (depth == 0) { tokens.Add(input[start..(i + 1)]); start = i + 1; needsToken = false; }
            }
            else if (depth == 0 && input[i] == ',')
            {
                if (needsToken) return [];
                start = i + 1; needsToken = true;
            }
            else if (depth == 0) return [];
        }
        return depth == 0 && start == input.Length && !needsToken ? tokens.ToArray() : [];
    }
}
