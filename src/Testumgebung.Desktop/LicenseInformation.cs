using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Testumgebung.Desktop;

internal static class LicenseInformation
{
    internal static void Show(Window owner)
    {
        var tabs = new TabControl();
        foreach (var (title, path) in new[]
        {
            ("Eigener Code (MIT)", "LICENSE"),
            ("Drittanbieter", "THIRD-PARTY-NOTICES.md"),
            ("Laufzeitbedingungen", "THIRD-PARTY-TERMS.md"),
            ("Weitergabe & Quellen", "docs/oeffentliche-veroeffentlichung.md"),
            (".NET-Vertrag", "licenses/third-party/microsoft/dotnet-library.txt"),
            ("SDK-Vertrag", "licenses/third-party/microsoft/windows-sdk.txt"),
            ("Datenschutz", "docs/datenschutz.md")
        })
        {
            var file = Path.Combine(AppContext.BaseDirectory, path);
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { text = "Unterlage nicht verfügbar: " + file + "\n" + ex.Message; }
            tabs.Items.Add(new TabItem { Header = title, Content = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12)
            }});
        }
        var panel = new DockPanel { Margin = new Thickness(12) };
        var button = new Button { Content = "Originaltexte und Quellenordner öffnen", Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 0) };
        button.Click += (_, _) => new FileDialogService().OpenExternal(AppContext.BaseDirectory);
        DockPanel.SetDock(button, Dock.Bottom);
        panel.Children.Add(button);
        panel.Children.Add(tabs);
        new Window { Owner = owner, Title = "Lizenzen und zugehörige Quellen", Width = 900, Height = 650,
            MinWidth = 550, MinHeight = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel }.ShowDialog();
    }
}
