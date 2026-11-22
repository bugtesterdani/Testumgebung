using System.Text.RegularExpressions;

namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>Proposes small syntax insertions, then proves their binding with the real grammar.</summary>
public static class CabOptionalParameter
{
    public static CabSourceEditor Add(CabSourceEditor editor, CabBoundCommand command, CabPropertyDefinition definition, string value)
    {
        if (!definition.IsEnabled || definition.Control == CabPropertyControl.Resource)
            throw new InvalidOperationException("Dieser Parameter ist hier keine verfügbare Texteingabe.");
        var error = definition.Rule.Validate(value, definition.Generation);
        if (error is not null) throw new ArgumentException(error, nameof(value));
        var original = editor.Document.Bytes(command.Syntax.Range);
        var positions = Positions(original).ToArray();
        var attempts = 0;
        foreach (var fragment in Fragments(command, definition, value).Distinct(StringComparer.Ordinal))
        foreach (var at in positions)
        {
            if (++attempts > 4096) throw new InvalidOperationException("Keine eindeutige Ergänzung gefunden. Zugehörige Option oder passende Syntaxvariante zuerst wählen.");
            var encoded = CabEncoding.Get(command.Syntax.CodePage).GetBytes(fragment);
            var bytes = original[..at].Concat(encoded).Concat(original[at..]).ToArray();
            var verified = VerifyCandidate(command, definition.Name, value, bytes);
            if (verified is not null) return ReplaceDifference(editor, command.Syntax.Range.Offset, original, verified);
        }
        throw new InvalidOperationException("Der Parameter gehört zu einer anderen Syntaxvariante. Zuerst die zugehörige Option, Schrift oder Barcodeart wählen.");
    }

    private static byte[]? VerifyCandidate(CabBoundCommand command, string name, string value, byte[] bytes)
    {
        var candidate = TryBind(command, bytes);
        if (candidate is null) return null;
        var target = candidate.Parameter(name);
        if (target?.IsPresent != true || target.Parts.Count == 0 || target.Value is null) return null;
        if (target.Value != value)
        {
            var document = CabParser.Parse(bytes, new(command.Syntax.CodePage));
            bytes = document.Write(CabParameterEditor.Replace(document, target, value));
            candidate = TryBind(command, bytes);
            if (candidate?.Parameter(name)?.Value != value) return null;
        }
        return Preserves(command, candidate, name) ? bytes : null;
    }

    private static IEnumerable<int> Positions(byte[] bytes)
    {
        for (var index = bytes.Length; index >= 0; index--)
            if (index == bytes.Length || index == 0 || Boundary(bytes[index]) || Boundary(bytes[index - 1])) yield return index;
    }

    private static bool Boundary(byte value) => value is (byte)',' or (byte)';' or (byte)':' or (byte)'+' or (byte)'[' or (byte)']' or 32 or 9;

    private static IEnumerable<string> Fragments(CabBoundCommand command, CabPropertyDefinition definition, string value)
    {
        var spelling = Spell(definition.Rule.Parameter.Spelling, definition.Name, value);
        var seeds = new[] { value, spelling, spelling.Trim('[', ']') }.Concat(OptionExamples(command));
        foreach (var seed in seeds.Where(s => s.Length > 0))
            foreach (var separator in new[] { "", ",", ";", ":", " ", ",,", "+" }) yield return separator + seed;
        if (definition.Name == "VAR") yield return "[" + value + "]";
        if (definition.Name == "U.xxxx") yield return "[U:" + value + "]";
        var parentName = definition.Name.Split('.')[0];
        var parent = command.Definition.Parameters.FirstOrDefault(p => p.Parameter.Name == parentName);
        if (parentName != definition.Name && parent is not null)
        {
            var example = CabPropertyTemplates.Example(parent.Entry.Id, parentName);
            foreach (var separator in new[] { ",", ";", ":", " " }) yield return separator + example + value;
        }
    }

    private static string Spell(string spelling, string name, string value)
    {
        var leaf = name.Split('.')[^1];
        foreach (var word in new[] { name, leaf, "<" + name + ">" }.Distinct())
            spelling = Regex.Replace(spelling, @"(?<![\p{L}\p{Nd}])" + Regex.Escape(word) + @"(?![\p{L}\p{Nd}])", _ => value, RegexOptions.CultureInvariant);
        return Regex.Replace(spelling, @"(?:%[12]|[nxy])(?=[\];,]*$)", _ => value, RegexOptions.CultureInvariant);
    }

    private static IEnumerable<string> OptionExamples(CabBoundCommand command)
        => command.Definition.Parameters.Where(p => p.ChoiceMode is CabChoiceMode.CommaList or CabChoiceMode.PlusList or CabChoiceMode.GraphicList || p.Parameter.Name == "param")
            .SelectMany(p => p.Parameter.Values.Select(v => CabPropertyTemplates.Example(p.Entry.Id, p.Parameter.Name, v.Value)));

    private static CabBoundCommand? TryBind(CabBoundCommand original, byte[] bytes)
    {
        var document = CabParser.Parse(bytes, new(original.Syntax.CodePage));
        var commands = CabSemanticBinder.Bind(document, new(document));
        return commands.FirstOrDefault(c => c.Syntax.Command == original.Syntax.Command && c.Definition.Catalog.Id == original.Definition.Catalog.Id
            && c.Parameters.All(p => !p.IsPresent || p.Value is not null || p.Rule.DataType == CabDataType.Binary));
    }

    private static bool Preserves(CabBoundCommand original, CabBoundCommand candidate, string changed)
    {
        foreach (var parameter in original.Parameters.Where(p => p.IsPresent && p.Value is not null))
        {
            var name = parameter.Rule.Parameter.Name;
            if (name == changed || Aggregate(parameter.Rule, changed)) continue;
            if (candidate.Parameter(name)?.Value != parameter.Value) return false;
        }
        return true;
    }

    private static bool Aggregate(CabParameterRule rule, string changed)
        => rule.ChoiceMode is CabChoiceMode.CommaList or CabChoiceMode.PlusList or CabChoiceMode.GraphicList
            || rule.Parameter.Name is "param" or "ao" || changed.StartsWith(rule.Parameter.Name + ".", StringComparison.Ordinal);

    private static CabSourceEditor ReplaceDifference(CabSourceEditor editor, int offset, byte[] before, byte[] after)
    {
        var first = 0;
        while (first < before.Length && first < after.Length && before[first] == after[first]) first++;
        var oldEnd = before.Length; var newEnd = after.Length;
        while (oldEnd > first && newEnd > first && before[oldEnd - 1] == after[newEnd - 1]) { oldEnd--; newEnd--; }
        return new(editor.Document.Write([new(new(offset + first, oldEnd - first), after[first..newEnd])]), editor.SetupCodePage, editor.Settings);
    }
}
