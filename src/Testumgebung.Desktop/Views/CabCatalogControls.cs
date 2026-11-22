using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Desktop.Views;

internal static class CabCatalogControls
{
    // Unavailable definitions remain selectable for reading their documentation.
    internal static void ExplainGeneration(ComboBox catalog, CabPrinterGeneration generation)
    {
        var style = new Style(typeof(ComboBoxItem));
        foreach (var entry in CabCatalog.Default.Entries.Where(e => e.Availability(generation) == CabAvailability.Unsupported))
        {
            var trigger = new DataTrigger { Binding = new Binding(nameof(CabCatalogEntry.Id)), Value = entry.Id };
            trigger.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.GrayTextBrush));
            trigger.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, "Für " + generation + " nicht verfügbar. Definition bleibt lesbar.\n" + CabSourceCatalog.Tooltip(entry)));
            style.Triggers.Add(trigger);
        }
        catalog.ItemContainerStyle = style;
    }
}
