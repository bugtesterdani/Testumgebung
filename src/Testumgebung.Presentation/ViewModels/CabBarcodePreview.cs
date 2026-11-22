using System.Globalization;
using System.Text.RegularExpressions;
using Testumgebung.Domain.Labels.CabJScript;
using ZXing;
using ZXing.Common;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Generates genuine supported symbols; never draws invented bars for unsupported variants.</summary>
public static class CabBarcodePreview
{
    private static readonly IReadOnlyDictionary<string, BarcodeFormat> Formats = new Dictionary<string, BarcodeFormat>
    {
        ["JS-4.2.3"] = BarcodeFormat.ITF, ["JS-4.2.6"] = BarcodeFormat.AZTEC, ["JS-4.2.7"] = BarcodeFormat.CODABAR,
        ["JS-4.2.9"] = BarcodeFormat.CODE_39, ["JS-4.2.10"] = BarcodeFormat.CODE_93, ["JS-4.2.11"] = BarcodeFormat.CODE_128,
        ["JS-4.2.12"] = BarcodeFormat.DATA_MATRIX, ["JS-4.2.15"] = BarcodeFormat.EAN_8, ["JS-4.2.16"] = BarcodeFormat.EAN_13,
        ["JS-4.2.19"] = BarcodeFormat.CODE_128, ["JS-4.2.27"] = BarcodeFormat.DATA_MATRIX, ["JS-4.2.28"] = BarcodeFormat.QR_CODE,
        ["JS-4.2.36"] = BarcodeFormat.MSI, ["JS-4.2.37"] = BarcodeFormat.PDF_417, ["JS-4.2.38"] = BarcodeFormat.PLESSEY,
        ["JS-4.2.41"] = BarcodeFormat.QR_CODE, ["JS-4.2.43"] = BarcodeFormat.UPC_A, ["JS-4.2.44"] = BarcodeFormat.UPC_E,
        ["JS-4.2.45"] = BarcodeFormat.UPC_E,
    };

