using System.Threading;
using System.Windows;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

/// <summary>Unabhängiges Fenster einer bestimmten Karte; weitere Editor-Auswahlen ändern diese Historie nicht.</summary>
public partial class NodeHistoryWindow : Window
{
    private readonly CancellationTokenSource _loading = new();
    public NodeHistoryWindow(NodeHistoryViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Loaded += async (_, _) => await model.LoadAsync(_loading.Token);
        Closed += (_, _) => _loading.Cancel();
    }
}
