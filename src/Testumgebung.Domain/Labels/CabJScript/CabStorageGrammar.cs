using System.Text.RegularExpressions;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabStorageGrammar
{
    private static readonly IReadOnlyDictionary<string, string> RfidFields = new Dictionary<string, string>
    { ["T"] = "tagtype", ["R"] = "retries", ["C"] = "cp", ["P"] = "pos", ["E"] = "power", ["W"] = "wpower", ["U"] = "unpower", ["L"] = "length", ["X"] = "tidlength", ["A"] = "antenna", ["S"] = "accesspwd" };

    internal static void Bind(CabBindingBuilder builder)
    {
        if (builder.Slice.Node.Command == "M") { Memory(builder); return; }
        var split = CabCommandResolver.HeaderData(builder.Slice);
        if (builder.Definition.Catalog.Id == "JS-4.5")
        {
            builder.Add("type", split.Header);
            if (split.Data is { } all) builder.Add("params", all);
            return;
        }
        if (split.Data is not { } data) return;
        switch (builder.Definition.Catalog.Id)
        {
            case "JS-4.5.3": Opcua(builder, split.Header, data); break;
            case "JS-4.5.4": Rfid(builder, data); break;
            case "JS-4.5.5": HostPort(builder, data, "ip"); break;
            default: FileExtension(builder, data); break;
        }
    }

    private static void FileExtension(CabBindingBuilder builder, CabSourceRange data)
    {
        var fields = builder.Slice.Split(data, ',');
        FileName(builder, fields[0]);
        foreach (var field in fields.Skip(1))
        {
            var text = builder.Slice.Protocol(field);
            if (text.StartsWith("C:", StringComparison.Ordinal)) builder.Add("cp", builder.Slice.Subrange(field, 2, text.Length - 2));
            else builder.Issue("file-option", "Unbekannte Dateioption bleibt erhalten.", field);
        }
    }

    private static void Memory(CabBindingBuilder builder)
    {
        var slice = builder.Slice;
        var split = CabCommandResolver.HeaderData(slice);
        var header = slice.Protocol(split.Header);
        if (header.Length == 0) return;
        var after = 1;
        while (after < header.Length && header[after] is ' ' or '\t') after++;
        if (builder.Definition.Catalog.Id == "JS-4.11")
        {
            builder.Add("type", slice.Subrange(split.Header, 0, 1));
            if (header.Length > after) builder.Add("params", slice.Subrange(split.Header, after, header.Length - after));
        }
        else if (builder.Has("type") && header.Length > after)
            builder.Add("type", slice.Subrange(split.Header, after, header.Length - after));
        if (split.Data is { } file) FileName(builder, file);
        else if (builder.Definition.Catalog.Id == "JS-4.11.1" && header.Length > after)
            builder.Add("path", slice.Subrange(split.Header, after, header.Length - after));
    }

    internal static void FileName(CabBindingBuilder builder, CabSourceRange range)
    {
        var slice = builder.Slice;
        var text = slice.Protocol(range);
        var start = 0;
        if (text.StartsWith('/'))
        {
            var slash = text.IndexOf('/', 1);
            if (slash >= 0) { builder.Add("path", slice.Subrange(range, 1, slash - 1)); start = slash + 1; }
        }
        var length = text.Length - start;
        if (builder.Has("ext"))
        {
            var dot = text.LastIndexOf('.');
            if (dot >= start)
            { builder.Add("ext", slice.Subrange(range, dot + 1, text.Length - dot - 1)); length = dot - start; }
        }
        builder.Add("name", slice.Subrange(range, start, length));
    }

    private static void Rfid(CabBindingBuilder builder, CabSourceRange range)
    {
        var slice = builder.Slice;
        foreach (var field in slice.Split(range, ','))
        {
            var text = slice.Protocol(field);
            var colon = text.IndexOf(':');
            if (colon >= 0 && RfidFields.TryGetValue(text[..colon], out var name))
                builder.Add(name, slice.Subrange(field, colon + 1, text.Length - colon - 1));
            else if (text is "V" or "P" or "N") builder.Add(text, field);
            else builder.Issue("rfid-option", "Unbekannte RFID-Option bleibt erhalten.", field);
        }
    }

    private static void Opcua(CabBindingBuilder builder, CabSourceRange header, CabSourceRange data)
    {
        var slice = builder.Slice;
        var identifier = Regex.Match(slice.Protocol(header), @"^OPCUA-(?<id>[^;]+)$", RegexOptions.CultureInvariant);
        if (identifier.Success)
        { var id = identifier.Groups["id"]; builder.Add("x", slice.Subrange(header, id.Index, id.Length)); }
        var uri = slice.Protocol(data);
        const string scheme = "opc.tcp://";
        if (!uri.StartsWith(scheme, StringComparison.Ordinal))
        { builder.Issue("opcua-uri", "OPC-UA-Verbindung benötigt opc.tcp://host:port.", data); return; }
        var address = scheme.Length;
        var at = uri.IndexOf('@', address);
        if (at >= 0)
        {
            var colon = uri.IndexOf(':', address);
            if (colon < 0 || colon > at) builder.Issue("opcua-login", "Zugangsdaten benötigen Benutzername:Passwort.", data);
            else
            {
                builder.Add("username", slice.Subrange(data, address, colon - address));
                builder.Add("password", slice.Subrange(data, colon + 1, at - colon - 1));
            }
            address = at + 1;
        }
        HostPort(builder, slice.Subrange(data, address, uri.Length - address), "host");
    }

    private static void HostPort(CabBindingBuilder builder, CabSourceRange data, string name)
    {
        var text = builder.Slice.Protocol(data);
        var colon = text.LastIndexOf(':');
        if (colon < 0) { builder.Add(name, data); return; }
        builder.Add(name, builder.Slice.Subrange(data, 0, colon));
        builder.Add("port", builder.Slice.Subrange(data, colon + 1, text.Length - colon - 1));
    }
}
