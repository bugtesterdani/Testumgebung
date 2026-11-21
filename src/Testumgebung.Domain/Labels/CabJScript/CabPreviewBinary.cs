using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

internal sealed partial class CabPreviewContext
{
    private static readonly IReadOnlyDictionary<int, (int Bits, bool BigEndian)> BinaryFormats = new Dictionary<int, (int, bool)>
    { [69]=(8,false), [70]=(16,true), [71]=(16,false), [72]=(32,true), [73]=(32,false), [74]=(64,true), [75]=(64,false) };
    private CabPreviewValue Binary(CabContentField field, int id, IReadOnlyList<string> args)
    {
        if (id == 68) return new(ConvertBase(args));
        if (id == 76) return BitField(args);
        if (id == 80) return new(Hex(field, args));
        if (id == 98)
        {
            var value = Resolve(args[0]);
            RequireResolved(value);
            var codePage = args.Count == 2 ? args[1][2..] : _currentCodePage;
            return new(Convert.ToHexString(SHA256.HashData(value.Bytes ?? CabEncoding.Get(codePage).GetBytes(value.Text))));
        }
        if (id == 103) return Unicode(field, args[0]);
        return BinaryIntegers(BinaryFormats[id], args);
    }

    private CabPreviewValue BinaryIntegers((int Bits, bool BigEndian) format, IReadOnlyList<string> args)
    {
        using var output = new MemoryStream();
        foreach (var arg in args)
        {
            var number = Unsigned(Resolve(arg).Text);
            if (number >= BigInteger.One << format.Bits) throw new FormatException("Binärwert überschreitet die Bitbreite; keine stille Abschneidung.");
            var bytes = number.ToByteArray(true, format.BigEndian);
            var padded = new byte[format.Bits / 8];
            bytes.CopyTo(padded, format.BigEndian ? padded.Length - bytes.Length : 0);
            output.Write(padded);
        }
        return BinaryValue(output.ToArray());
    }

    private CabPreviewValue BitField(IReadOnlyList<string> args)
    {
        var widths = args[0].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var values = args[1].Split(',');
        if (widths.Length != values.Length || widths.Any(w => w is < 1 or > 32)) throw new FormatException("BITFIELD benötigt gleich viele Werte und Breiten von 1…32.");
        var total = widths.Sum();
        if (total > 65536) throw new FormatException("BITFIELD überschreitet die lokale Vorschaugrenze.");
        var number = BigInteger.Zero;
        for (var index = 0; index < widths.Length; index++)
        {
            var value = Unsigned(Resolve(values[index]).Text);
            if (value >= BigInteger.One << widths[index]) throw new FormatException("BITFIELD-Wert passt nicht in seine Bitbreite.");
            number = (number << widths[index]) | value;
        }
        number <<= (8 - total % 8) % 8;
        var bytes = number.ToByteArray(true, true);
        var padded = new byte[(total + 7) / 8];
        bytes.CopyTo(padded, padded.Length - bytes.Length);
        return BinaryValue(padded);
    }

    private string Hex(CabContentField field, IReadOnlyList<string> args)
    {
        using var bytes = new MemoryStream();
        foreach (var arg in args)
        {
            var value = Resolve(arg); RequireResolved(value);
            if (value.Bytes is { } binary) bytes.Write(binary);
            else
            {
                if (value.Text.Any(char.IsSurrogate)) throw new FormatException("HEX von Surrogatpaaren ist im Handbuch nicht spezifiziert.");
                foreach (var c in value.Text) bytes.WriteByte((byte)(c & 255));
            }
        }
        Note(field, "Text-HEX verwendet das niederwertige Byte je Unicode-Zeichen, keine UTF-8-Kodierung.");
        return Convert.ToHexString(bytes.ToArray());
    }

    private string ConvertBase(IReadOnlyList<string> args)
    {
        if (!fields.TryGetValue(args[2], out var value)) throw new FormatException("B2B benötigt ein zuvor definiertes Datenfeld.");
        RequireResolved(value);
        var source = Alphabet(args[0]); var target = Alphabet(args[1]);
        if (value.Text.Length > 4096) throw new FormatException("Basisumwandlung überschreitet die lokale Vorschaugrenze.");
        var number = BigInteger.Zero;
        foreach (var c in value.Text)
        {
            var digit = source.IndexOf(c);
            if (digit < 0) throw new FormatException("Zeichen außerhalb des Quellalphabets.");
            number = number * source.Length + digit;
        }
        if (value.Text.Length == 0) throw new FormatException("Leeres Datenfeld.");
        if (number.IsZero) return target[..1];
        var result = new StringBuilder();
        while (number > 0)
        {
            number = BigInteger.DivRem(number, target.Length, out var digit);
            result.Insert(0, target[(int)digit]);
        }
        return result.ToString();
    }

    private static string Alphabet(string specification)
    {
        var alphabet = specification switch
        {
            "B" => "01", "O" => "01234567", "D" => "0123456789", "H" => "0123456789ABCDEF",
            "A" => "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ", _ when specification.StartsWith("U:", StringComparison.Ordinal) => specification[2..],
            _ => throw new FormatException("Unbekannte Zählbasis.")
        };
        if (alphabet.Length < 2 || alphabet.Distinct().Count() != alphabet.Length) throw new FormatException("Benutzeralphabet benötigt mindestens zwei unterschiedliche Zeichen.");
        return alphabet;
    }

    private CabPreviewValue Unicode(CabContentField field, string argument)
    {
        if (argument.StartsWith('$'))
        {
            var scalar = int.Parse(argument.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            return new(char.ConvertFromUtf32(scalar));
        }
        string[] controls = ["NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL", "BS", "HT", "LF", "VT", "FF", "CR", "SO", "SI", "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB", "CAN", "EM", "SU", "ESC", "FS", "GS", "RS", "US"];
        var index = Array.IndexOf(controls, argument);
        if (index >= 0) return new(((char)index).ToString());
        if (argument is "FNC1" or "CODEA" or "CODEB" or "CODEC")
        {
            Note(field, "Semantisches Code-128-Steuerzeichen; keine Unicode-Glyphe.", "preview-barcode-control");
            return new("⟦" + argument + "⟧");
        }
        throw new FormatException("Unbekannter Unicode-Steuerzeichenname.");
    }

    private static BigInteger Unsigned(string text)
    {
        var number = text.StartsWith('$') ? BigInteger.Parse("0" + text[1..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : BigInteger.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        if (number < 0) throw new FormatException("Negative Binärwerte sind nicht spezifiziert.");
        return number;
    }

    private static CabPreviewValue BinaryValue(byte[] bytes) => new("⟦Binär " + Convert.ToHexString(bytes) + "⟧", bytes);
    private static void RequireResolved(CabPreviewValue value)
    {
        if (value.Placeholder) throw new InvalidOperationException("Abhängiger Druckerwert ist nur ein Platzhalter.");
    }
}
