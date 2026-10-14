/**
 * @file
 * @brief WPF-Anbindung für Debugging. Ereignisse und Bindings verbinden diese Ansicht mit ihrem Datenkontext.
 */

using System.Windows;
using System.Windows.Controls;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public partial class DebugView : UserControl
{
    public DebugView() => InitializeComponent();

    /// <summary>Wert beim Verlassen des Feldes übernehmen (Typprüfung im ViewModel).</summary>
    private void OnValueLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DebugVariableRowViewModel row) row.Commit();
    }
}
