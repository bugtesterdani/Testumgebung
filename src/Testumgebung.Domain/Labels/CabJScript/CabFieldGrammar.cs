using System.Globalization;
using System.Text.RegularExpressions;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabFieldGrammar
{
    internal static void Bind(CabBindingBuilder builder)
    {
        var slice = builder.Slice;
        if (slice.Name is { } name) builder.Add(slice.Node.Command == "I" ? "fieldname" : "name", name);
        var split = CabCommandResolver.HeaderData(slice);
        var fields = slice.Split(split.Header, ',');
        builder.AddAt("x", fields, 0); builder.AddAt("y", fields, 1); builder.AddAt("r", fields, 2);
        switch (slice.Node.Command)
        {
            case "T": Text(builder, fields); break;
            case "W": RichText(builder, fields); break;
            case "B": Barcode(builder, fields); break;
            case "I": Image(builder, fields); break;
            case "G": Graphic(builder, split.Data); break;
        }
        if (split.Data is { } data)
            builder.Add(slice.Node.Command == "I" ? "name" : "text", data);
        if (slice.Node.Command == "B" && split.Data is { } content) BarcodeControls(builder, content);
    }

    private static void Text(CabBindingBuilder builder, IReadOnlyList<CabSourceRange> fields)
    {
        builder.AddAt("font", fields, 3);
        var bitmap = fields.Count > 4 && builder.Slice.Protocol(fields[4]).StartsWith('x');
        if (fields.Count > 3 && decimal.TryParse(builder.Slice.Protocol(fields[3]), CultureInfo.InvariantCulture, out var font) && font < 0) bitmap = true;
        if (bitmap)
        {
            ScaledSize(builder, fields, 4, "x", "size.x");
            ScaledSize(builder, fields, 5, "y", "size.y");
            Effects(builder, fields, 6);
        }
        else { Size(builder, fields, 4); Effects(builder, fields, 5); }
    }

    private static void ScaledSize(CabBindingBuilder builder, IReadOnlyList<CabSourceRange> fields, int index, string prefix, string name)
    {
        if (index >= fields.Count)
        { builder.Issue("bitmap-size", "Bitmapschrift benötigt xn und yn als Vergrößerung.", builder.Slice.Body); return; }
        var text = builder.Slice.Protocol(fields[index]);
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
            builder.Issue("bitmap-size", "Vergrößerung benötigt das Präfix " + prefix + ".", fields[index]);
        else builder.Add(name, builder.Slice.Subrange(fields[index], 1, text.Length - 1));
    }

    private static void Size(CabBindingBuilder builder, IReadOnlyList<CabSourceRange> fields, int index)
    {
        if (index >= fields.Count) { builder.Require("size"); return; }
        builder.AddAt("size", fields, index);
        var text = builder.Slice.Protocol(fields[index]);
        if (text.StartsWith("pt", StringComparison.Ordinal)) builder.Add("size.pt", builder.Slice.Subrange(fields[index], 2, text.Length - 2));
    }

    private static void Effects(CabBindingBuilder builder, IReadOnlyList<CabSourceRange> fields, int start)
    {
        if (start >= fields.Count) return;
        var range = new CabSourceRange(fields[start].Offset, fields[^1].End - fields[start].Offset);
        builder.Add("effects", range);
        foreach (var field in fields.Skip(start))
        {
            var token = builder.Slice.Protocol(field);
            var prefix = new[] { "fr", "fl", "fu", "fd", "q", "h", "m" }.FirstOrDefault(p => token.StartsWith(p, StringComparison.Ordinal));
            if (prefix is not null) builder.Add("effects." + prefix, builder.Slice.Subrange(field, prefix.Length, token.Length - prefix.Length));
        }
    }

    private static void RichText(CabBindingBuilder builder, IReadOnlyList<CabSourceRange> fields)
    {
        builder.AddAt("width", fields, 3); builder.AddAt("height", fields, 4); builder.AddAt("font", fields, 5);
        Size(builder, fields, 6); builder.AddAt("line-spacing", fields, 7);
        if (fields.Count > 8) builder.Issue("extra-parameter", "Rich-Text-Kopf hat zusätzliche Parameter.", fields[8]);
    }

    private static void Barcode(CabBindingBuilder builder, IReadOnlyList<CabSourceRange> fields)
    {
        if (fields.Count > 3) BarcodeType(builder, fields[3]);
        var dimensions = new List<string>();
        if (builder.Has("dotsize")) dimensions.Add("dotsize");
        else if (StandardSize(builder, fields))
            StandardDimensions(builder, fields, dimensions);
        else if (builder.Has("height"))
        {
            builder.AllowMissing("size");
            dimensions.Add("height");
            if (builder.Has("ne")) dimensions.Add("ne");
            if (builder.Has("ratio")) dimensions.Add("ratio");
        }
        else if (builder.Has("size")) dimensions.Add("size");
        for (var at = 0; at < dimensions.Count; at++) builder.AddAt(dimensions[at], fields, 4 + at);
        var extra = 4 + dimensions.Count;
        if (fields.Count > extra)
            builder.Add("fx", new(fields[extra].Offset, fields[^1].End - fields[extra].Offset));
    }

    private static bool StandardSize(CabBindingBuilder builder, IReadOnlyList<CabSourceRange> fields)
        => builder.Has("size") && fields.Count > 4 && builder.Slice.Protocol(fields[4]).Trim().StartsWith("SC", StringComparison.Ordinal);

    private static bool NumericField(CabBindingBuilder builder, IReadOnlyList<CabSourceRange> fields, int index)
        => index < fields.Count && decimal.TryParse(builder.Slice.Protocol(fields[index]), CultureInfo.InvariantCulture, out _);

    private static void StandardDimensions(CabBindingBuilder builder, IReadOnlyList<CabSourceRange> fields, List<string> dimensions)
    {
        dimensions.Add("size");
        if (!builder.Has("height") || !NumericField(builder, fields, 5)) return;
        dimensions.Add("height");
        if (builder.Has("ne") && NumericField(builder, fields, 6)) dimensions.Add("ne");
    }

    private static void BarcodeType(CabBindingBuilder builder, CabSourceRange range)
    {
        var slice = builder.Slice;
        var raw = slice.Protocol(range);
        var text = raw.Trim();
        var leading = raw.Length - raw.TrimStart().Length;
        var type = builder.Definition.Parameter("type");
        var candidates = type.Parameter.Values.Select(v => v.Value).Concat(type.AdditionalValues).OrderByDescending(v => v.Length);
        var selected = candidates.FirstOrDefault(v => text == v || text.StartsWith(v + "+", StringComparison.Ordinal));
        var length = selected?.Length ?? (text.IndexOf('+') is var plus && plus >= 0 ? plus : text.Length);
        builder.Add("type", slice.Subrange(range, leading, length));
        if (text.Length > length)
            CabModifierGrammar.Barcode(builder, slice.Subrange(range, leading + length, text.Length - length));
    }

    private static void Image(CabBindingBuilder builder, IReadOnlyList<CabSourceRange> fields)
    {
        var numeric = 0;
        foreach (var field in fields.Skip(3))
        {
            var text = builder.Slice.Protocol(field);
            if (text.StartsWith("GOODBAD", StringComparison.Ordinal) || text.StartsWith("EXTERN", StringComparison.Ordinal))
                CabModifierGrammar.Verifier(builder, field, false);
            else if (text == "a") builder.Add("a", field);
            else if (numeric++ < 2) builder.Add(numeric == 1 ? "mx" : "my", field);
            else builder.Issue("image-option", "Unbekannte Bildoption bleibt erhalten.", field);
        }
    }

    private static void Graphic(CabBindingBuilder builder, CabSourceRange? data)
    {
        if (data is not { } range) return;
        var slice = builder.Slice;
        var text = slice.Protocol(range);
        var colon = text.IndexOf(':');
        if (colon < 0) { builder.Issue("graphic-shape", "Grafik benötigt Elementart und Doppelpunkt.", range); return; }
        builder.Add("ge", slice.Subrange(range, 0, colon));
        var values = slice.Subrange(range, colon + 1, text.Length - colon - 1);
        builder.Add("settings", values);
        var fields = slice.Split(values, ',');
        var option = fields.ToList().FindIndex(f => slice.Protocol(f).StartsWith("[", StringComparison.Ordinal));
        var count = option < 0 ? fields.Count : option;
        var names = builder.Definition.Catalog.Id switch
        {
            "JS-4.7.1" => new[] { "radius1", "radius2", "width" },
            "JS-4.7.2" => ["length", "width", "start", "end"],
            "JS-4.7.3" => ["width", "height", "ht", "vt"],
            _ => []
        };
        for (var i = 0; i < Math.Min(count, names.Length); i++) builder.Add(names[i], fields[i]);
        if (count > names.Length) builder.Issue("graphic-extra", "Grafikparameter ist im Handbuch nicht eindeutig beschrieben.", fields[names.Length]);
        if (option >= 0) CabModifierGrammar.Graphic(builder, new(fields[option].Offset, range.End - fields[option].Offset));
    }

    private static void BarcodeControls(CabBindingBuilder builder, CabSourceRange content)
    {
        var text = builder.Slice.Protocol(content);
        foreach (Match match in Regex.Matches(text, @"\[U:(?<code>CODE[A-Z]|FNC[0-9]+)\]", RegexOptions.CultureInvariant))
        { var code = match.Groups["code"]; builder.Add("subcode", builder.Slice.Subrange(content, code.Index, code.Length)); }
    }
}
