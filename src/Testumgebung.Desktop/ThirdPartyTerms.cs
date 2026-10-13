using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace Testumgebung.Desktop;

internal static class ThirdPartyTerms
{
    internal static bool Accept()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-TERMS.md");
        var bundledRuntime = File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"))
            || File.Exists(Path.Combine(AppContext.BaseDirectory, "workers", "python", "runtime", "python.exe"));
        if (!File.Exists(path)) return !bundledRuntime;
        byte[] bytes;
        try { bytes = System.Text.Encoding.UTF8.GetBytes(ReadConsentText(AppContext.BaseDirectory)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Laufzeitbedingungen fehlen oder sind nicht lesbar.\n" + ex.Message, "Laufzeitbedingungen");
            return false;
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "licenses");
        var receipt = Path.Combine(folder, "microsoft-runtime-terms.sha256");
        try { if (IsCurrentReceipt(receipt, hash)) return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Ask again if the previous receipt cannot be read. */ }
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var window = new Window { Title = "Microsoft-Laufzeitbedingungen (Python, .NET und WPF)", Width = 780, Height = 600,
            MinWidth = 500, MinHeight = 350, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var panel = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var accept = new Button { Content = "Zustimmen und starten", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8) };
        var cancel = new Button { Content = "Beenden", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8), IsCancel = true };
        accept.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(accept);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } });
        window.Content = panel;
        if (window.ShowDialog() != true) return false;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(receipt, hash);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Die Zustimmung konnte nicht gespeichert werden.\n" + ex.Message, "Laufzeitbedingungen");
            return false;
        }
    }
    internal static bool IsCurrentReceipt(string path, string hash)
        => File.Exists(path) && File.ReadAllText(path).Trim() == hash;

    internal static string ReadConsentText(string folder)
    {
        var paths = new[] { "THIRD-PARTY-TERMS.md", "licenses/third-party/microsoft/dotnet-library.txt",
            "licenses/third-party/microsoft/windows-sdk.txt" };
        var texts = paths.Select(p => File.ReadAllText(Path.Combine(folder, p))).ToList();
        var python = Path.Combine(folder, "workers", "python", "runtime", "LICENSE.txt");
        if (File.Exists(Path.Combine(folder, "workers", "python", "runtime", "python.exe")))
            texts.Add(File.ReadAllText(python));
        return string.Join("\n\n--------------------\n\n", texts);
    }

}
