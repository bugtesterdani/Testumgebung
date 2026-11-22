using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

public sealed record CabResource(CabBoundCommand Command, CabSyntaxNode Block, string Type, string Name);

/// <summary>Exchanges download files without decoding neighbouring source bytes.</summary>
public static class CabResourceEditor
{
    private static CabParameterRule TypeRule => CabDefinitions.Default.Commands.Single(c => c.Catalog.Command == "d").Parameters.Single(p => p.Parameter.Name == "type");
    public static IReadOnlyList<CabCatalogValue> Types => TypeRule.Parameter.Values;
    public static IReadOnlyList<CabPropertyChoice> TypeChoices(CabPrinterGeneration generation)
        => new CabPropertyDefinition(TypeRule, generation).Choices;

    public static CabSourceEditor Add(CabSourceEditor editor, string type, string name, byte[] file, CabJobItem? target = null)
    {
        if (!Types.Any(t => t.Value == type)) throw new ArgumentException("Unbekannter Downloadtyp.", nameof(type));
        if (name.Length == 0 || name.Any(c => char.IsControl(c) || c is ';' or '[' or ']'))
            throw new ArgumentException("Dateiname enthält reservierte Zeichen oder ist leer.", nameof(name));
        var header = CabEncoding.Get(editor.SetupCodePage).GetBytes("d " + type + ";" + name + editor.Document.LineEnding);
        return CabJobEditor.Insert(editor, header.Concat(Frame(file)).ToArray(), target);
    }

    public static IReadOnlyList<CabResource> Resources(CabSourceEditor editor)
    {
        var blocks = CabSourceEditor.Physical(editor.Document.Root).Where(n => n.Kind == CabNodeKind.BinaryBlock).ToArray();
        return editor.Validation.Commands.Where(c => c.Syntax.Command == "d" || c.Syntax.Kind == CabNodeKind.BinaryBlock)
            .Select(c => (Command: c, Data: c.Parameter("data")))
            .Where(x => x.Data?.Syntax is not null)
            .Select(x => new CabResource(x.Command,
                blocks.Single(b => b.Range.Offset <= x.Data!.Syntax!.Range.Offset && b.Range.End >= x.Data.Syntax.Range.End),
                x.Command.Parameter("type")?.Value ?? "Binärdaten", x.Command.Parameter("name")?.Value ?? x.Command.Syntax.Command))
            .GroupBy(r => r.Block.Range).Select(g => g.OrderByDescending(r => r.Command.Syntax.Command == "d").First())
            .ToArray();
    }

    public static byte[] Read(CabSourceEditor editor, CabResource resource)
    {
        var bytes = editor.Document.Bytes(resource.Block.Range);
        return resource.Block.Command switch
        {
            "<BASE64>" => DecodeBase64(bytes),
            "ESC:" => Unframe(bytes, "\u001b:", "\u001bend-of-data"),
            "ESC." => Unescape(resource.Block, Unframe(bytes, "\u001b.", "\u001b.")),
            _ => throw new InvalidOperationException("Unbekannter Dateirahmen."),
        };
    }

    public static CabSourceEditor Replace(CabSourceEditor editor, CabResource resource, byte[] file)
    {
        VerifyExchangeBoundary(editor, resource);
        var frame = Frame(file, resource.Block.Command);
        return new(editor.Document.Write([new(resource.Block.Range, frame)]), editor.SetupCodePage, editor.Settings);
    }

    private static void VerifyExchangeBoundary(CabSourceEditor editor, CabResource resource)
    {
        if (resource.Block.Children.Count > 0)
            throw new InvalidDataException("Der Dateiblock enthält Steuerbefehle. Ein Dateiaustausch würde diese entfernen und wird deshalb nicht übernommen.");
        var framing = resource.Block.Command;
        var opening = framing == "<BASE64>" ? "<BASE64>" : framing == "ESC:" ? "\u001b:" : "\u001b.";
        var closing = framing == "<BASE64>" ? "</BASE64>" : framing == "ESC:" ? "\u001bend-of-data" : "\u001b.";
        _ = Unframe(editor.Document.Bytes(resource.Block.Range), opening, closing);
    }

    public static byte[] Frame(byte[] file, string framing = "ESC.")
    {
        if (framing == "<BASE64>") return Encoding.ASCII.GetBytes("<BASE64>" + Convert.ToBase64String(file) + "</BASE64>");
        if (framing == "ESC:" && file.AsSpan().IndexOf(Encoding.ASCII.GetBytes("\u001bend-of-data")) < 0)
            return Encoding.ASCII.GetBytes("\u001b:").Concat(file).Concat(Encoding.ASCII.GetBytes("\u001bend-of-data")).ToArray();
        using var output = new MemoryStream();
        output.Write([27, (byte)'.']);
        foreach (var value in file)
        {
            output.WriteByte(value);
            if (value == 27) output.WriteByte(27);
        }
        output.Write([27, (byte)'.']);
        return output.ToArray();
    }

    private static byte[] DecodeBase64(byte[] bytes)
        => Convert.FromBase64String(Encoding.ASCII.GetString(Unframe(bytes, "<BASE64>", "</BASE64>")));

    private static byte[] Unframe(byte[] bytes, string opening, string closing)
    {
        var first = Encoding.ASCII.GetBytes(opening); var last = Encoding.ASCII.GetBytes(closing);
        if (bytes.Length < first.Length + last.Length || !bytes.AsSpan().StartsWith(first) || !bytes.AsSpan().EndsWith(last))
            throw new InvalidDataException("Der Dateirahmen ist unvollständig.");
        return bytes[first.Length..^last.Length];
    }

    private static byte[] Unescape(CabSyntaxNode block, byte[] bytes)
    {
        if (block.Children.Count > 0) throw new InvalidDataException("Der Dateiblock enthält eingebettete Steuerbefehle. Keine eindeutige Dateivorschau möglich.");
        using var output = new MemoryStream();
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            if (value == 27)
            {
                if (++index >= bytes.Length || bytes[index] != 27) throw new InvalidDataException("Ungültige ESC-Maskierung im Dateiblock.");
            }
            output.WriteByte(value);
        }
        return output.ToArray();
    }
}
