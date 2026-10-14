/**
 * @file
 * @brief Erzeugt die benötigten PDF-Strukturen und Zeichenoperationen ohne externe PDF-Laufzeit.
 */

using System.Globalization;
using System.Text;

namespace Testumgebung.Persistence.Reports;

/// <summary>RGB-Farbe 0…1.</summary>
public readonly record struct PdfColor(double R, double G, double B)
{
    public static readonly PdfColor Black = new(0, 0, 0);
    public static readonly PdfColor Gray = new(0.42, 0.45, 0.5);
    public static readonly PdfColor Light = new(0.93, 0.95, 0.97);
    public static readonly PdfColor Line = new(0.8, 0.83, 0.87);
    public static readonly PdfColor Blue = new(0.1, 0.36, 0.69);
    public static readonly PdfColor Pass = new(0.13, 0.55, 0.25);
    public static readonly PdfColor Fail = new(0.8, 0.15, 0.15);
    public static readonly PdfColor Warn = new(0.85, 0.5, 0.05);

    internal string Fill => string.Create(CultureInfo.InvariantCulture, $"{R:0.###} {G:0.###} {B:0.###} rg");
    internal string Stroke => string.Create(CultureInfo.InvariantCulture, $"{R:0.###} {G:0.###} {B:0.###} RG");
}

public enum PdfFont
{
    Regular,
    Bold,
    Mono,
}

/// <summary>Eine Seite (Koordinaten in Punkt, Ursprung unten links).</summary>
public sealed class PdfPage
{
    internal readonly StringBuilder Content = new();

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public void Text(double x, double y, string text, double size = 10, PdfFont font = PdfFont.Regular, PdfColor? color = null)
    {
        var f = font switch { PdfFont.Bold => "F2", PdfFont.Mono => "F3", _ => "F1" };
        Content.Append((color ?? PdfColor.Black).Fill).Append(" BT /").Append(f).Append(' ').Append(N(size)).Append(" Tf ")
            .Append(N(x)).Append(' ').Append(N(y)).Append(" Td (").Append(PdfText.Escape(text)).Append(") Tj ET\n");
    }

    public void Line(double x1, double y1, double x2, double y2, double width = 0.5, PdfColor? color = null)
        => Content.Append((color ?? PdfColor.Line).Stroke).Append(' ').Append(N(width)).Append(" w ")
            .Append(N(x1)).Append(' ').Append(N(y1)).Append(" m ").Append(N(x2)).Append(' ').Append(N(y2)).Append(" l S\n");

    public void Rect(double x, double y, double w, double h, PdfColor fill)
        => Content.Append(fill.Fill).Append(' ').Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(w)).Append(' ').Append(N(h)).Append(" re f\n");

    public void Polyline(IReadOnlyList<(double X, double Y)> points, double width = 0.8, PdfColor? color = null)
    {
        if (points.Count < 2) return;
        Content.Append((color ?? PdfColor.Blue).Stroke).Append(' ').Append(N(width)).Append(" w ");
        Content.Append(N(points[0].X)).Append(' ').Append(N(points[0].Y)).Append(" m ");
        for (var i = 1; i < points.Count; i++) Content.Append(N(points[i].X)).Append(' ').Append(N(points[i].Y)).Append(" l ");
        Content.Append("S\n");
    }
}

/// <summary>Textkodierung (WinAnsi) und Breiten der Standardschriften Helvetica/Courier.</summary>
public static class PdfText
{
    // Helvetica-Breiten (1/1000 em) für ASCII 32…126 laut AFM.
    private static readonly short[] Helvetica =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556,
        556, 556, 278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556,
        556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly Dictionary<char, byte> WinAnsi = new()
    {
        ['€'] = 0x80,
        ['‚'] = 0x82,
        ['„'] = 0x84,
        ['…'] = 0x85,
        ['‘'] = 0x91,
        ['’'] = 0x92,
        ['“'] = 0x93,
        ['”'] = 0x94,
        ['•'] = 0x95,
        ['–'] = 0x96,
        ['—'] = 0x97,
        ['™'] = 0x99,
    };

    private static readonly IReadOnlyDictionary<char, char> BasicGlyph = new Dictionary<char, char>
    {
        ['ä'] = 'a',
        ['á'] = 'a',
        ['à'] = 'a',
        ['â'] = 'a',
        ['ö'] = 'o',
        ['ó'] = 'o',
        ['ò'] = 'o',
        ['ô'] = 'o',
        ['ü'] = 'u',
        ['ú'] = 'u',
        ['ù'] = 'u',
        ['û'] = 'u',
        ['Ä'] = 'A',
        ['Ö'] = 'O',
        ['Ü'] = 'U',
        ['ß'] = 'B',
        ['é'] = 'e',
        ['è'] = 'e',
        ['ê'] = 'e',
        ['–'] = '-',
        ['„'] = '"',
        ['“'] = '"',
        ['”'] = '"',
    };

