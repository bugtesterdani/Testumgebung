using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Presentation.ViewModels;

public enum CabGraphicKind { Text, RichText, Barcode, Circle, Line, Rectangle, Image }

public sealed record CabGraphicItem(CabBoundCommand Command, CabGraphicKind Kind, CabGraphicRect ContentBounds,
    CabGraphicRect LocalBounds, CabGraphicTransform Transform, string Text, IReadOnlyList<string> Notes)
{
    public CabGraphicRect Bounds => Transform.Bounds(LocalBounds);
    public CabBarcodeSymbol? Barcode { get; init; }
    public CabGraphicImage? Image { get; init; }
    public CabGraphicImage? FillPattern { get; init; }
    public double FontHeight { get; init; }
    public double HumanReadableHeight { get; init; }
    public double StrokeX { get; init; }
    public double StrokeY { get; init; }
    public int Column { get; init; }
    public bool Hidden { get; init; }
    public override string ToString() => Command.Definition.Catalog.Title + " · " + (Command.Parameter("name")?.Value ?? Command.Syntax.Range.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed record CabGraphicImage(string Name, string Type, byte[] Data, int PixelWidth, int PixelHeight);
public sealed record CabBarcodeSymbol(int Columns, int Rows, bool[]? Modules, string Note)
{
    public bool IsEncoded => Modules is not null;
}
public sealed record CabGraphicJob(int Index, string Name)
{
    public override string ToString() => Name;
}
public sealed record CabGraphicScene(int JobIndex, int Dpi, double Width, double Height, double Gap,
    string Sensor, int Columns, double ColumnPitch, CabGraphicTransform LabelTransform,
    IReadOnlyList<CabGraphicItem> Items, IReadOnlyList<string> Notes);

public static class CabGraphicCatalog
{
    public static IReadOnlyList<CabCatalogEntry> Definitions { get; } = CabCatalog.Default.Entries
        .Where(e => e.Kind is "barcode" or "grafik" || e.Id is "JS-4.7" or "JS-4.9" or "JS-4.16" or "JS-4.17").ToArray();

    public static IReadOnlyList<CabGraphicJob> Jobs(CabSourceEditor editor)
        => editor.Validation.Commands.Where(c => c.JobIndex >= 0).Select(c => c.JobIndex).Distinct()
            .Select(i => new CabGraphicJob(i, "Auftrag " + (i + 1))).ToArray();
}
