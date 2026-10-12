using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

/// <summary>Protokolltabelle eines Prüfplatzes; scrollt automatisch zum neuesten Eintrag.</summary>
public partial class ProtocolGrid : UserControl
{
    private INotifyCollectionChanged? _source;

    public ProtocolGrid()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_source is not null) _source.CollectionChanged -= OnChanged;
        _source = (DataContext as StationViewModel)?.Protocol;
        if (_source is not null) _source.CollectionChanged += OnChanged;
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !IsVisible) return;
        Dispatcher.InvokeAsync(() =>
        {
            var count = ProtocolTable.Items.Count;
            if (count > 0) ProtocolTable.ScrollIntoView(ProtocolTable.Items[count - 1]);
        }, DispatcherPriority.Background);
    }
}
