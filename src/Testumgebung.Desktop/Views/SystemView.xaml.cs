/**
 * @file
 * @brief WPF-Anbindung für Systemprüfung. Ereignisse und Bindings verbinden diese Ansicht mit ihrem Datenkontext.
 */

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Testumgebung.Presentation.Pilot;

namespace Testumgebung.Desktop.Views;

public partial class SystemView : UserControl
{
    public SystemView() => InitializeComponent();

    private void OnOpenConfigurationFolder(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: BackupSource source }) return;
        try
        {
            OpenFolder(source.Path);
        }
        catch (IOException error) { ShowFolderError(error); }
        catch (System.ComponentModel.Win32Exception error) { ShowFolderError(error); }
    }

    private static void OpenFolder(string file)
    {
        var folder = Path.GetDirectoryName(file);
        if (!Directory.Exists(folder))
            throw new IOException("Der Konfigurationsordner wurde noch nicht angelegt.");
        Process.Start(new ProcessStartInfo(folder!) { UseShellExecute = true });
    }

    private static void ShowFolderError(Exception error)
    {
        MessageBox.Show($"Der Ordner konnte nicht geöffnet werden. {error.Message}", "Konfiguration", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
