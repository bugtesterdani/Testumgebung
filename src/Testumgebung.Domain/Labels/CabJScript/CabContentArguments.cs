namespace Testumgebung.Domain.Labels.CabJScript;

public sealed record CabContentArgument(CabCatalogParameter Parameter, string Value)
{
    /// <summary>UTF-16 position in the decoded content parameter, not a byte offset.</summary>
    public int Offset { get; init; } = -1;
    public int Length => Value.Length;
}

public static class CabContentArguments
{
    private static readonly IReadOnlyDictionary<int, Action<Builder>> Recipes = new Dictionary<int, Action<Builder>>
    {
        [53]=b => b.Price(), [66]=b => b.Prompt(), [68]=b => b.Bases(), [76]=b => b.Bits(), [82]=b => b.Alignment(),
        [88]=b => b.Add("name", b.Field.Name),
        [89]=b => { b.Add("name", b.Field.Name); b.At("m", 0); b.At("n", 1); },
        [90]=b => b.OpcUa(), [91]=b => b.OpcUa(), [96]=b => b.Selection(),
        [97]=b => { b.At("x", 0); b.At("inc", 1); b.At("freq", 2); },
        [98]=b => b.Hash()
    };

    public static IReadOnlyList<CabContentArgument> Bind(CabContentField contentField)
    {
        var builder = new Builder(contentField);
        if (Recipes.TryGetValue(CabContentGrammar.Number(contentField), out var recipe)) recipe(builder);
        else builder.Ordinary();
        return builder.Values.AsReadOnly();
    }

    private sealed class Builder(CabContentField contentField)
    {
        internal CabContentField Field => contentField;
        private readonly IReadOnlyList<string> _args = CabContentGrammar.Arguments(contentField);
        private int _cursor;
        private bool _argumentLocated;
        internal List<CabContentArgument> Values { get; } = [];
        internal void Add(string name, string value)
        {
            var relative = Locate(name, value);
            Values.Add(new(contentField.Definition.Parameters.Single(p => p.Name == name), value)
                { Offset = relative < 0 ? -1 : contentField.Offset + relative });
        }

        private int Locate(string name, string value)
        {
            var id = CabContentGrammar.Number(contentField);
            if (id is 88 or 89 && name == "name") return 1;
            if (id is 90 or 91 && name == "x") return 1 + contentField.Name.IndexOf('-') + 1;
            if (name is "source.characters" or "target.characters")
            {
                var parent = Values.First(v => v.Parameter.Name == name.Split('.')[0]);
                return parent.Offset - contentField.Offset + 2;
            }
            var found = FindArgument(value);
            if (found < 0) return -1;
            _cursor = found + value.Length;
            return 1 + contentField.Name.Length + 1 + found;
        }

        private int FindArgument(string value)
        {
            var argument = contentField.Argument ?? "";
            var start = Math.Min(_cursor, argument.Length);
            // An empty positional argument starts after its separator, not at
            // the end of the preceding value. Consecutive empty slots each advance.
            if (value.Length == 0 && _argumentLocated && start < argument.Length && argument[start] == ',') start++;
            var found = argument.IndexOf(value, start, StringComparison.Ordinal);
            if (found >= 0) _argumentLocated = true;
            return found;
        }
        internal void At(string name, int position) { if (position < _args.Count) Add(name, _args[position]); }

        internal void Ordinary()
        {
            for (var i = 0; i < _args.Count && contentField.Definition.Parameters.Count > 0; i++)
                Add(contentField.Definition.Parameters[System.Math.Min(i, contentField.Definition.Parameters.Count - 1)].Name, _args[i]);
        }

        internal void Price()
        {
            At("value", 0);
            if (_args.Count < 2) return;
            string[] names = ["t", "d", "o"];
            for (var i = 0; i < System.Math.Min(_args[1].Length, 3); i++) Add(names[i], _args[1][i].ToString());
        }

        internal void Prompt()
        {
            At("x", 0); At("y", 1); At("z", 2);
            foreach (var option in _args.Skip(3))
            {
                if (option is "D" or "R" or "J") Add(option, option);
                else if (option.StartsWith('L')) Add("Lx", option[1..]);
                else if (option.StartsWith('M')) Add("Mx", option[1..]);
            }
        }

        internal void Bases()
        {
            At("source", 0); At("target", 1); At("fieldname", 2);
            if (_args.Count > 0 && _args[0].StartsWith("U:", StringComparison.Ordinal)) Add("source.characters", _args[0][2..]);
            if (_args.Count > 1 && _args[1].StartsWith("U:", StringComparison.Ordinal)) Add("target.characters", _args[1][2..]);
        }

        internal void Bits()
        {
            if (_args.Count > 0) Repeated(_args[0].Split(','), "bits1", "bitsx");
            if (_args.Count > 1) Repeated(_args[1].Split(','), "val1", "valx");
        }

        private void Repeated(IReadOnlyList<string> args, string first, string rest)
        {
            for (var i = 0; i < args.Count; i++) Add(i == 0 ? first : rest, args[i]);
        }

        internal void Alignment()
        {
            if (_args.Count == 0 || _args[0].Length == 0) return;
            Add("m", _args[0][..1]); Add("l", _args[0][1..]);
        }

        internal void OpcUa()
        {
            if (contentField.Name.Contains('-')) Add("x", contentField.Name[(contentField.Name.IndexOf('-') + 1)..]);
            At("request", 0);
        }

        internal void Selection()
        {
            At("text", 0); At("name", 1); At("idx", 2); At("x", 3);
            foreach (var option in _args.Skip(4).Where(o => o is "D" or "R" or "J")) Add(option, option);
        }

        internal void Hash()
        {
            At("x", 0);
            if (_args.Count > 1 && _args[1].StartsWith("C:", StringComparison.Ordinal)) Add("charset", _args[1][2..]);
        }
    }
}