    public static double Width(string text, double size, PdfFont font = PdfFont.Regular)
    {
        if (font == PdfFont.Mono) return text.Length * 0.6 * size;
        double w = 0;
        foreach (var c in Normalize(text))
        {
            var basic = BasicGlyph.GetValueOrDefault(c, c);
            w += basic is >= ' ' and <= '~' ? Helvetica[basic - ' '] : 556;
        }
        return w / 1000 * size * (font == PdfFont.Bold ? 1.06 : 1);
    }

    /// <summary>Zeichen außerhalb von WinAnsi ersetzen (±, µ, °, Umlaute bleiben erhalten).</summary>
    private static readonly IReadOnlyDictionary<char, string> FallbackGlyphs = new Dictionary<char, string>
    {
        ['→'] = "->",
        ['←'] = "<-",
        ['≤'] = "<=",
        ['≥'] = ">=",
        ['×'] = "x",
        ['σ'] = "s",
        ['μ'] = "\u00B5",
        ['−'] = "-",
        ['Ω'] = "Ohm",
        ['…'] = "...",
    };

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\r' or '\n' or '\t') sb.Append(' ');
            else if (c <= 0xFF && c is not (>= (char)0x80 and <= (char)0x9F)) sb.Append(c);
            else if (WinAnsi.ContainsKey(c)) sb.Append(c);
            else sb.Append(FallbackGlyphs.GetValueOrDefault(c, "?"));
        }
        return sb.ToString();
    }

    internal static string Escape(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in Normalize(text))
        {
            var b = WinAnsi.TryGetValue(c, out var mapped) ? mapped : (byte)c;
            switch (b)
            {
                case (byte)'(': sb.Append("\\("); break;
                case (byte)')': sb.Append("\\)"); break;
                case (byte)'\\': sb.Append("\\\\"); break;
                case < 0x20 or >= 0x7F: sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0')); break;
                default: sb.Append((char)b); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Text auf Breite kürzen (mit „…“).</summary>
    public static string Fit(string text, double width, double size, PdfFont font = PdfFont.Regular)
    {
        if (Width(text, size, font) <= width) return text;
        var t = text;
        while (t.Length > 1 && Width(t + "…", size, font) > width) t = t[..^1];
        return t + "…";
    }

    /// <summary>Zeilenumbruch an Wortgrenzen.</summary>
    public static IReadOnlyList<string> Wrap(string text, double width, double size, PdfFont font = PdfFont.Regular)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (Width(candidate, size, font) <= width || line.Length == 0)
                {
                    line = Width(candidate, size, font) <= width ? candidate : Fit(candidate, width, size, font);
                }
                else
                {
                    lines.Add(line);
                    line = Width(word, size, font) <= width ? word : Fit(word, width, size, font);
                }
            }
            lines.Add(line);
        }
        return lines;
    }
}

/// <summary>
/// Minimaler PDF-1.4-Schreiber (Standardschriften Helvetica/Courier, WinAnsi, Linien, Flächen) – ohne externe Bibliothek,
/// für Prüfberichte und Auswertungen. A4 hoch.
/// </summary>
public sealed class PdfDocument
{
    public const double Width = 595.28;
    public const double Height = 841.89;

    private readonly List<PdfPage> _pages = [];

    public string Title { get; set; } = "";
    public string Author { get; set; } = "";

    public IReadOnlyList<PdfPage> Pages => _pages;

    public PdfPage NewPage()
    {
        var page = new PdfPage();
        _pages.Add(page);
        return page;
    }

