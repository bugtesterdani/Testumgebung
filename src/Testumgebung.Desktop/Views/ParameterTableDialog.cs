using System.Data;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

internal static class ParameterTableDialog
{
    public static void Edit(FrameworkElement source, ParameterRowViewModel field)
    {
        ParameterTable table;
        try { table = ParameterTable.Read(field); }
        catch (Exception error) when (error is ArgumentException or JsonException)
        {
            EditRaw(source, field, error.Message);
            return;
        }

        using var data = new DataTable();
        foreach (var name in table.Columns) data.Columns.Add(name, typeof(string));
        foreach (var row in table.Rows) data.Rows.Add(row.Select(cell => (object?)cell ?? DBNull.Value).ToArray());
        var grid = new DataGrid { ItemsSource = data.DefaultView, AutoGenerateColumns = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, SelectionMode = DataGridSelectionMode.Extended };
        var dialog = CreateWindow(source, field.Label);
        var content = Layout(dialog, grid, out var save, out var errorText);
        var buttons = (StackPanel)content.Children[0];
        var add = new Button { Content = "Zeile hinzufügen", Margin = new Thickness(4), MinWidth = 110 };
        add.Click += (_, _) =>
        {
            var row = data.NewRow();
            data.Rows.Add(row);
            grid.SelectedItem = data.DefaultView[data.Rows.Count - 1];
            grid.ScrollIntoView(grid.SelectedItem);
        };
        var remove = new Button { Content = "Auswahl löschen", Margin = new Thickness(4), MinWidth = 110 };
        remove.Click += (_, _) =>
        {
            grid.CancelEdit(DataGridEditingUnit.Cell);
            grid.CancelEdit(DataGridEditingUnit.Row);
            foreach (var row in grid.SelectedItems.OfType<DataRowView>().ToArray()) row.Delete();
        };
        buttons.Children.Insert(0, add);
        buttons.Children.Insert(1, remove);
        save.Click += (_, _) => AcceptTable(dialog, field, grid, data, errorText);
        dialog.ShowDialog();
    }

    private static void AcceptTable(Window dialog, ParameterRowViewModel field, DataGrid grid, DataTable data, TextBlock errorText)
    {
        if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        try
        {
            var rows = data.Rows.Cast<DataRow>().Select(row => row.ItemArray.Select(cell => cell as string).ToArray()).ToArray();
            field.Owner.ApplyTableValues(ParameterTable.Write(field, rows));
            dialog.DialogResult = true;
        }
        catch (ArgumentException error) { errorText.Text = error.Message; }
    }

    private static void EditRaw(FrameworkElement source, ParameterRowViewModel field, string message)
    {
        var dialog = CreateWindow(source, field.Label);
        var raw = new TextBox { Text = field.Value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        Layout(dialog, raw, out var save, out var error);
        error.Text = "Die Liste kann so nicht als Tabelle geöffnet werden. Bitte den gespeicherten Text korrigieren. " + message;
        save.Click += (_, _) =>
        {
            field.Value = raw.Text;
            dialog.DialogResult = true;
        };
        dialog.ShowDialog();
    }

    private static Window CreateWindow(FrameworkElement source, string title) => new()
    {
        Owner = Window.GetWindow(source), WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Title = title, Width = 760, Height = 420, MinWidth = 420, MinHeight = 260, ShowInTaskbar = false
    };

    private static DockPanel Layout(Window dialog, FrameworkElement editor, out Button save, out TextBlock error)
    {
        var content = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom);
        save = new Button { Content = "Übernehmen", IsDefault = true, MinWidth = 100, Margin = new Thickness(4) };
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 100, Margin = new Thickness(4) });
        content.Children.Add(buttons);
        error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        DockPanel.SetDock(error, Dock.Bottom);
        content.Children.Add(error);
        content.Children.Add(editor);
        dialog.Content = content;
        return content;
    }
}
