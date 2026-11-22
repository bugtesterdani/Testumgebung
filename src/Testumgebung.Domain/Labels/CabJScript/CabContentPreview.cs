using System.Globalization;
using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

public enum CabPreviewMonthPolicy { ThirtyDays, CalendarMonths }
public enum CabPreviewVisibilityPolicy { ExampleHidesWhenTrue, TablePrintsWhenTrue }
public sealed class CabPreviewSettings
{
    public DateTime ExampleTime { get; set; } = new(2026, 1, 15, 13, 45, 30);
    public string Culture { get; set; } = "de-DE";
    public string InterfaceCodePage { get; set; } = "UTF-8";
    public CabPreviewMonthPolicy MonthPolicy { get; set; } = CabPreviewMonthPolicy.ThirtyDays;
    public CalendarWeekRule WeekRule { get; set; } = CalendarWeekRule.FirstFourDayWeek;
    public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Monday;
    public CabPreviewVisibilityPolicy VisibilityPolicy { get; set; } = CabPreviewVisibilityPolicy.ExampleHidesWhenTrue;
    public Dictionary<string, string> SelectionExamples { get; set; } = new(StringComparer.Ordinal);
}

public sealed record CabPreviewValue(string Text, byte[]? Bytes = null, bool Placeholder = false);
public sealed record CabContentPreviewResult(string Text, bool Hidden, string? Alignment, decimal? AlignmentLength,
    IReadOnlyList<CabContentIssue> Issues, CabContentSyntax Syntax, CabPreviewValue Value);

/// <summary>Pure offline evaluation. No printer, file, database or network operations.</summary>
public static class CabContentPreview
{
    public static CabContentPreviewResult Evaluate(string text, CabPreviewSettings settings,
        IReadOnlyDictionary<string, CabPreviewValue>? previousFields = null, bool barcode = false)
        => EvaluateCore(text, settings, previousFields, barcode, null);

    internal static CabContentPreviewResult EvaluateSource(CabContentSource source, CabPreviewSettings settings,
        IReadOnlyDictionary<string, CabPreviewValue> previousFields, bool barcode)
        => EvaluateCore(source.Text, settings, previousFields, barcode, source);

    private static CabContentPreviewResult EvaluateCore(string text, CabPreviewSettings settings,
        IReadOnlyDictionary<string, CabPreviewValue>? previousFields, bool barcode, CabContentSource? source)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var context = new CabPreviewContext(settings, previousFields ?? new Dictionary<string, CabPreviewValue>(), barcode, source);
        var syntax = CabContentParser.Parse(text, context.Names);
        context.Issues.AddRange(syntax.Issues);
        context.Configure(syntax.Fields);
        var output = new StringBuilder();
        var chunks = new List<(CabPreviewValue Value, string CodePage)>();
        var hasBinary = false;
        var placeholder = false;
        var offset = 0;
        foreach (var field in syntax.Fields)
        {
            if (field.Offset < offset) continue;
            var literal = text[offset..field.Offset];
            output.Append(literal);
            chunks.Add((Literal(literal, offset, source), settings.InterfaceCodePage));
            var value = context.Evaluate(field);
            output.Append(value.Text);
            chunks.Add((value, PageAt(source, field.Offset, settings.InterfaceCodePage)));
            hasBinary |= value.Bytes is not null;
            placeholder |= value.Placeholder;
            offset = field.Offset + field.Length;
            if (output.Length > 65536)
            { context.Issues.Add(new("preview-budget", "Vorschaugröße überschreitet die lokale Darstellungsgrenze.", field.Offset, field.Length, field.Definition.Id)); break; }
        }
        var tail = text[offset..];
        output.Append(tail);
        chunks.Add((Literal(tail, offset, source), settings.InterfaceCodePage));
        var rendered = context.Digits(output.ToString());
        return new(rendered, context.Hidden, context.Alignment, context.AlignmentLength, context.Issues.AsReadOnly(), syntax,
            new(output.ToString(), hasBinary ? EncodeChunks(chunks, context) : null, placeholder));
    }

    private static CabPreviewValue Literal(string text, int offset, CabContentSource? source) => new(text, source?.Bytes(offset, text.Length));
    private static string PageAt(CabContentSource? source, int offset, string fallback) => source?.CodePageAt(offset) ?? fallback;

    private static byte[]? EncodeChunks(List<(CabPreviewValue Value, string CodePage)> chunks, CabPreviewContext context)
    {
        try
        {
            using var bytes = new MemoryStream();
            foreach (var chunk in chunks) bytes.Write(chunk.Value.Bytes ?? CabEncoding.Get(chunk.CodePage).GetBytes(chunk.Value.Text));
            return bytes.ToArray();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            context.Issues.Add(new("preview-encoding", "Gemischter Vorschauinhalt ist nicht in der gewählten Codepage darstellbar: " + error.Message, 0, 0, null));
            return null;
        }
    }
}

