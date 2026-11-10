using System.Windows;
using System.Windows.Controls;
using Testumgebung.Presentation.ViewModels;
namespace Testumgebung.Desktop.Views;
public partial class ProgramSourcesView : UserControl
{
    public ProgramSourcesView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is ProgramSourcesViewModel model) model.RequestAuthentication = PromptAuthentication; };
        Unloaded += (_, _) => { if (DataContext is ProgramSourcesViewModel model) { model.RequestAuthentication = null; model.CancelSourceOperation(); } };
    }
    private Task<bool> PromptAuthentication()
    {
        if (DataContext is not ProgramSourcesViewModel model) return Task.FromResult(false);
        var window = new SourceAuthenticationWindow(model) { Owner = Window.GetWindow(this) };
        return Task.FromResult(window.ShowDialog() == true);
    }
    private async void OnLogin(object sender, RoutedEventArgs eventArgs) => await PromptAuthentication();
    private async void OnTestConnection(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ProgramSourcesViewModel model) await model.TestConnectionAsync();
    }
    private void OnRemoveLogin(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ProgramSourcesViewModel model) model.RemoveAuthentication();
    }
    private void OnCancelConnection(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is ProgramSourcesViewModel model) model.CancelSourceOperation();
    }
}