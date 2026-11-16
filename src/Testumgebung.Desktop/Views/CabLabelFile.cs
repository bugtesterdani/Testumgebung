using System.IO;
using System.Windows;
using Microsoft.Win32;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

internal static class CabLabelFile
{
    public static void Import(FrameworkElement source, ParameterRowViewModel row)
    {
        var owner = Window.GetWindow(source);
        var picker = new OpenFileDialog { Filter = "cab-LBL-Druckdateien (*.lbl)|*.lbl", CheckFileExists = true };
        if (picker.ShowDialog(owner) == true && TryEmbed(picker.FileName, row) is { } error)
            MessageBox.Show(owner, error, "cab-LBL-Datei konnte nicht geladen werden", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    internal static string? TryEmbed(string path, ParameterRowViewModel row)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return "Die Druckdatei ist leer.";
            row.Owner.ImportCabPrintData(bytes);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
