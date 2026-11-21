using System.Text.RegularExpressions;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabCommandGrammar
{
    internal static void Bind(CabBindingBuilder builder, CabSyntaxNode? next)
    {
        var slice = builder.Slice;
        if (BindFamily(builder)) return;
        switch (slice.Node.Command)
        {
            case "S": LabelSize(builder); break;
            case "A": Amount(builder); break;
            case "C": Csv(builder, ["param", "disp1", "disp2"]); break;
            case "D": Csv(builder, ["x", "y"]); break;
            case "R": Pair(builder, "name", "value"); break;
            case "F": FontAlias(builder); break;
            case "X": Signal(builder); break;
            case "d": Download(builder, next); break;
            case "s": Date(builder); break;
            case "q": Query(builder); break;
            default: Simple(builder); break;
        }
    }

    private static bool BindFamily(CabBindingBuilder builder)
    {
        var node = builder.Slice.Node;
        if (node.Kind == CabNodeKind.EscapeCommand) Escape(builder);
        else if (node.Kind == CabNodeKind.BinaryBlock) Payload(builder, node, "data");
        else if (node.Kind == CabNodeKind.Block) Block(builder);
        else if (new[] { "T", "B", "W", "I", "G" }.Contains(node.Command, StringComparer.Ordinal)) CabFieldGrammar.Bind(builder);
        else if (node.Command is "E" or "M") CabStorageGrammar.Bind(builder);
        else if (node.Command is "H" or "O") CabOptionGrammar.Bind(builder);
        else if (node.Command == "e") Pair(builder, "type", "name");
        else return false;
        return true;
    }

    internal static void Csv(CabBindingBuilder builder, string[] names, CabSourceRange? range = null)
    {
        var fields = builder.Slice.Split(range ?? builder.Slice.Body, ',');
        for (var index = 0; index < names.Length; index++) builder.AddAt(names[index], fields, index);
        if (fields.Count > names.Length) builder.Issue("extra-parameter", "Zusätzlicher Parameter ist für diese Syntax nicht dokumentiert.", fields[names.Length]);
    }

    private static void Simple(CabBindingBuilder builder)
    {
        var parameters = builder.Definition.Parameters.Where(p => !p.Parameter.IsAnswer).ToArray();
        if (parameters.Length == 1)
        {
            if (!parameters[0].Required && builder.Slice.Protocol(builder.Slice.Body).Trim().Length == 0) return;
            builder.Add(parameters[0].Parameter.Name, builder.Slice.Body);
        }
        else if (builder.Slice.Protocol(builder.Slice.Body).Trim().Length > 0)
            builder.Issue("unexpected-argument", "Dieser Befehl hat keine Eingabeparameter.", builder.Slice.Body);
    }

    private static void Escape(CabBindingBuilder builder)
    {
        var inputs = builder.Definition.Parameters.Where(p => !p.Parameter.IsAnswer).ToArray();
        var supplied = builder.Slice.Node.Children.Where(n => n.Kind == CabNodeKind.Parameter).ToArray();
        for (var index = 0; index < Math.Min(inputs.Length, supplied.Length); index++)
            builder.Add(inputs[index].Parameter.Name, supplied[index].Range);
    }

    internal static void Pair(CabBindingBuilder builder, string headerName, string dataName)
    {
        var split = CabCommandResolver.HeaderData(builder.Slice);
        builder.Add(headerName, split.Header);
        if (split.Data is { } data) builder.Add(dataName, data);
    }

    private static void FontAlias(CabBindingBuilder builder)
    {
        if (CabCommandResolver.HeaderData(builder.Slice).Data is not null) Pair(builder, "number", "name");
        else Csv(builder, ["number", "name"]);
    }

    private static void Date(CabBindingBuilder builder)
    {
        var names = new[] { "YY", "MM", "DD", "hh", "mm", "ss" };
        var length = builder.Slice.Protocol(builder.Slice.Body).Length;
        for (var part = 0; part < names.Length && part * 2 < length; part++)
            builder.Add(names[part], builder.Slice.Subrange(builder.Slice.Body, part * 2, Math.Min(2, length - part * 2)));
        if (length is not (10 or 12)) builder.Issue("date-length", "Datumsbefehl benötigt zehn oder zwölf Ziffern.", builder.Slice.Body);
    }

    private static void Query(CabBindingBuilder builder)
    {
        var split = CabCommandResolver.HeaderData(builder.Slice);
        builder.Add("type", split.Header);
        if (split.Data is { } name) builder.Add("name", name);
        else if ("bdeils".Contains(builder.Slice.Protocol(split.Header), StringComparison.Ordinal))
            builder.Issue("query-name", "Dieser Abfragetyp benötigt einen Dateinamen nach dem Semikolon.", split.Header);
    }

    private static void LabelSize(CabBindingBuilder builder)
    {
        var slice = builder.Slice;
        var pieces = slice.Split(slice.Body, ';');
        var coordinates = pieces[0];
        if (pieces.Count > 1 && !slice.Protocol(pieces[0]).Contains(','))
        { builder.Add("ptype", pieces[0]); coordinates = pieces[1]; }
        Csv(builder, ["xo", "yo", "ho", "dy", "wd", "dx", "col"], coordinates);
        if (coordinates.End < slice.Body.End) builder.Add("name", slice.TailAfter(coordinates, 1));
    }

    private static void Amount(CabBindingBuilder builder)
    {
        var slice = builder.Slice;
        builder.Add("param", slice.Body);
        var body = slice.Protocol(slice.Body);
        var count = Regex.Match(body, @"^[0-9]+", RegexOptions.CultureInvariant);
        var preview = Regex.Match(body, @"\[PREVIEW(?::(?<index>[0-9]+|ALL))?\]", RegexOptions.CultureInvariant);
        if (count.Success)
        {
            builder.Add("num", slice.Subrange(slice.Body, count.Index, count.Length));
            if (preview.Success) builder.Add("preview_count", slice.Subrange(slice.Body, count.Index, count.Length));
        }
        if (preview.Groups["index"].Success)
        { var index = preview.Groups["index"]; builder.Add("preview_index", slice.Subrange(slice.Body, index.Index, index.Length)); }
        var variable = Regex.Match(body, @"\[(?<name>\p{L}[\p{L}\p{Nd}]*)\]", RegexOptions.CultureInvariant);
        if (variable.Success && !new[] { "NO", "NOPRINT", "R", "REPEAT", "SAVE", "PREVIEW" }.Contains(variable.Groups["name"].Value, StringComparer.Ordinal))
        { var name = variable.Groups["name"]; builder.Add("VAR", slice.Subrange(slice.Body, name.Index, name.Length)); }
    }

    private static void Signal(CabBindingBuilder builder)
    {
        var split = CabCommandResolver.HeaderData(builder.Slice);
        builder.Add("y", split.Header);
        if (split.Data is not { } data) return;
        builder.Add("ao", data);
        var length = builder.Slice.Protocol(data).Length;
        if (length > 0) builder.Add("a", builder.Slice.Subrange(data, 0, 1));
        if (length > 1) builder.Add("o", builder.Slice.Subrange(data, 1, 1));
    }

    private static void Download(CabBindingBuilder builder, CabSyntaxNode? next)
    {
        var slice = builder.Slice;
        var split = CabCommandResolver.HeaderData(slice);
        builder.Add("type", split.Header);
        if (split.Data is { } data) DownloadName(builder, data);
        if (next?.Kind == CabNodeKind.BinaryBlock) Payload(builder, next, "data");
    }

    private static void DownloadName(CabBindingBuilder builder, CabSourceRange data)
    {
        var slice = builder.Slice;
        var text = slice.Protocol(data);
        var flag = text.IndexOf('[', StringComparison.Ordinal);
        var nameLength = flag < 0 ? text.Length : text[..flag].TrimEnd().Length;
        CabStorageGrammar.FileName(builder, slice.Subrange(data, 0, nameLength));
        var save = Regex.Match(text, @"\[(?<save>SAVE)\]", RegexOptions.CultureInvariant);
        if (save.Success) builder.Add("SAVE", slice.Subrange(data, save.Groups["save"].Index, 4));
        var brightness = Regex.Match(text, @"\[B:(?<value>[^\]]*)\]", RegexOptions.CultureInvariant);
        if (brightness.Success)
        { var value = brightness.Groups["value"]; builder.Add("B", slice.Subrange(data, value.Index, value.Length)); }
    }

    private static void Payload(CabBindingBuilder builder, CabSyntaxNode node, string name)
    {
        var bytes = builder.Slice.Document.Bytes(node.Range);
        var prefix = node.Command == "<BASE64>" ? 8 : 2;
        var close = node.Command switch { "ESC." => "\u001b.", "ESC:" => "\u001bend-of-data", _ => "</BASE64>" };
        var suffix = bytes.AsSpan().EndsWith(System.Text.Encoding.ASCII.GetBytes(close)) ? close.Length : 0;
        builder.AddBinary(name, new(node.Range.Offset + prefix, Math.Max(0, node.Range.Length - prefix - suffix)), node.CodePage);
    }

    private static void Block(CabBindingBuilder builder)
    {
        var slice = builder.Slice;
        if (slice.Node.Command == "<ENCRYPTED LABEL:")
        {
            var number = Regex.Match(slice.Framing, @"^<ENCRYPTED LABEL:\s*(?<number>[^>]*?)\s*>", RegexOptions.CultureInvariant);
            if (number.Success)
            { var group = number.Groups["number"]; builder.Add("board_number", slice.Range(group.Index, group.Length)); }
            else builder.Issue("encrypted-header", "Verschlüsselter Etikettkopf ist unvollständig.", slice.Node.Range);
            return;
        }
        var opening = slice.Node.Command;
        var closing = opening == "<ABC>" ? "</ABC>" : "</ENCRYPTED JOB>";
        var end = slice.Framing.EndsWith(closing, StringComparison.Ordinal) ? slice.Framing.Length - closing.Length : slice.Framing.Length;
        builder.Add(opening == "<ABC>" ? "abc_code" : "job", slice.Range(opening.Length, Math.Max(0, end - opening.Length)));
    }
}