    public static CabBarcodeSymbol Create(CabBoundCommand command, CabGraphicContentValue content)
    {
        var fallback = Estimate(command, content.Text);
        if (content.Value.Placeholder) return fallback with { Note = "Beschriftete Vorschaufläche: Beispielwert oder Hardwarewert fehlt." };
        if (!Formats.TryGetValue(command.Definition.Catalog.Id, out var format)) return fallback;
        if (!SupportedOptions(command)) return fallback with { Note = "Beschriftete Vorschaufläche: diese Options-/Untercodevariante wird nicht erzeugt. Größe ist eine berechnete Näherung." };
        try
        {
            var hints = Hints(command, format);
            var text = content.Text;
            if (command.Definition.Catalog.Id is "JS-4.2.19" or "JS-4.2.27" or "JS-4.2.28") text = CabBarcodeGs1.Payload(text);
            if (format == BarcodeFormat.CODE_128) text = text.Replace('\u001d', '\u00f1');
            var matrix = new MultiFormatWriter().encode(text, format, 0, 1, hints);
            if ((long)matrix.Width * matrix.Height > 1_048_576) return fallback with { Note = "Beschriftete Vorschaufläche: Symbol überschreitet die Vorschaugrenze." };
            var modules = new bool[matrix.Width * matrix.Height];
            for (var y = 0; y < matrix.Height; y++)
                for (var x = 0; x < matrix.Width; x++) modules[y * matrix.Width + x] = matrix[x, y];
            return new(matrix.Width, matrix.Height, modules, "Echte Symbolerzeugung mit ZXing.Net 0.16.11; Vorschau mit Beispieldaten.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.FormatException or WriterException)
        { return fallback with { Note = "Beschriftete Vorschaufläche: " + error.Message + " Größe ist eine berechnete Näherung." }; }
    }

    private static bool SupportedOptions(CabBoundCommand command)
    {
        if (CabGraphicValues.Present(command, "subcode")) return false;
        if (CabGraphicValues.Text(command, "MODEL") is { Length: > 0 } model && model != "2") return false;
        var options = CabGraphicValues.Text(command, "options");
        return options.Split('+', StringSplitOptions.RemoveEmptyEntries).All(t => SupportedOption(command.Definition.Catalog.Id, t));
    }

    private static bool SupportedOption(string id, string option)
    {
        var pattern = id switch
        {
            "JS-4.2.28" or "JS-4.2.41" => @"^(?:EL[LMQH]|VERSION[0-9]+|MODEL2)$",
            "JS-4.2.12" or "JS-4.2.27" => @"^(?:ROWS[0-9]+|COLS[0-9]+)$",
            _ => "(?!)",
        };
        return Regex.IsMatch(option, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    private static Dictionary<EncodeHintType, object> Hints(CabBoundCommand command, BarcodeFormat format)
    {
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0, [EncodeHintType.CHARACTER_SET] = "UTF-8" };
        var id = command.Definition.Catalog.Id;
        if (id is "JS-4.2.19" or "JS-4.2.27" or "JS-4.2.28") hints[EncodeHintType.GS1_FORMAT] = true;
        if (id == "JS-4.2.27") hints[EncodeHintType.DATA_MATRIX_COMPACT] = true;
        if (format == BarcodeFormat.QR_CODE) QrHints(command, hints);
        if (format == BarcodeFormat.DATA_MATRIX) MatrixSize(command, hints);
        if (format == BarcodeFormat.PDF_417) hints[EncodeHintType.PDF417_ASPECT_RATIO] = Math.Max(1, (int)CabGraphicValues.Number(command, "ratio", 3));
        return hints;
    }

    private static void QrHints(CabBoundCommand command, Dictionary<EncodeHintType, object> hints)
    {
        var error = CabGraphicValues.Text(command, "EL");
        if (error.Length > 0) hints[EncodeHintType.ERROR_CORRECTION] = error;
        var version = (int)CabGraphicValues.Number(command, "VERSION");
        if (version > 0) hints[EncodeHintType.QR_VERSION] = version;
    }

    private static void MatrixSize(CabBoundCommand command, Dictionary<EncodeHintType, object> hints)
    {
        var rows = (int)CabGraphicValues.Number(command, "ROWS"); var columns = (int)CabGraphicValues.Number(command, "COLS");
        if (rows <= 0 || columns <= 0) return;
        hints[EncodeHintType.MIN_SIZE] = new Dimension(columns, rows); hints[EncodeHintType.MAX_SIZE] = new Dimension(columns, rows);
    }

    private static CabBarcodeSymbol Estimate(CabBoundCommand command, string text)
    {
        var id = command.Definition.Catalog.Id;
        var count = Math.Max(1, text.Length);
        var dimensions = id switch
        {
            "JS-4.2.4" => (20, 1), "JS-4.2.5" => (47, 1), "JS-4.2.13" => (107, 1), "JS-4.2.18" => (17, 1),
            "JS-4.2.33" => (30, 33), "JS-4.2.35" => MicroQr(command),
            "JS-4.2.29" or "JS-4.2.42" => (Math.Max(27, (int)CabGraphicValues.Number(command, "COLS", 43)), Math.Max(7, (int)CabGraphicValues.Number(command, "ROWS", 11))),
            _ => GeneralEstimate(command, count),
        };
        return new(dimensions.Item1, dimensions.Item2, null, "Beschriftete Vorschaufläche: Symbologie nicht erzeugt. Größe ist eine berechnete Näherung.");
    }

    private static (int, int) MicroQr(CabBoundCommand command)
    {
        var side = 11 + 2 * Math.Max(0, (int)CabGraphicValues.Number(command, "VERSION", 1) - 1);
        return (side, side);
    }

    private static (int, int) GeneralEstimate(CabBoundCommand command, int count)
    {
        if (CabGraphicValues.Present(command, "dotsize"))
        {
            var size = Math.Max(10, (int)Math.Ceiling(Math.Sqrt(count * 8)) + 8);
            if (size % 2 != 0) size++;
            return (Math.Max(size, (int)CabGraphicValues.Number(command, "COLS")), Math.Max(size, (int)CabGraphicValues.Number(command, "ROWS")));
        }
        return (Math.Max(20, count * 11 + 35), 1);
    }

    public static double ModuleWidth(CabBoundCommand command, int dpi)
    {
        if (CabGraphicValues.Present(command, "dotsize")) return CabGraphicValues.Length(command, "dotsize", dpi);
        if (CabGraphicValues.Present(command, "ne")) return CabGraphicValues.Length(command, "ne", dpi);
        var size = CabGraphicValues.Text(command, "size");
        if (size.StartsWith("SC", StringComparison.OrdinalIgnoreCase) && int.TryParse(size[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            double[] factors = [.8, .9, 1, 1.1, 1.2, 1.35, 1.5, 1.65, 1.85, 2];
            return .33 * dpi / 25.4 * factors[Math.Clamp(index, 0, 9)];
        }
        return .3 * dpi / 25.4;
    }
}
