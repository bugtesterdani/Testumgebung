using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Loads every byte into the lossless document and projects representable objects for the existing canvas.</summary>
public static class CabLabelImport
{
    public static LabelLayout Read(byte[] data, int dpi = 203, string setupCodePage = "UTF-8")
    {
        if (dpi is < 50 or > 1200) throw new InvalidDataException("Druckauflösung muss zwischen 50 und 1200 dpi liegen.");
        var document = CabParser.Parse(data, new(setupCodePage));
        var layout = new LabelLayout { Dpi = dpi };
        var state = new CabImportedSource { Data = document.Write(), SetupCodePage = setupCodePage, Dpi = dpi };
        layout.CabSource = state;
        var images = new Dictionary<string, LabelElement>(StringComparer.Ordinal);
        var nodes = Commands(document.Root).ToArray();
        var commands = CabValidator.Validate(document).Commands;
        var firstJobStart = commands.FirstOrDefault(c => c.Syntax.Command == "J")?.Syntax.Range.Offset;
        var firstJob = document.Root.Children.FirstOrDefault(n => n.Kind == CabNodeKind.Job && n.Range.Offset == firstJobStart);
        var projectable = commands.Where(c => c.Syntax.Unit == CabUnit.Millimetres).Select(c => c.Syntax.Range).ToHashSet();
        state.InsertOffset = firstJob?.Children.LastOrDefault(n => n.Command == "A" && projectable.Contains(n.Range))?.Range.Offset ?? data.Length;
        for (var index = 0; index < nodes.Length; index++)
        {
            if (nodes[index].Kind == CabNodeKind.LabelCommand && !projectable.Contains(nodes[index].Range)) continue;
            Project(nodes[index], index + 1 < nodes.Length ? nodes[index + 1] : null, firstJob, document, layout, state, images);
        }
        state.WidthMm = layout.WidthMm; state.HeightMm = layout.HeightMm; state.GapMm = layout.GapMm;
        return layout;
    }

    private static bool IsVisibleJobNode(CabSyntaxNode node, CabSyntaxNode? job)
        => job is not null && node.Range.Offset >= job.Range.Offset && node.Range.End <= job.Range.End
            && node.Kind == CabNodeKind.LabelCommand && node.Unit == CabUnit.Millimetres;

    private static void ReadDownload(string command, CabSyntaxNode? next, CabDocument document, Dictionary<string, LabelElement> images)
    {
        if (command.StartsWith("d BMP;", StringComparison.Ordinal) && next?.Kind == CabNodeKind.BinaryBlock)
            images[command[6..]] = ReadBitmap(Unescape(document.Bytes(next.Range)));
    }

    private static void Project(CabSyntaxNode node, CabSyntaxNode? next, CabSyntaxNode? job, CabDocument document,
        LabelLayout layout, CabImportedSource state, Dictionary<string, LabelElement> images)
    {
        var command = document.Decode(node)?.TrimStart();
        if (command is null) return;
        try
        {
            ReadDownload(command, next, document, images);
            if (!IsVisibleJobNode(node, job)) return;
            if (command.StartsWith("S ", StringComparison.Ordinal))
            {
                ReadSize(command, layout);
                state.SizeOffset = node.Range.Offset;
            }
            else if (node.Command is "T" or "B" or "G" or "I")
            {
                var item = ReadElement(command, layout, images);
                item.CabSourceOffset = node.Range.Offset;
                layout.Elements.Add(item);
                state.Elements.Add(new() { Offset = node.Range.Offset,
                    Original = JsonSerializer.Deserialize<LabelElement>(JsonSerializer.Serialize(item))! });
            }
        }
        catch (Exception error) when (error is InvalidDataException or FormatException or OverflowException)
        {
            // A projection failure never removes or rewrites the original command.
        }
    }

