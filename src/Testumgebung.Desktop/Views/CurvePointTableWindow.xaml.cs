using System.Windows;
using System.Windows.Controls;
using Testumgebung.Domain.Curves;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public partial class CurvePointTableWindow : Window
{
    private readonly CurvePointTable _table;
    private int _channel = -1;
    private int _page;

    public CurveDefinition? EditedCurve { get; private set; }

    public CurvePointTableWindow(CurveDefinition source)
    {
        _table = new CurvePointTable(source);
        InitializeComponent();
        Lines.ItemsSource = source.Channels.Select(line => new
        {
            Label = string.IsNullOrWhiteSpace(line.Unit) ? line.Id : $"{line.Id} [{line.Unit}]"
        }).ToArray();
        Lines.SelectedIndex = 0;
    }

    private void SelectLine(object sender, SelectionChangedEventArgs args)
    {
        if (Lines.SelectedIndex < 0) return;
        SavePage();
        _channel = Lines.SelectedIndex;
        _page = 0;
        ShowPage();
    }

    private void SavePage()
    {
        if (Points.ItemsSource is not IEnumerable<CurvePointTable.Row> rows) return;
        Points.CommitEdit(DataGridEditingUnit.Cell, true);
        Points.CommitEdit(DataGridEditingUnit.Row, true);
        _table.KeepChanges(_channel, rows);
    }

    private void ShowPage()
    {
        Points.ItemsSource = _table.ReadPage(_channel, _page).ToArray();
        var pageCount = _table.PageCount(_channel);
        PageInput.Text = (_page + 1).ToString();
        PageTotal.Text = $"von {pageCount}";
        Previous.IsEnabled = _page > 0;
        Next.IsEnabled = _page + 1 < pageCount;
        Error.Text = "";
    }

    private void MoveToPage(int page)
    {
        SavePage();
        _page = page;
        ShowPage();
    }

    private void PreviousPage(object sender, RoutedEventArgs args) => MoveToPage(_page - 1);
    private void NextPage(object sender, RoutedEventArgs args) => MoveToPage(_page + 1);

    private void JumpPage(object sender, RoutedEventArgs args)
    {
        if (int.TryParse(PageInput.Text, out var page) && page >= 1 && page <= _table.PageCount(_channel))
            MoveToPage(page - 1);
        else
            Error.Text = $"Seite zwischen 1 und {_table.PageCount(_channel)} eingeben.";
    }

    private void AddPoint(object sender, RoutedEventArgs args)
    {
        SavePage();
        var row = _table.Add(_channel);
        _page = row.Index / CurvePointTable.PageSize;
        ShowPage();
        Points.SelectedItem = row;
        Points.ScrollIntoView(row);
        Points.CurrentCell = new DataGridCellInfo(row, Points.Columns[1]);
        Points.Focus();
        Points.BeginEdit();
    }

    private void RemovePoint(object sender, RoutedEventArgs args)
    {
        if (Points.SelectedItem is not CurvePointTable.Row row) return;
        SavePage();
        _table.Remove(_channel, row);
        ShowPage();
    }

    private void Apply(object sender, RoutedEventArgs args)
    {
        SavePage();
        try
        {
            EditedCurve = _table.Build();
            DialogResult = true;
        }
        catch (ArgumentException error)
        {
            Error.Text = error.Message;
        }
    }
}
