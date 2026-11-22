using System.Text.RegularExpressions;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabModifierGrammar
{
    internal static void Barcode(CabBindingBuilder builder, CabSourceRange range)
    {
        builder.Add("options", range);
        var text = builder.Slice.Protocol(range);
        foreach (Match match in Regex.Matches(text, @"\+(?<name>VERSION|MODEL|MODE|ROWS|COLS|MOD|WS|EL|CC)(?<value>[0-9]+|[LMQH])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
        {
            var value = match.Groups["value"];
            builder.Add(match.Groups["name"].Value.ToUpperInvariant(), builder.Slice.Subrange(range, value.Index, value.Length));
        }
        foreach (Match match in Regex.Matches(text, @"\+(?:GOODBAD|VERIFY|EXTERN)[^+]*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            Verifier(builder, builder.Slice.Subrange(range, match.Index, match.Length), true);
    }

    internal static void Verifier(CabBindingBuilder builder, CabSourceRange range, bool plus)
    {
        var slice = builder.Slice;
        var text = slice.Protocol(range);
        var start = plus ? 1 : 0;
        var name = new[] { "GOODBAD", "VERIFY", "EXTERN" }.FirstOrDefault(n => text.AsSpan(start).StartsWith(n, StringComparison.OrdinalIgnoreCase));
        if (name is null) return;
        builder.Add(name, range);
        var length = name.Length + start;
        if (text.Length == length) return;
        var fields = slice.Split(slice.Subrange(range, length, text.Length - length), ':');
        foreach (var part in new[] { ("a", 0), ("b", 1), ("c", 2) }) builder.AddAt(name + "." + part.Item1, fields, part.Item2);
        if (fields.Count > 3) builder.Issue("verifier-parameter", "Prüfoption hat zu viele Teilparameter.", fields[3]);
    }

    internal static void Graphic(CabBindingBuilder builder, CabSourceRange range)
    {
        builder.Add("options", range);
        var text = builder.Slice.Protocol(range);
        foreach (Match match in Regex.Matches(text, @"\[(?<kind>D|F|S):(?<values>[^\]]*)\]", RegexOptions.CultureInvariant))
        {
            var values = match.Groups["values"];
            var valueRange = builder.Slice.Subrange(range, values.Index, values.Length);
            var fields = builder.Slice.Split(valueRange, ',');
            var names = match.Groups["kind"].Value switch
            {
                "D" => new[] { "D.line", "D.gap" }, "F" => ["F.value"], "S" => ["S.start", "S.end", "S.direction"], _ => []
            };
            for (var i = 0; i < names.Length; i++) builder.AddAt(names[i], fields, i);
            if (fields.Count > names.Length) builder.Issue("graphic-option", "Grafikoption hat zu viele Teilparameter.", fields[names.Length]);
        }
    }
}
