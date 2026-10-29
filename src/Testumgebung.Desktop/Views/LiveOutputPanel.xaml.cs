/** @file @brief Gemeinsame Live-Ausgabe für Einzel- und Doppelplatzansicht. */
using System.Windows.Controls;
namespace Testumgebung.Desktop.Views;
public partial class LiveOutputPanel : UserControl
{
    public LiveOutputPanel() => InitializeComponent();
    private void OutputChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();
}
