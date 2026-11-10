/**
 * @file
 * @brief WPF-Anbindung für Release-Verwaltung. Ereignisse und Bindings verbinden diese Ansicht mit ihrem Datenkontext.
 */

using System.Windows.Controls;

namespace Testumgebung.Desktop.Views;

public partial class ReleasesView : UserControl
{
    private void OnOpenSources(object sender, System.Windows.RoutedEventArgs eventArgs)
    {
        if (DataContext is not Testumgebung.Presentation.ViewModels.ShellViewModel { ReleasesPage: { } page }) return;
        _ = page.Sources.RefreshAsync();
        var window = new System.Windows.Window
        {
            Title = "Programmquellen & Versionen", Width = 850, Height = 780, MinWidth = 620, MinHeight = 500,
            Owner = System.Windows.Window.GetWindow(this), WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Content = new ProgramSourcesView { DataContext = page.Sources }, Background = System.Windows.Media.Brushes.White
        };
        window.ShowDialog();
    }

    public ReleasesView() => InitializeComponent();
}
