using System.Windows;
using Testumgebung.Presentation.ViewModels;
namespace Testumgebung.Desktop.Views;

public partial class SourceAuthenticationWindow : Window
{
    public SourceAuthenticationWindow(ProgramSourcesViewModel model)
    {
        InitializeComponent(); DataContext = model;
        Closed += (_, _) => Secret.Clear();
        SourceInitialized += (_, _) => MaxHeight = AdaptiveWindowLayout.CurrentWorkArea(this).Height;
    }
    private void OnApply(object sender, RoutedEventArgs args)
    {
        try { if (((ProgramSourcesViewModel)DataContext).SaveAuthentication(Secret.Password)) DialogResult = true; }
        finally { Secret.Clear(); }
    }
}