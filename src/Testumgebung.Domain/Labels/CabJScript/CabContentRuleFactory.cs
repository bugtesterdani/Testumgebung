using System.Globalization;

namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>Chapter-5 parameter tables, separate from command parameters with the same names.</summary>
internal static class CabContentRuleFactory
{
    private static readonly HashSet<int> IntegerFields = [56, 59, 60, 63, 65, 78, 92, 94, 97];
    private static readonly HashSet<int> ChoiceFields = [54, 62, 64, 95];
    private static readonly HashSet<(int, string)> IntegerParameters =
    [ (66,"z"), (66,"Lx"), (76,"bits1"), (76,"bitsx"), (77,"base"), (89,"m"), (89,"n"),
      (90,"x"), (91,"x"), (96,"idx"), (96,"x"), (99,"idx") ];
    private static readonly IReadOnlyDictionary<(int, string), CabNumericRange> Ranges = new Dictionary<(int, string), CabNumericRange>
    {
        [(56,"start")]=new(0), [(56,"len")]=new(0), [(59,"start")]=new(0), [(59,"len")]=new(0),
        [(60,"start")]=new(0), [(60,"len")]=new(0), [(63,"start")]=new(0), [(63,"len")]=new(0),
        [(65,"start")]=new(0), [(65,"len")]=new(0), [(66,"Lx")]=new(1,200), [(66,"z")]=new(0),
        [(76,"bits1")]=new(1,32), [(76,"bitsx")]=new(1,32), [(77,"base")]=new(2,36),
        [(78,"m")]=new(0), [(78,"n")]=new(0), [(89,"m")]=new(1), [(89,"n")]=new(0),
        [(90,"x")]=new(0), [(91,"x")]=new(0), [(92,"x")]=new(0), [(94,"x")]=new(0),
        [(96,"idx")]=new(1), [(99,"idx")]=new(1), [(96,"x")]=new(0),
        [(97,"freq")]=new(0,MinimumExclusive:true), [(82,"l")]=new(0)
    };
    private static readonly IReadOnlyDictionary<(int, string), int> MaximumLengths = new Dictionary<(int, string), int>
    { [(53,"t")]=1, [(53,"d")]=1, [(53,"o")]=1, [(77,"fill")]=1, [(66,"x")]=16, [(96,"text")]=32, [(100,"query")]=128, [(101,"query")]=128 };
    private static readonly HashSet<(int, string)> LiteralParameters =
    [ (66,"D"), (66,"R"), (66,"J"), (96,"D"), (96,"R"), (96,"J") ];
    private static readonly IReadOnlyDictionary<(int, string), string> Patterns = new Dictionary<(int, string), string>
    { [(66,"Mx")]="^!?[0-8]+$", [(50,"x")]="^[0-9]+$", [(51,"x")]="^[0-9A-Z]+$", [(52,"x")]=@"^[0-9A-Z. $/+%\-]+$" };

    internal static CabParameterRule Create(CabCatalogEntry entry, CabCatalogParameter parameter)
    {
        var id = int.Parse(entry.Section.AsSpan(2), CultureInfo.InvariantCulture);
        var key = (id, parameter.Name);
        var rule = new CabParameterRule(entry, parameter, Type(id, parameter.Name))
        {
            NumericRange = Ranges.GetValueOrDefault(key), Pattern = Patterns.GetValueOrDefault(key),
            Literal = LiteralParameters.Contains(key) ? parameter.Name : null,
            EvenInteger = key is (63, "start") or (63, "len")
        };
        if (MaximumLengths.TryGetValue(key, out var maximum))
            rule = rule with { MinimumLength = maximum == 1 ? 1 : null, MaximumLength = maximum };
        if (key == (98, "charset")) rule = rule with { MinimumLength = 1 };
        return rule;
    }

    private static CabDataType Type(int id, string name)
    {
        if (id <= 48) return CabDataType.Number;
        if (id == 53 && name == "value") return CabDataType.Number;
        if (id == 97 && name == "x") return CabDataType.Text;
        if (IntegerFields.Contains(id)) return name == "locklevel" ? CabDataType.Choice : CabDataType.Integer;
        return OtherType(id, name);
    }

    private static CabDataType OtherType(int id, string name)
    {
        if (IntegerParameters.Contains((id, name))) return CabDataType.Integer;
        if (ChoiceFields.Contains(id)) return CabDataType.Choice;
        if (id == 82) return name == "m" ? CabDataType.Choice : CabDataType.Number;
        if (name is "name" or "fieldname") return CabDataType.Name;
        return CabDataType.Text;
    }
}
