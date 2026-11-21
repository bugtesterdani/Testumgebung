namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabOptionGrammar
{
    internal static void Bind(CabBindingBuilder builder)
    {
        var fields = builder.Slice.Split(builder.Slice.Body, ',').Select(builder.Slice.Trim).ToArray();
        if (builder.Slice.Node.Command == "O")
        {
            foreach (var field in fields.Where(f => builder.Slice.Protocol(f).Length > 0)) PrintOption(builder, field);
            return;
        }
        builder.AddAt("speed", fields, 0);
        var heat = false;
        foreach (var field in fields.Skip(1))
        {
            var text = builder.Slice.Protocol(field);
            if (text.Length == 0) continue;
            if (char.IsDigit(text[0]) || text[0] is '-' or '+' or '.')
            {
                if (heat) builder.Issue("heat-duplicate", "Heizkorrektur mehrfach angegeben.", field);
                Pair(builder, field, "h", "h2"); heat = true;
            }
            else HeadOption(builder, field, text);
        }
    }

    private static void HeadOption(CabBindingBuilder builder, CabSourceRange field, string text)
    {
        switch (text[0])
        {
            case 'P':
                builder.Add("P", field);
                if (text.Length > 1) builder.Add("P.x", builder.Slice.Subrange(field, 1, text.Length - 1));
                break;
            case 'T': case 'D': Pair(builder, field, "t", "t2"); break;
            case 'R': builder.Add("r", field); break;
            case 'S': builder.Add("s", field); break;
            case 'B': builder.Add("Bb", field); break;
            default: builder.Issue("head-option", "Unbekannte Druckkopfoption bleibt erhalten.", field); break;
        }
    }

    private static void Pair(CabBindingBuilder builder, CabSourceRange range, string first, string second)
    {
        var fields = builder.Slice.Split(range, ':').Select(builder.Slice.Trim).ToArray();
        builder.AddAt(first, fields, 0); builder.AddAt(second, fields, 1);
        if (fields.Length > 2) builder.Issue("head-pair", "Kopfoption hat zusätzliche Werte.", fields[2]);
    }

    private static void PrintOption(CabBindingBuilder builder, CabSourceRange field)
    {
        var text = builder.Slice.Protocol(field);
        var name = text[0] switch
        {
            'A' => "Ax=y", 'C' => "Cx", 'H' => "Hx", 'L' => "Lx", 'T' => "Tx", 'W' => "Wy",
            'S' when text.Length > 1 => "Sx", _ => text
        };
        if (builder.Has(name)) builder.Add(name, field);
        else builder.Issue("print-option", "Unbekannte Druckoption bleibt erhalten.", field);
    }
}
