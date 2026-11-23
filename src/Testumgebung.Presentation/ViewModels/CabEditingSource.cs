namespace Testumgebung.Presentation.ViewModels;

/// <summary>Creates a source snapshot without modifying the layout or project used as its input.</summary>
public static class CabEditingSource
{
    public static LabelLayout Ensure(LabelLayout layout) => layout.CabSource is null ? Capture(layout) : layout;

    public static LabelLayout Capture(LabelLayout layout)
    {
        var bytes = CabLabelLayout.Render(layout);
        var imported = CabLabelImport.Read(bytes, layout.Dpi, layout.CabSource?.SetupCodePage ?? "UTF-8");
        imported.CabSettings = layout.CabSettings;
        imported.CabGraphicExampleValues = layout.CabGraphicExampleValues;
        CopyExamples(layout.Elements, imported.Elements);
        return imported;
    }

    private static void CopyExamples(IReadOnlyList<LabelElement> original, IReadOnlyList<LabelElement> imported)
    {
        for (var index = 0; index < Math.Min(original.Count, imported.Count); index++)
        {
            imported[index].ExampleValues = original[index].ExampleValues;
            imported[index].Preview = original[index].Preview;
        }
    }
}
