using System.Globalization;
using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Persisted source and canvas bindings. Unsupported commands remain in Data.</summary>
public sealed class CabImportedSource
{
    public byte[] Data { get; set; } = [];
    public string SetupCodePage { get; set; } = "UTF-8";
    public int Dpi { get; set; }
    public int WidthMm { get; set; }
    public int HeightMm { get; set; }
    public decimal GapMm { get; set; }
    public int? SizeOffset { get; set; }
    public int InsertOffset { get; set; }
    public List<CabElementSource> Elements { get; set; } = [];
}

public sealed class CabElementSource
{
    public int Offset { get; set; }
    public LabelElement Original { get; set; } = new();
}

internal static class CabImportedWriter
{
    internal static byte[] Render(LabelLayout layout, out int textOffset)
    {
        var source = layout.CabSource!;
        if (layout.Dpi != source.Dpi)
            throw new InvalidDataException("Für eine geänderte Druckauflösung die Originaldatei erneut mit dieser Auflösung öffnen.");
        var document = CabParser.Parse(source.Data, new(source.SetupCodePage));
        var nodes = CabLabelImport.Commands(document.Root).ToDictionary(n => n.Range.Offset);
        var changes = new List<CabTextChange>();
        using var downloads = new MemoryStream();
        ChangeDimensions(layout, source, document, nodes, changes);
        foreach (var binding in source.Elements)
        {
            var node = nodes[binding.Offset];
            var item = layout.Elements.SingleOrDefault(e => e.CabSourceOffset == binding.Offset);
            if (item is null) { changes.Add(new(node.Range, [])); continue; }
            ChangeElement(layout, item, binding.Original, node, document, changes, downloads);
        }
        var added = new StringBuilder();
        foreach (var item in layout.Elements.Where(e => e.CabSourceOffset is null))
            added.Append(CabLabelLayout.Field(layout, item, downloads)).Append(document.LineEnding);
        if (added.Length > 0)
        {
            if (!nodes.TryGetValue(source.InsertOffset, out var insertBefore) || insertBefore.Command != "A"
                || insertBefore.Unit != CabUnit.Millimetres)
                throw new InvalidDataException("Neue grafische Elemente benötigen einen abgeschlossenen Auftrag in Millimetern.");
            var prefix = source.InsertOffset > 0 && source.Data[source.InsertOffset - 1] is not (10 or 13) ? document.LineEnding : "";
            var page = insertBefore.CodePage;
            var insertion = new CabSyntaxNode(CabNodeKind.Parameter, new(source.InsertOffset, 0), "", page, CabUnit.Millimetres, []);
            changes.Add(document.Replace(insertion, prefix + added));
        }
        if (downloads.Length > 0) changes.Add(new(new(0, 0), DownloadEndings(downloads.ToArray(), document.LineEnding)));
        var output = document.Write(changes);
        textOffset = TextOffset(output, source.SetupCodePage);
        return output;
    }

    private static void ChangeDimensions(LabelLayout layout, CabImportedSource source, CabDocument document,
        Dictionary<int, CabSyntaxNode> nodes, List<CabTextChange> changes)
    {
        if (layout.WidthMm == source.WidthMm && layout.HeightMm == source.HeightMm && layout.GapMm == source.GapMm) return;
        if (source.SizeOffset is not int offset) throw new InvalidDataException("Etikettenmaße sind im Quelltext zu bearbeiten.");
        var node = nodes[offset];
        if ((layout.GapMm == 0) != (source.GapMm == 0))
            changes.Add(document.Replace(node.Children[0], layout.GapMm == 0 ? "e" : "l1"));
        if (layout.HeightMm != source.HeightMm) changes.Add(document.Replace(node.Children[3], Invariant(layout.HeightMm)));
        if (layout.HeightMm != source.HeightMm || layout.GapMm != source.GapMm)
            changes.Add(document.Replace(node.Children[4], Invariant(layout.HeightMm + layout.GapMm)));
        if (layout.WidthMm != source.WidthMm) changes.Add(document.Replace(node.Children[5], Invariant(layout.WidthMm)));
    }

    private static void ChangeElement(LabelLayout layout, LabelElement item, LabelElement original,
        CabSyntaxNode node, CabDocument document, List<CabTextChange> changes, Stream downloads)
    {
        var imageChanged = !((item.ImageWidth, item.ImageHeight).Equals((original.ImageWidth, original.ImageHeight))
            && item.Bitmap.AsSpan().SequenceEqual(original.Bitmap));
        var unchanged = (item.Kind, item.X, item.Y, item.Size, item.Length, item.Value)
            .Equals((original.Kind, original.X, original.Y, original.Size, original.Length, original.Value));
        if (unchanged && !imageChanged) return;
        using var unusedDownload = new MemoryStream();
        var rendered = CabLabelLayout.Field(layout, item,
            item.Kind == LabelElementKind.Image && !imageChanged && original.Kind == item.Kind ? unusedDownload : downloads);
        var canonical = CabParser.Parse(Encoding.UTF8.GetBytes(rendered));
        var replacement = canonical.Root.Children[0];
        if (item.Kind != original.Kind)
        { changes.Add(document.Replace(node, rendered)); return; }
        foreach (var index in ChangedParameterIndexes(item, original, node.Children.Count, imageChanged))
            changes.Add(document.Replace(node.Children[index], canonical.Decode(replacement.Children[index])!));
    }

    private static IEnumerable<int> ChangedParameterIndexes(LabelElement item, LabelElement original, int count, bool imageChanged)
    {
        var indexes = new HashSet<int>();
        if (item.X != original.X) indexes.Add(0);
        if (item.Y != original.Y) indexes.Add(1);
        if (item.Size != original.Size && item.Kind != LabelElementKind.Image) indexes.Add(4);
        if (item.Length != original.Length && item.Kind == LabelElementKind.Line) indexes.Add(3);
        if (item.Value != original.Value && item.Kind is not (LabelElementKind.Line or LabelElementKind.Image)) indexes.Add(count - 1);
        if (item.Kind == LabelElementKind.Image && imageChanged) indexes.Add(count - 1);
        return indexes;
    }

    // The legacy print substitution accepts a single UTF-8 tail. Do not expose opaque
    // or non-UTF-8 regions to it. Broader substitution is part of AP 7.
    private static int TextOffset(byte[] bytes, string setupCodePage)
    {
        var document = CabParser.Parse(bytes, new(setupCodePage));
        var nodes = CabLabelImport.Commands(document.Root).ToArray();
        var first = nodes.FirstOrDefault(n => n.Kind == CabNodeKind.LabelCommand && n.Command == "J");
        if (first is null || !bytes.AsSpan(first.Range.Offset).StartsWith("J\r"u8)
            || bytes.AsSpan(first.Range.Offset).Contains((byte)'\n') || bytes.AsSpan(first.Range.Offset).Contains((byte)'[')
            || nodes.Any(n => n.Range.Offset >= first.Range.Offset
            && (n.Kind is CabNodeKind.BinaryBlock or CabNodeKind.Block || n.CodePage != "UTF-8" || document.Decode(n) is null)))
            return bytes.Length;
        return first.Range.Offset;
    }

    private static string Invariant(decimal number) => number.ToString(CultureInfo.InvariantCulture);

    private static byte[] DownloadEndings(byte[] bytes, string ending)
    {
        var document = CabParser.Parse(bytes);
        return document.Write(document.Root.Children.Where(n => n.Kind == CabNodeKind.RawLine
            && document.Decode(n) is "\r" or "\n" or "\r\n").Select(n => document.Replace(n, ending)));
    }
}
