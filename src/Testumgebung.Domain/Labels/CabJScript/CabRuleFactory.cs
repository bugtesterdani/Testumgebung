using System.Text.RegularExpressions;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabRuleFactory
{
    private static readonly HashSet<string> Lengths = ["x", "y", "xo", "yo", "ho", "dy", "wd", "dx", "height", "width", "ne", "dotsize", "radius1", "radius2", "length", "ht", "vt", "disp", "disp1", "disp2", "P.x", "size", "effects.h", "effects.m", "effects.fr", "effects.fl", "effects.fu", "effects.fd", "D.line", "D.gap"];
    private static readonly HashSet<string> Integers = ["CC", "EL", "MODEL", "VERSION", "MOD", "MODE", "ROWS", "COLS", "WS", "num", "preview_count", "preview_index", "col", "font", "size.x", "size.y", "number", "port", "retries", "unpower", "tidlength"];
    private static readonly HashSet<string> FreeText = ["text", "value", "comment", "params", "settings", "special_functions", "fx", "options", "subcode", "composite", "html.attributes", "html.style", "host", "ip", "username", "password", "ext", "Job-ID", "jobname", "version", "version_info", "datetime", "memory", "peripheral_type", "peripheral_name", "additional_settings", "abc_code", "F.value"];
    private static readonly Dictionary<string, (CabDataType Type, CabParameterUnit Unit)> NumericTypes = new()
    {
        ["ratio"] = (CabDataType.Number, CabParameterUnit.None),
        ["h"] = (CabDataType.Integer, CabParameterUnit.None), ["h2"] = (CabDataType.Integer, CabParameterUnit.None),
        ["B"] = (CabDataType.Integer, CabParameterUnit.None),
        ["speed"] = (CabDataType.Number, CabParameterUnit.CurrentSpeed), ["Bb"] = (CabDataType.Number, CabParameterUnit.CurrentSpeed),
        ["S.start"] = (CabDataType.Number, CabParameterUnit.Percent), ["S.end"] = (CabDataType.Number, CabParameterUnit.Percent),
        ["effects.q"] = (CabDataType.Number, CabParameterUnit.Percent), ["S.direction"] = (CabDataType.Number, CabParameterUnit.Degrees),
        ["size.pt"] = (CabDataType.Number, CabParameterUnit.Points),
        ["mx"] = (CabDataType.Integer, CabParameterUnit.None), ["my"] = (CabDataType.Integer, CabParameterUnit.None),
        ["line-spacing"] = (CabDataType.Number, CabParameterUnit.Millimetres)
    };

    internal static CabParameterRule Create(CabCatalogEntry entry, CabCatalogParameter parameter)
    {
        var rule = Classify(entry, parameter) with
        { Required = entry.Id == "JS-3.13" ? parameter.Name != "ss" : Required(entry, parameter) };
        return CabRuleBounds.Apply(rule);
    }

    private static CabParameterRule Rule(CabCatalogEntry e, CabCatalogParameter p, CabDataType type, CabParameterUnit unit = CabParameterUnit.None)
        => new(e, p, type, unit);

    private static CabParameterRule Classify(CabCatalogEntry e, CabCatalogParameter p)
    {
        if (p.IsAnswer) return Rule(e, p, p.Values.Count > 0 ? CabDataType.Choice : CabDataType.Text);
        if (IsFieldName(e, p)) return Rule(e, p, CabDataType.Name) with { MinimumLength = 1 };
        if (p.Name == "data" || p.Name == "job") return Rule(e, p, CabDataType.Binary);
        if (p.Name == "board_number") return Rule(e, p, CabDataType.Text) with { Pattern = "^[0-9]{12}$" };
        if (p.Name is "path" or "filename.ext") return Rule(e, p, CabDataType.Path);
        var special = Special(e, p);
        if (special is not null) return special;
        return Common(e, p);
    }

    private static CabParameterRule Common(CabCatalogEntry e, CabCatalogParameter p)
    {
        if (p.Name is "cp" or "codepage") return Rule(e, p, p.Values.Count > 1 ? CabDataType.Choice : CabDataType.Text);
        if (p.Name == "name") return Rule(e, p, p.Values.Count > 0 ? CabDataType.Choice : CabDataType.Path) with { MinimumLength = 1 };
        if (p.Name is "host" or "ip") return Rule(e, p, CabDataType.Text) with { MinimumLength = 1 };
        if (p.Name == "port") return Rule(e, p, CabDataType.Integer) with { NumericRange = new(1, 65535) };
        if (p.Values.Count > 0 && !IsOpenValueList(e, p)) return Rule(e, p, CabDataType.Choice);
        if (Lengths.Contains(p.Name) || IsVerifierPosition(p.Name)) return Rule(e, p, CabDataType.Number, CabParameterUnit.CurrentLength)
            with { AllowKnownValues = true, NumericPrefix = p.Name == "size" && e.Id is "JS-4.16" or "JS-4.17" ? "pt" : null };
        if (Integers.Contains(p.Name)) return Rule(e, p, CabDataType.Integer, CabParameterUnit.Count) with { AllowKnownValues = true };
        if (FreeText.Contains(p.Name)) return Rule(e, p, CabDataType.Text);
        throw new InvalidOperationException("Datentyp ist nicht explizit zugeordnet: " + e.Id + "/" + p.Name);
    }

    private static bool IsFieldName(CabCatalogEntry e, CabCatalogParameter p)
        => p.Name == "fieldname" || p.Name == "VAR" || p.Name == "name" &&
            (e.Kind is "barcode" or "grafik" || e.Id is "JS-4.2" or "JS-4.7" or "JS-4.14" or "JS-4.16" or "JS-4.17");

    private static bool IsOpenValueList(CabCatalogEntry e, CabCatalogParameter p)
        => p.Name is "font" or "size" or "options" or "fx" or "special_functions" or "F.value"
            || e.Id == "JS-4.17" && p.Name is "text" or "html.attributes" or "html.style";

    private static bool IsVerifierPosition(string name)
        => name.EndsWith(".a", StringComparison.Ordinal) || name.EndsWith(".b", StringComparison.Ordinal);

    private static CabParameterRule? Special(CabCatalogEntry e, CabCatalogParameter p)
    {
        if (p.Name == "effects" || p.Name == "fx") return Rule(e, p, CabDataType.Choice) with { ChoiceMode = CabChoiceMode.CommaList };
        if (p.Name == "options") return Rule(e, p, CabDataType.Choice) with
        { ChoiceMode = e.Kind == "grafik" || e.Id == "JS-4.7" ? CabChoiceMode.GraphicList : CabChoiceMode.PlusList };
        if (p.Name == "F.value") return Rule(e, p, CabDataType.Path) with
        { Pattern = @"^(?:(?:0|6|12|25|38|50|100)%|[A-Za-z0-9_.-]+)$" };
        return SpecialInput(e, p);
    }

    private static CabParameterRule? SpecialInput(CabCatalogEntry e, CabCatalogParameter p)
    {
        if (e.Id == "JS-4.1" && p.Name == "param") return Rule(e, p, CabDataType.Text) with
        { Pattern = @"^(?:[0-9]+)?\s*(?:\[(?:NO|NOPRINT|\?|R|REPEAT|\?,(?:R|REPEAT)|\$DBF|PREVIEW(?::(?:[0-9]+|ALL))?|\p{L}[\p{L}\p{Nd}]*)\])?(?:\[SAVE\])?$" };
        if (e.Id == "JS-4.3" && p.Name == "param") return Rule(e, p, CabDataType.Integer) with { AllowKnownValues = true };
        if (e.Id == "JS-4.2" && p.Name == "type") return BarcodeType(e, p);
        if (p.Name == "preview_index") return Rule(e, p, CabDataType.Integer) with { AllowKnownValues = true };
        if (e.Id == "JS-4.8" && p.Name == "P") return Rule(e, p, CabDataType.Text) with { Pattern = @"^P(?:[+-]?[0-9]+(?:\.[0-9]+)?)?$" };
        if (e.Id == "JS-4.9" && p.Name == "a") return Rule(e, p, CabDataType.Text) with { Literal = "a" };
        if (e.Id == "JS-4.15" && p.Name == "name") return Rule(e, p, CabDataType.Text);
        if (p.Name is "GOODBAD" or "EXTERN") return Rule(e, p, CabDataType.Text) with
        { Pattern = "^" + p.Name + @"(?:[+-]?[0-9]+(?:\.[0-9]+)?(?::[+-]?[0-9]+(?:\.[0-9]+)?(?::[0-9]+)?)?)?$" };
        return SpecificFamily(e, p);
    }

    private static CabParameterRule? SpecificFamily(CabCatalogEntry e, CabCatalogParameter p)
    {
        if (e.Id == "JS-4.12") return Option(e, p);
        if (e.Id == "JS-4.5.4") return Rfid(e, p);
        if (e.Id == "JS-3.13") return Rule(e, p, CabDataType.Integer) with { Pattern = "^[0-9]{2}$" };
        if (e.Id == "JS-3.16") return Rule(e, p, CabDataType.Text) with { Pattern = "^[0-9A-Fa-f]$" };
        if (e.Id == "JS-4.18") return Signal(e, p);
        if (e.Id == "JS-4.5.3" && p.Name == "x") return Rule(e, p, CabDataType.Integer, CabParameterUnit.Count);
        if (p.Name == "r" && e.Id != "JS-4.8") return Rotation(e, p);
        if (p.Name.EndsWith(".c", StringComparison.Ordinal)) return Rule(e, p, CabDataType.Integer, CabParameterUnit.Milliseconds);
        if (p.Name == "SAVE") return Rule(e, p, CabDataType.Text) with { Literal = "SAVE" };
        if (NumericTypes.TryGetValue(p.Name, out var numeric)) return Rule(e, p, numeric.Type, numeric.Unit) with
        { NumericPrefix = p.Name switch { "Bb" => "B", "size.pt" => "pt", _ => null } };
        return null;
    }

    private static CabParameterRule Rotation(CabCatalogEntry e, CabCatalogParameter p)
        => p.Values.Count > 0 ? Rule(e, p, CabDataType.Choice, CabParameterUnit.Degrees) with { NumericChoices = true }
            : Rule(e, p, CabDataType.Number, CabParameterUnit.Degrees) with { NumericRange = new(0, 359) };

    private static CabParameterRule BarcodeType(CabCatalogEntry e, CabCatalogParameter p)
        => Rule(e, p, CabDataType.Choice) with { AdditionalValues = CabCatalog.Default.Entries.Where(c => c.Kind == "barcode")
            .SelectMany(c => c.Parameters.Where(q => q.Name == "type")).SelectMany(q => q.Values).Select(v => v.Value).Distinct(StringComparer.Ordinal).ToArray() };

    private static CabParameterRule Option(CabCatalogEntry e, CabCatalogParameter p)
    {
        if (p.Name == "Ax=y") return Rule(e, p, CabDataType.Text) with { Pattern = @"^A[0-4]=[0-9]+$" };
        if (p.Name == "Wy") return Rule(e, p, CabDataType.Text) with { Pattern = @"^(?:Wn|Wi|Wi[+-]?[0-9]+(?:\.[0-9]+)?)$" };
        if (p.Name.EndsWith('x')) return Rule(e, p, p.Name == "Sx" ? CabDataType.Integer : CabDataType.Number,
            p.Name == "Lx" ? CabParameterUnit.Percent : CabParameterUnit.CurrentLength) with
            { NumericPrefix = p.Name[..1], AllowBarePrefix = p.Name == "Tx" };
        return Rule(e, p, CabDataType.Text) with { Literal = p.Name };
    }

    private static CabParameterRule Rfid(CabCatalogEntry e, CabCatalogParameter p)
        => p.Name switch
        {
            "pos" => Rule(e, p, CabDataType.Number, CabParameterUnit.Millimetres),
            "power" => Rule(e, p, CabDataType.Number) with { AllowKnownValues = true,
                GenerationDataTypes = new Dictionary<CabPrinterGeneration, CabDataType> { [CabPrinterGeneration.X2] = CabDataType.Choice } },
            "wpower" => Rule(e, p, CabDataType.Integer),
            "length" => Rule(e, p, CabDataType.Integer, CabParameterUnit.Bytes) with { EvenInteger = true },
            "V" or "P" or "N" => Rule(e, p, CabDataType.Text) with { Literal = p.Name },
            "accesspwd" => Rule(e, p, CabDataType.Binary) with { Pattern = @"^(?:0x[0-9A-Fa-f]{8}|[\x00-\xFF]{4})$" },
            _ => RfidCommon(e, p)
        };

    private static CabParameterRule RfidCommon(CabCatalogEntry e, CabCatalogParameter p)
    {
        if (p.Values.Count > 0 && p.Name != "cp") return Rule(e, p, CabDataType.Choice);
        return p.Name switch
        {
            "retries" or "unpower" or "tidlength" => Rule(e, p, CabDataType.Integer),
            "cp" => Rule(e, p, CabDataType.Text),
            _ => throw new InvalidOperationException("Unbekannter RFID-Parameter: " + p.Name)
        };
    }

    private static CabParameterRule Signal(CabCatalogEntry e, CabCatalogParameter p)
        => p.Name == "y" ? Rule(e, p, CabDataType.Number, CabParameterUnit.CurrentLength)
            : Rule(e, p, CabDataType.Text) with { Pattern = p.Name == "ao" ? "^[0-9A-Fa-f]{2}$" : "^[0-9A-Fa-f]$" };

    private static bool Required(CabCatalogEntry entry, CabCatalogParameter parameter)
    {
        if (parameter.IsAnswer) return false;
        var pattern = @"(?<![A-Za-z0-9_.])" + Regex.Escape(parameter.Name) + @"(?![A-Za-z0-9_.])";
        return entry.Syntax.Count > 0 && entry.Syntax.All(s => Regex.Matches(s, pattern, RegexOptions.CultureInvariant)
            .Any(m => OutsideOptional(s, m.Index)));
    }

    private static bool OutsideOptional(string syntax, int position)
    {
        var depth = 0;
        for (var i = 0; i < position; i++)
        {
            if (syntax[i] is '[' or '{') depth++;
            else if (syntax[i] is ']' or '}' && depth > 0) depth--;
        }
        return depth == 0;
    }
}
