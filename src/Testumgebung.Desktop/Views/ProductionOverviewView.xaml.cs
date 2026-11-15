using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public partial class ProductionOverviewView : UserControl
{
    public ProductionOverviewView() => InitializeComponent();

    private void OnMonitor(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportsViewModel reports || Window.GetWindow(this) is not { } owner) return;
        var view = new ProductionOverviewView { DataContext = reports };
        var monitor = new Window
        {
            Owner = owner,
            Title = "Fertigungsübersicht",
            WindowStyle = WindowStyle.None,
            WindowState = WindowState.Maximized,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = view,
        };

        view.OverviewGrid.FontSize = 22;
        view.OverviewGrid.MinRowHeight = 42;
        view.MonitorButton.Content = "Vollbild verlassen (Esc)";
        view.MonitorButton.Click -= view.OnMonitor;
        view.MonitorButton.Click += (_, _) => monitor.Close();
        monitor.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            monitor.Close();
        };
        ShowMonitor(monitor, owner.DataContext as ShellViewModel, reports);
    }

    private static void ShowMonitor(Window monitor, ShellViewModel? shell, ReportsViewModel reports)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += (_, _) => reports.LoadCommand.Execute(null);
        monitor.Loaded += (_, _) => { reports.LoadCommand.Execute(null); timer.Start(); };

        void OnSessionChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ShellViewModel.CanSeeReports) && shell?.CanSeeReports == false)
                monitor.Close();
        }

        if (shell is not null) shell.PropertyChanged += OnSessionChanged;
        try { monitor.ShowDialog(); }
        finally
        {
            timer.Stop();
            if (shell is not null) shell.PropertyChanged -= OnSessionChanged;
        }
    }
}