    internal static IEnumerable<CabSyntaxNode> Commands(CabSyntaxNode parent)
        => parent.Children.SelectMany(n => n.Kind == CabNodeKind.Job ? Commands(n) : new[] { n })
            .Where(n => n.Command.Length > 0);

    private static byte[] Unescape(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0x1b || bytes[1] != '.' || bytes[^2] != 0x1b || bytes[^1] != '.')
            throw new InvalidDataException("Keine vollständig darstellbaren BMP-Daten.");
        using var output = new MemoryStream();
        for (var i = 2; i < bytes.Length - 2; i++)
        {
            output.WriteByte(bytes[i]);
            if (bytes[i] == 0x1b && (++i >= bytes.Length - 2 || bytes[i] != 0x1b))
                throw new InvalidDataException("Binärblock enthält eingebettete ESC-Befehle.");
        }
        return output.ToArray();
    }

    private static LabelElement ReadBitmap(byte[] bytes)
    {
        if (bytes.Length < 62 || bytes[0] != 'B' || bytes[1] != 'M')
            throw new InvalidDataException("Eingebettetes Bild ist kein monochromes BMP.");
        var span = bytes.AsSpan();
        var width = BinaryPrimitives.ReadInt32LittleEndian(span[18..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(span[22..]);
        var start = BinaryPrimitives.ReadInt32LittleEndian(span[10..]);
        ValidateBitmapDimensions(width, height, start);
        ValidateBitmapFormat(span);
        var rowBytes = LabelLayout.BytesPerRow(width);
        var stride = (rowBytes + 3) / 4 * 4;
        if ((long)start + (long)stride * height > bytes.Length)
            throw new InvalidDataException("BMP-Bilddaten sind unvollständig.");
        var palette = span.Slice(54, 8);
        var whiteFirst = palette.SequenceEqual(new byte[] { 255, 255, 255, 0, 0, 0, 0, 0 });
        if (!whiteFirst && !palette.SequenceEqual(new byte[] { 0, 0, 0, 0, 255, 255, 255, 0 }))
            throw new InvalidDataException("BMP-Farbpalette muss Schwarz und Weiß enthalten.");
        var bitmap = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < rowBytes; x++)
            {
                var value = bytes[start + (height - y - 1) * stride + x];
                bitmap[y * rowBytes + x] = whiteFirst ? value : (byte)~value;
            }
        }
        return new() { Kind = LabelElementKind.Image, ImageWidth = width, ImageHeight = height, Bitmap = bitmap };
    }

    private static void ValidateBitmapDimensions(int width, int height, int start)
    {
        if (width is < 1 or > LabelLayout.MaximumDots || height is < 1 or > LabelLayout.MaximumDots || start < 62)
            throw new InvalidDataException("Ungültige BMP-Abmessungen.");
    }

    private static void ValidateBitmapFormat(ReadOnlySpan<byte> span)
    {
        if (BinaryPrimitives.ReadInt32LittleEndian(span[14..]) != 40
            || BinaryPrimitives.ReadInt16LittleEndian(span[26..]) != 1
            || BinaryPrimitives.ReadInt16LittleEndian(span[28..]) != 1
            || BinaryPrimitives.ReadInt32LittleEndian(span[30..]) != 0)
            throw new InvalidDataException("Unterstützt werden unkomprimierte monochrome Windows-BMP-Bilder.");
    }

    private static void ReadSize(string command, LabelLayout layout)
    {
        var halves = command[2..].Split(';');
        if (halves.Length != 2 || halves[0] is not ("l1" or "e")) throw Unsupported(command);
        var values = halves[1].Split(',');
        if (values.Length != 5 || Number(values[0]) != 0 || Number(values[1]) != 0) throw Unsupported(command);
        var height = Number(values[2]);
        var pitch = Number(values[3]);
        var width = Number(values[4]);
        if (height < 1 || width < 1 || height != decimal.Truncate(height) || width != decimal.Truncate(width)
            || pitch < height || width > 500 || height > 500)
            throw new InvalidDataException("Etikettenmaße benötigen ganze Millimeter von 1 bis 500 und einen gültigen Abstand.");
        layout.HeightMm = (int)height;
        layout.WidthMm = (int)width;
        layout.GapMm = pitch - height;
    }

    private static LabelElement ReadElement(string command, LabelLayout layout, Dictionary<string, LabelElement> images)
    {
        if (command.Length < 3 || command[1] != ' ') throw Unsupported(command);
        var separator = command.IndexOf(';');
        if (separator < 0) throw Unsupported(command);
        var fields = command[2..separator].Split(',');
        if (fields.Length < 3 || Number(fields[2]) != 0) throw Unsupported(command);
        var value = command[(separator + 1)..];
        var item = ReadContent(command[0], fields, value, layout, images);
        item.X = Dots(fields[0], layout, true);
        item.Y = Dots(fields[1], layout, true);
        if (item.X >= layout.WidthDots || item.Y >= layout.HeightDots) throw Unsupported(command);
        return item;
    }

    private static LabelElement ReadContent(char kind, string[] fields, string value, LabelLayout layout, Dictionary<string, LabelElement> images)
        => kind switch
        {
            'T' when fields.Length == 5 && fields[3] == "5" => new LabelElement { Kind = LabelElementKind.Text, Size = Dots(fields[4], layout), Value = value },
            'B' => ReadBarcode(fields, value, layout),
            'G' when fields.Length == 3 => ReadLineElement(value, layout),
            'I' when fields.Length == 3 && images.TryGetValue(value, out var image) => new LabelElement
                { Kind = LabelElementKind.Image, ImageWidth = image.ImageWidth, ImageHeight = image.ImageHeight, Bitmap = image.Bitmap.ToArray() },
            _ => throw Unsupported(kind.ToString())
        };

    private static LabelElement ReadBarcode(string[] fields, string value, LabelLayout layout)
    {
        if (fields.Length < 5) throw Unsupported("B");
        var kind = fields[3] switch
        {
            "CODE128" => LabelElementKind.Code128,
            "QRCODE" => LabelElementKind.Qr,
            "DATAMATRIX" => LabelElementKind.DataMatrix,
            "GS1-DATAMATRIX" => LabelElementKind.Gs1DataMatrix,
            "GS1QRCODE" => LabelElementKind.Gs1Qr,
            _ => throw Unsupported("B " + fields[3])
        };
        if (kind == LabelElementKind.Code128)
        {
            if (fields.Length != 6 || Dots(fields[5], layout) != 2) throw Unsupported("CODE128-Modulbreite");
        }
        else if (fields.Length != 5) throw Unsupported("Barcode-Optionen");
        var size = Dots(fields[4], layout);
        if (kind != LabelElementKind.Code128 && size > 10) throw Unsupported("Matrix-Modulgröße");
        return new() { Kind = kind, Value = value, Size = size };
    }

    private static LabelElement ReadLineElement(string value, LabelLayout layout)
    {
        if (!value.StartsWith("L:", StringComparison.Ordinal)) throw Unsupported("G " + value);
        var parts = value[2..].Split(',');
        if (parts.Length != 2) throw Unsupported("G " + value);
        return new() { Kind = LabelElementKind.Line, Length = Dots(parts[0], layout), Size = Dots(parts[1], layout) };
    }

    private static decimal Number(string value) => decimal.Parse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static int Dots(string value, LabelLayout layout, bool position = false)
    {
        var dots = decimal.Round(Number(value) * layout.Dpi / 25.4m);
        if (dots < (position ? 0 : 1) || dots > LabelLayout.MaximumDots)
            throw new InvalidDataException("Position oder Größe liegt außerhalb des Designerbereichs.");
        return (int)dots;
    }

    private static InvalidDataException Unsupported(string command)
        => new("Die LBL enthält einen im Designer noch nicht unterstützten Befehl oder Parameter: " + command);
}
