using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Testumgebung.Desktop.Views;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyProductionMonitor(Dispatcher dispatcher)
    {
        var shell = new ShellViewModel(SynchronizationContextDispatcher.FromCurrent());
        if (shell.CanEnableDevelopmentLogin) shell.Session.EnableDevelopmentMode();
        shell.Login();
        Sta.Flush(dispatcher);
        Assert.True(shell.CanSeeReports);

        var view = new ProductionOverviewView { DataContext = shell.ReportsPage };
        var owner = Sta.ShowHost(view);
        owner.DataContext = shell;
        try
        {
            WindowStyle? style = null;
            WindowState? state = null;
            object? dataContext = null;
            var closedOnLogout = false;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (owner.OwnedWindows.OfType<Window>().FirstOrDefault() is not { } monitor) return;
                try
                {
                    style = monitor.WindowStyle;
                    state = monitor.WindowState;
                    dataContext = (monitor.Content as ProductionOverviewView)?.DataContext;
                    shell.Logout();
                    Sta.Flush(dispatcher);
                    closedOnLogout = !monitor.IsVisible;
                }
                finally
                {
                    if (monitor.IsVisible) monitor.Close();
                }
            }));

            var button = Assert.IsType<Button>(view.FindName("MonitorButton"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(WindowStyle.None, style);
            Assert.Equal(WindowState.Maximized, state);
            Assert.Same(shell.ReportsPage, dataContext);
            Assert.True(closedOnLogout);
        }
        finally { owner.Close(); }
    }
}
