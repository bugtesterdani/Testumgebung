/**
 * @file
 * @brief Bindet die Dateiauswahl der Oberfläche an native Windows-Dialoge.
 */

using System.Windows;
using Microsoft.Win32;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Desktop;

/// <summary>Standarddialoge von Windows für Projektdateien.</summary>
public sealed class FileDialogService : IFileDialogService
{
    private const string Filter = "Testumgebung-Projekt (*.tproj.json)|*.tproj.json|JSON (*.json)|*.json|Alle Dateien (*.*)|*.*";

    public string? OpenProject()
    {
        var dialog = new OpenFileDialog { Filter = Filter, Title = "Projekt öffnen" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveProject(string suggestedName)
    {
        var dialog = new SaveFileDialog { Filter = Filter, FileName = suggestedName, Title = "Projekt speichern" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, Title = title };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string filter, string suggestedName)
    {
        var dialog = new SaveFileDialog { Filter = filter, Title = title, FileName = suggestedName };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void OpenExternal(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowError($"'{path}' konnte nicht geöffnet werden: {ex.Message}");
        }
    }

    public void ShowError(string message)
        => MessageBox.Show(message, "Testumgebung", MessageBoxButton.OK, MessageBoxImage.Warning);
}
