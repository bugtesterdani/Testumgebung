using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Testumgebung.Desktop.Views;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyProjectAssistantAndHelp(MainWindow window, Dispatcher dispatcher, string directory)
    {
        HandbookContent? content = null;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (window.OwnedWindows.OfType<ContextHelpWindow>().FirstOrDefault() is not { } help) return;
            content = help.DataContext as HandbookContent;
            help.Close();
        }));
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.F1)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        window.RaiseEvent(key);
        Assert.True(key.Handled);
        Assert.Equal("4. Prüfen als Werker", Assert.IsType<HandbookContent>(content).Topic.Heading);

        var previous = Application.Current.MainWindow;
        Application.Current.MainWindow = window;
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (window.OwnedWindows.OfType<NewProjectWindow>().FirstOrDefault() is not { } wizard) return;
                try
                {
                    var name = (TextBox)wizard.FindName("ProjectName");
                    var templates = (ListBox)wizard.FindName("Templates");
                    var create = Descendants(wizard).OfType<Button>().Single(button => Equals(button.Content, "Prüfprogramm erstellen"));
                    name.Text = "";
                    create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var message = (TextBlock)wizard.FindName("ValidationMessage");
                    if (string.IsNullOrEmpty(message.Text)) return;
                    templates.SelectedIndex = 1;
                    name.Text = "Mein Prüfprogramm";
                    create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                finally { if (wizard.IsVisible) wizard.Close(); }
            }));
            window.Shell.Editor.NewCommand.Execute(null);
        }
        finally { Application.Current.MainWindow = previous; }

        Assert.Equal("Mein Prüfprogramm", window.Shell.Editor.ProjectName);
        Assert.True(window.Shell.Editor.IsDirty);
        Assert.Null(window.Shell.Editor.FilePath);
        Assert.True(window.Shell.Editor.Save(Path.Combine(directory, "new.tproj.json")));
    }
}
