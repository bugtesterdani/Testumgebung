using System.IO;
using System.Windows;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public partial class NewProjectWindow : Window
{
    public NewProjectWindow(string folder)
    {
        InitializeComponent();
        Templates.ItemsSource = ProjectTemplates.InFolder(folder);
        Templates.SelectedIndex = 0;
    }

    public NewProjectRequest? Request { get; private set; }

    private void OnCreate(object sender, RoutedEventArgs args)
    {
        var name = ProjectName.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ValidationMessage.Text = "Einen Projektnamen ohne ungültige Dateinamenzeichen eingeben.";
            return;
        }

        Request = new(name, (Templates.SelectedItem as ProjectTemplate)?.Path);
        DialogResult = true;
    }
}