internal sealed partial class CabPreviewContext(CabPreviewSettings settings,
    IReadOnlyDictionary<string, CabPreviewValue> fields, bool barcode, CabContentSource? source)
{
    private static readonly HashSet<int> BinaryFunctions = [68, 69, 70, 71, 72, 73, 74, 75, 76, 80, 98, 103];
    private static readonly HashSet<int> Modifiers = [77, 78, 81, 82, 95];
    private static readonly HashSet<int> TextFunctions = [85, 86, 87, 88, 89, 93, 96, 97, 99, 102, 104];
    internal readonly List<CabContentIssue> Issues = [];
    internal IReadOnlySet<string> Names { get; } = fields.Keys.ToHashSet(StringComparer.Ordinal);
    internal bool Hidden { get; private set; }
    internal string? Alignment { get; private set; }
    internal decimal? AlignmentLength { get; private set; }
    private int _digits = 2;
    private int _width;
    private string _fill = "0";
    private int _base = 10;
    private string _rounding = "n";
    private string _style = "LATIN";
    private readonly HashSet<int> _invalidModifiers = [];
    private string _currentCodePage = settings.InterfaceCodePage;
    internal CultureInfo Culture { get; } = CultureInfo.GetCultureInfo(settings.Culture);

    internal CabPreviewValue Resolve(string text) => fields.TryGetValue(text, out var value) ? value : new(text);
    internal decimal Number(string text)
    {
        text = text.Trim();
        var value = Resolve(text);
        if (value.Placeholder) throw new InvalidOperationException("Abhängiger Druckerwert ist nur ein Platzhalter.");
        var culture = fields.ContainsKey(text) ? Culture : CultureInfo.InvariantCulture;
        return decimal.Parse(value.Text.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, culture);
    }

    private int Integer(string text)
    {
        var value = Number(text);
        if (value != decimal.Truncate(value)) throw new FormatException("Ganzzahliger Wert erforderlich; keine stille Abschneidung.");
        return checked((int)value);
    }

    internal void Note(CabContentField field, string message, string code = "preview-assumption")
        => Issues.Add(new(code, message, field.Offset, field.Length, field.Definition.Id));

    internal CabPreviewValue Evaluate(CabContentField field)
    {
        var errors = CabContentGrammar.Errors(field, Names, barcode);
        if (field.Nested || errors.Count > 0 || _invalidModifiers.Contains(field.Offset))
        {
            foreach (var error in errors) Note(field, error, "content-parameter");
            return new("⟦ungültig: " + field.Name + "⟧", Placeholder: true);
        }
        try
        {
            _currentCodePage = source?.CodePageAt(field.Offset) ?? settings.InterfaceCodePage;
            return EvaluateCore(field);
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException or InvalidOperationException or DivideByZeroException)
        {
            Note(field, error.Message, "preview-value");
            return new("⟦nicht berechenbar: " + field.Name + "⟧", Placeholder: true);
        }
    }

    private CabPreviewValue EvaluateCore(CabContentField field)
    {
        var id = CabContentGrammar.Number(field);
        var args = CabContentGrammar.Arguments(field);
        if (id <= 38) return new(Date(field, id, args));
        if (id <= 54) return new(Math(field, id, args));
        if (BinaryFunctions.Contains(id)) return Binary(field, id, args);
        if (Modifiers.Contains(id)) return new("");
        if (TextFunctions.Contains(id)) return Text(field, id, args);
        Note(field, "Druckerabhängiger Wert; keine Hardwareoperation ausgeführt.", "preview-hardware");
        return new("⟦Drucker: " + field.Name + "⟧", Placeholder: true);
    }
}
