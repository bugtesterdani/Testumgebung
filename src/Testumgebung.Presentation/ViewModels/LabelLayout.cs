using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Testumgebung.Presentation.ViewModels;

public enum LabelElementKind { Text, Code128, Qr, Line, Image }

public sealed class LabelElement
{
    public LabelElementKind Kind { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Size { get; set; } = 30;
    public int Length { get; set; } = 150;
    public string Value { get; set; } = "{Kennung}";
    public string Preview { get; set; } = "SN-123";
    public string? ExampleValues { get; set; }

    public string PreviewText()
    {
        if (ExampleValues is null)
            return Value?.Contains('{') == true ? Preview : Value ?? "";
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ExampleValues.ReplaceLineEndings("\n").Split('\n'))
        {
            var equals = line.IndexOf('=');
            if (equals > 0) examples[line[..equals].Trim()] = line[(equals + 1)..];
        }
        return System.Text.RegularExpressions.Regex.Replace(Value ?? "", @"\{\{|\}\}|\{([^{}]+)\}", match => match.Value switch
        {
            "{{" => "{",
            "}}" => "}",
            _ => examples.GetValueOrDefault(match.Groups[1].Value.Trim(), match.Value),
        });
    }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public byte[] Bitmap { get; set; } = [];
}

public sealed class LabelLayout
{
    public const string Parameter = "ZplLayout";
    public const int MaximumDots = 4096;
    public int WidthMm { get; set; } = 60;
    public int HeightMm { get; set; } = 40;
    public int Dpi { get; set; } = 203;
    public decimal GapMm { get; set; } = 3;
    public List<LabelElement> Elements { get; set; } = [];

    public int WidthDots => ToDots(WidthMm);
    public int HeightDots => ToDots(HeightMm);

    private int ToDots(int mm) => (int)Math.Clamp(Math.Round((double)mm * Dpi / 25.4), 1, MaximumDots);

    public string Save() => JsonSerializer.Serialize(this);
    public static LabelLayout Load(string json) => JsonSerializer.Deserialize<LabelLayout>(json) ?? new();

    public string ToZpl()
    {
        if (WidthMm <= 0 || HeightMm <= 0 || Dpi <= 0)
            throw new InvalidDataException("Breite, Höhe und Auflösung müssen positiv sein.");
        var zpl = new StringBuilder(FormattableString.Invariant($"^XA\n^CI28\n^PW{WidthDots}\n^LL{HeightDots}\n"));
        foreach (var element in Elements ?? throw new InvalidDataException("Etikettenelemente fehlen."))
        {
            if (element is null) throw new InvalidDataException("Leeres Etikettenelement.");
            zpl.Append(CultureInfo.InvariantCulture, $"^FO{Math.Clamp(element.X, 0, WidthDots - 1)},{Math.Clamp(element.Y, 0, HeightDots - 1)}");
            zpl.Append(Render(element)).Append('\n');
        }
        return zpl.Append("^XZ").ToString();
    }

    private static string Render(LabelElement element)
    {
        var size = Math.Clamp(element.Size, 1, MaximumDots);
        var length = Math.Clamp(element.Length, 1, MaximumDots);
        return element.Kind switch
        {
            LabelElementKind.Text => FormattableString.Invariant($"^A0N,{size},{size}^FD{Field(element.Value)}^FS"),
            LabelElementKind.Code128 => FormattableString.Invariant($"^BY2^BCN,{size},Y,N,N^FD{Field(element.Value)}^FS"),
            LabelElementKind.Qr => FormattableString.Invariant($"^BQN,2,{Math.Clamp(size, 1, 10)}^FDLA,{Field(element.Value)}^FS"),
            LabelElementKind.Line => FormattableString.Invariant($"^GB{length},{size},{size}^FS"),
            LabelElementKind.Image => Graphic(element),
            _ => throw new InvalidDataException("Unbekanntes Etikettenelement."),
        };
    }

    private static string Field(string? value)
    {
        if (value is null || value.Any(character => character is '^' or '~' || char.IsControl(character)))
            throw new InvalidDataException("Etikettentext darf keine ZPL-Steuerzeichen enthalten.");
        return value;
    }

    private static string Graphic(LabelElement element)
    {
        var rowBytes = BytesPerRow(element.ImageWidth);
        if (element.ImageHeight is < 1 or > MaximumDots || element.Bitmap?.Length != rowBytes * element.ImageHeight)
            throw new InvalidDataException("Ungültige Bilddaten im Etikett.");
        return FormattableString.Invariant($"^GFA,{element.Bitmap.Length},{element.Bitmap.Length},{rowBytes},{Convert.ToHexString(element.Bitmap)}^FS");
    }

    public static int BytesPerRow(int width)
        => width is >= 1 and <= MaximumDots ? (width + 7) / 8 : throw new InvalidDataException("Ungültige Bildbreite.");

    public static byte[] PackMonochrome(byte[] bgra, int width, int height)
    {
        var stride = BytesPerRow(width);
        var result = new byte[stride * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            var lightness = (29 * bgra[index] + 150 * bgra[index + 1] + 77 * bgra[index + 2]) / 256;
            var overWhite = 255 - (255 - lightness) * bgra[index + 3] / 255;
            if (overWhite < 128) result[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
        }
        return result;
    }
}
