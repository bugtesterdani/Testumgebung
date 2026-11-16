using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Renders an editable label as a self-contained cab JScript print job.</summary>
public static class CabLabelLayout
{
    public const string Parameter = "CabLblLayout";
    private const byte Escape = 0x1B;

    public static byte[] Render(LabelLayout layout) => Render(layout, out _);

    public static byte[] Render(LabelLayout layout, out int textOffset)
    {
        if (layout.WidthMm <= 0 || layout.HeightMm <= 0 || layout.Dpi <= 0 || layout.GapMm < 0)
            throw new InvalidDataException("Breite, Höhe und Auflösung müssen positiv sein. Der Etikettenabstand darf nicht negativ sein.");

        using var output = new MemoryStream();
        Write(output, "\u001BoUTF-8;\rm m\r");
        var sensor = layout.GapMm == 0 ? "e" : "l1";
        var fields = new StringBuilder(FormattableString.Invariant($"J\rS {sensor};0,0,{layout.HeightMm},{layout.HeightMm + layout.GapMm},{layout.WidthMm}\r"));
        foreach (var element in layout.Elements ?? throw new InvalidDataException("Etikettenelemente fehlen."))
            fields.Append(Field(layout, element ?? throw new InvalidDataException("Leeres Etikettenelement."), output)).Append('\r');

        textOffset = checked((int)output.Length);
        Write(output, fields.Append("A 1\r").ToString());
        return output.ToArray();
    }

    private static string Field(LabelLayout layout, LabelElement item, Stream output)
    {
        var x = Mm(Math.Clamp(item.X, 0, layout.WidthDots - 1), layout.Dpi);
        var y = Mm(Math.Clamp(item.Y, 0, layout.HeightDots - 1), layout.Dpi);
        var size = Mm(Math.Clamp(item.Size, 1, LabelLayout.MaximumDots), layout.Dpi);
        var length = Mm(Math.Clamp(item.Length, 1, LabelLayout.MaximumDots), layout.Dpi);
        return item.Kind switch
        {
            LabelElementKind.Text => $"T {x},{y},0,5,{size};{Literal(item.Value)}",
            LabelElementKind.Code128 => $"B {x},{y},0,CODE128,{size},{Mm(2, layout.Dpi)};{Code128(item.Value)}",
            LabelElementKind.Qr => $"B {x},{y},0,QRCODE,{Mm(Math.Clamp(item.Size, 1, 10), layout.Dpi)};{Literal(item.Value)}",
            LabelElementKind.DataMatrix => MatrixField(x, y, layout.Dpi, item, "DATAMATRIX"),
            LabelElementKind.Gs1DataMatrix => MatrixField(x, y, layout.Dpi, item, "GS1-DATAMATRIX"),
            LabelElementKind.Gs1Qr => MatrixField(x, y, layout.Dpi, item, "GS1QRCODE"),
            LabelElementKind.Line =>$"G {x},{y},0;L:{length},{size}",
            LabelElementKind.Image => $"I {x},{y},0;{DownloadImage(output, item)}",
            _ => throw new InvalidDataException("Unbekanntes Etikettenelement."),
        };
    }

    private static string MatrixField(string x, string y, int dpi, LabelElement item, string type)
        => $"B {x},{y},0,{type},{Mm(Math.Clamp(item.Size, 1, 10), dpi)};{Literal(item.Value)}";

    private static string Mm(int dots, int dpi) => (dots * 25.4m / dpi).ToString("0.###", CultureInfo.InvariantCulture);

    private static string Literal(string? value)
    {
        if (value is null || value.Any(c => char.IsControl(c) || c is '[' or ']'))
            throw new InvalidDataException("cab-Etikettentext darf keine Steuerzeichen oder eckigen Klammern enthalten.");
        return value;
    }

    private static string Code128(string? value)
    {
        var text = Literal(value);
        if (text.Length == 0 || (!text.Contains('{') && text.Any(c => !char.IsAscii(c))))
            throw new InvalidDataException("cab Code 128 benötigt mindestens ein druckbares ASCII-Zeichen.");
        return text;
    }

    private static string DownloadImage(Stream output, LabelElement item)
    {
        var bmp = Bitmap(item);
        var name = Convert.ToHexString(SHA256.HashData(bmp).AsSpan(0, 4));
        Write(output, $"d BMP;{name}\r\u001B.");
        foreach (var value in bmp)
        {
            output.WriteByte(value);
            if (value == Escape) output.WriteByte(Escape);
        }
        Write(output, "\u001B.\r");
        return name;
    }

    private static byte[] Bitmap(LabelElement item)
    {
        var rowBytes = LabelLayout.BytesPerRow(item.ImageWidth);
        if (item.ImageHeight is < 1 or > LabelLayout.MaximumDots || item.Bitmap?.Length != rowBytes * item.ImageHeight)
            throw new InvalidDataException("Ungültige Bilddaten im Etikett.");

        // BMP rows are aligned to four bytes and stored bottom-up.
        const int pixelOffset = 62;
        var padding = new byte[(4 - rowBytes % 4) % 4];
        var imageSize = (rowBytes + padding.Length) * item.ImageHeight;
        using var bitmap = new MemoryStream(pixelOffset + imageSize);
        using var writer = new BinaryWriter(bitmap);
        WriteBitmapHeader(writer, item.ImageWidth, item.ImageHeight, pixelOffset, imageSize);
        for (var y = item.ImageHeight - 1; y >= 0; y--)
        {
            writer.Write(item.Bitmap, y * rowBytes, rowBytes);
            writer.Write(padding);
        }
        return bitmap.ToArray();
    }

    private static void WriteBitmapHeader(BinaryWriter writer, int width, int height, int pixelOffset, int imageSize)
    {
        writer.Write((ushort)0x4D42); // BM
        writer.Write(pixelOffset + imageSize);
        writer.Write(0);
        writer.Write(pixelOffset);
        writer.Write(40); // BITMAPINFOHEADER
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1); // planes
        writer.Write((ushort)1); // bits per pixel
        writer.Write(0); // BI_RGB
        writer.Write(imageSize);
        writer.Write(0); // Horizontal resolution
        writer.Write(0); // Vertical resolution
        writer.Write(2); // Palette size
        writer.Write(0); // Important colors
        writer.Write(0x00FFFFFF); // 0 = white
        writer.Write(0); // 1 = black
    }

    private static void Write(Stream output, string text) => output.Write(Encoding.UTF8.GetBytes(text));
}
