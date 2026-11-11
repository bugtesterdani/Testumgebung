/**
 * @file
 * @brief WPF-Anbindung für Kurvenbearbeitung. Ereignisse und Bindings verbinden diese Ansicht mit ihrem Datenkontext.
 */

using System.Windows;
using System.Windows.Controls;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public partial class CurvesView : UserControl
{
    public CurvesView() => InitializeComponent();

    private void OpenPointTable(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: CurvesViewModel model } || model.Working is not { } source)
            return;

        var window = new CurvePointTableWindow(source) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true && window.EditedCurve is { } edited)
            model.ApplyPointTable(source, edited);
    }
}