    public byte[] ToBytes()
    {
        if (_pages.Count == 0) NewPage();
        var latin = Encoding.Latin1;
        using var ms = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) => ms.Write(latin.GetBytes(s));
        void Obj(string body)
        {
            offsets.Add(ms.Position);
            Write($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n%âãÏÓ\n");
        var pageCount = _pages.Count;
        // 1 Katalog, 2 Seitenbaum, 3–5 Schriften, 6 Info, danach je Seite: Seite + Inhalt
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{7 + 2 * i} 0 R"));
        Obj($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>");
        Obj($"<< /Title ({PdfText.Escape(Title)}) /Author ({PdfText.Escape(Author)}) /Producer (Testumgebung) /CreationDate (D:{DateTime.Now:yyyyMMddHHmmss}) >>");
        foreach (var page in _pages)
        {
            var index = offsets.Count + 1;
            Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Width.ToString(CultureInfo.InvariantCulture)} {Height.ToString(CultureInfo.InvariantCulture)}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >> >> /Contents {index + 1} 0 R >>");
            var content = latin.GetBytes(page.Content.ToString());
            offsets.Add(ms.Position);
            Write($"{offsets.Count} 0 obj\n<< /Length {content.Length} >>\nstream\n");
            ms.Write(content);
            Write("\nendstream\nendobj\n");
        }
        var xref = ms.Position;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(sb.ToString());
        return ms.ToArray();
    }

    /// <summary>Atomar speichern.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ToBytes());
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Fließlayout mit Seitenumbruch, Kopf-/Fußzeile, Überschriften, Absätzen, Tabellen und Kurvenbild.</summary>
public sealed class PdfReportWriter
{
    public const double Margin = 42;
    private readonly PdfDocument _doc = new();
    private readonly string _header;
    private PdfPage _page = null!;
    private double _y;

    public PdfReportWriter(string title, string header)
    {
        _doc.Title = title;
        _header = header;
        NewPage();
    }

    public double ContentWidth => PdfDocument.Width - 2 * Margin;

    private void NewPage()
    {
        _page = _doc.NewPage();
        _page.Text(Margin, PdfDocument.Height - 28, _header, 8, PdfFont.Regular, PdfColor.Gray);
        _page.Line(Margin, PdfDocument.Height - 32, PdfDocument.Width - Margin, PdfDocument.Height - 32);
        _y = PdfDocument.Height - 52;
    }

    /// <summary>Platz sicherstellen, sonst neue Seite.</summary>
    public void Need(double height)
    {
        if (_y - height < 50) NewPage();
    }

    public void Space(double h) => _y -= h;

    public void Title(string text, string? subtitle = null)
    {
        Need(50);
        _page.Text(Margin, _y - 18, text, 18, PdfFont.Bold);
        _y -= 26;
        if (subtitle is not null)
        {
            _page.Text(Margin, _y - 10, subtitle, 10, PdfFont.Regular, PdfColor.Gray);
            _y -= 16;
        }
        _y -= 6;
    }

    public void Heading(string text)
    {
        Need(40);
        _y -= 8;
        _page.Text(Margin, _y - 12, text, 12, PdfFont.Bold, PdfColor.Blue);
        _y -= 18;
    }

    public void Paragraph(string text, double size = 9, PdfColor? color = null, PdfFont font = PdfFont.Regular)
    {
        foreach (var line in PdfText.Wrap(text, ContentWidth, size, font))
        {
            Need(size + 4);
            _page.Text(Margin, _y - size, line, size, font, color);
            _y -= size + 3;
        }
        _y -= 3;
    }

    /// <summary>Zweispaltige Angaben (Bezeichnung: Wert), optional farbig.</summary>
    public void KeyValues(IEnumerable<(string Key, string Value, PdfColor? Color)> items, double keyWidth = 130)
    {
        foreach (var (key, value, color) in items)
        {
            var lines = PdfText.Wrap(value, ContentWidth - keyWidth, 9.5, color is null ? PdfFont.Regular : PdfFont.Bold);
            Need(lines.Count * 13 + 2);
            _page.Text(Margin, _y - 10, key, 9.5, PdfFont.Regular, PdfColor.Gray);
            foreach (var line in lines)
            {
                _page.Text(Margin + keyWidth, _y - 10, line, 9.5, color is null ? PdfFont.Regular : PdfFont.Bold, color);
                _y -= 13;
            }
        }
        _y -= 4;
    }

    /// <summary>Tabelle mit Kopfzeile (wird auf Folgeseiten wiederholt), Spaltenbreiten relativ, Text wird gekürzt.</summary>
    public void Table(IReadOnlyList<(string Header, double Weight)> columns, IEnumerable<IReadOnlyList<(string Text, PdfColor? Color)>> rows, double size = 8.5)
    {
        var total = columns.Sum(c => c.Weight);
        var widths = columns.Select(c => c.Weight / total * ContentWidth).ToArray();
        var rowHeight = size + 6;
        void HeaderRow()
        {
            Need(rowHeight * 2);
            _page.Rect(Margin, _y - rowHeight, ContentWidth, rowHeight, PdfColor.Light);
            var x = Margin;
            for (var i = 0; i < columns.Count; i++)
            {
                _page.Text(x + 3, _y - size - 2, PdfText.Fit(columns[i].Header, widths[i] - 6, size, PdfFont.Bold), size, PdfFont.Bold);
                x += widths[i];
            }
            _y -= rowHeight;
        }
        HeaderRow();
        foreach (var row in rows)
        {
            if (_y - rowHeight < 50)
            {
                NewPage();
                HeaderRow();
            }
            var x = Margin;
            for (var i = 0; i < columns.Count && i < row.Count; i++)
            {
                var (text, color) = row[i];
                _page.Text(x + 3, _y - size - 2, PdfText.Fit(text, widths[i] - 6, size, color is null ? PdfFont.Regular : PdfFont.Bold), size,
                    color is null ? PdfFont.Regular : PdfFont.Bold, color);
                x += widths[i];
            }
            _y -= rowHeight;
            _page.Line(Margin, _y, Margin + ContentWidth, _y, 0.3);
        }
        _y -= 6;
    }

    /// <summary>Kurvenbild: Kanäle mit gemeinsamer Zeitachse, auf höchstens 600 Punkte je Kanal verdichtet.</summary>
    public void Plot(string title, IReadOnlyList<(string Name, double[] X, double[] Y)> series, string xUnit, string yUnit, double height = 150)
    {
        Need(height + 30);
        _page.Text(Margin, _y - 10, title, 9.5, PdfFont.Bold);
        _y -= 16;
        var (left, bottom, w, h) = (Margin + 40, _y - height, ContentWidth - 40, height - 14);
        var all = series.Where(s => s.X.Length > 0).ToList();
        if (all.Count == 0) return;
        var (xmin, xmax) = (all.Min(s => s.X.Min()), all.Max(s => s.X.Max()));
        var (ymin, ymax) = (all.Min(s => s.Y.Min()), all.Max(s => s.Y.Max()));
        if (xmax <= xmin) xmax = xmin + 1;
        if (ymax <= ymin) { ymax += 1; ymin -= 1; }
        var pad = (ymax - ymin) * 0.05;
        (ymin, ymax) = (ymin - pad, ymax + pad);
        _page.Rect(left, bottom, w, h, PdfColor.Light);
        for (var i = 0; i <= 4; i++)
        {
            var yv = ymin + (ymax - ymin) * i / 4;
            var y = bottom + h * i / 4;
            _page.Line(left, y, left + w, y, 0.3);
            _page.Text(Margin, y - 3, yv.ToString("G4", CultureInfo.InvariantCulture), 7, PdfFont.Regular, PdfColor.Gray);
            var xv = xmin + (xmax - xmin) * i / 4;
            _page.Text(left + w * i / 4 - 8, bottom - 10, xv.ToString("G4", CultureInfo.InvariantCulture), 7, PdfFont.Regular, PdfColor.Gray);
        }
        _page.Text(left + w - 30, bottom - 20, xUnit, 7, PdfFont.Regular, PdfColor.Gray);
        _page.Text(Margin, bottom + h + 3, yUnit, 7, PdfFont.Regular, PdfColor.Gray);
        PdfColor[] colors = [new(0.75, 0.45, 0.1), new(0.15, 0.15, 0.15), new(0.45, 0.45, 0.48), PdfColor.Blue, PdfColor.Pass];
        var legendX = left;
        for (var s = 0; s < all.Count; s++)
        {
            var (name, xs, ys) = all[s];
            var step = Math.Max(1, xs.Length / 600);
            var points = new List<(double, double)>();
            for (var i = 0; i < xs.Length; i += step)
                points.Add((left + (xs[i] - xmin) / (xmax - xmin) * w, bottom + (ys[i] - ymin) / (ymax - ymin) * h));
            var color = colors[s % colors.Length];
            _page.Polyline(points, 0.7, color);
            _page.Rect(legendX + 40, bottom - 27, 8, 3, color);
            _page.Text(legendX + 51, bottom - 29, name, 7, PdfFont.Regular, PdfColor.Gray);
            legendX += 70;
        }
        _y = bottom - 36;
    }

    /// <summary>Fußzeilen „Seite n von m“ setzen und Dokument liefern.</summary>
    public PdfDocument Finish(string footer)
    {
        var pages = _doc.Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            pages[i].Line(Margin, 36, PdfDocument.Width - Margin, 36);
            pages[i].Text(Margin, 24, footer, 7.5, PdfFont.Regular, PdfColor.Gray);
            var text = $"Seite {i + 1} von {pages.Count}";
            pages[i].Text(PdfDocument.Width - Margin - PdfText.Width(text, 7.5), 24, text, 7.5, PdfFont.Regular, PdfColor.Gray);
        }
        return _doc;
    }
}
