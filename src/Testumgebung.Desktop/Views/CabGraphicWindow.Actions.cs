using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Testumgebung.Domain.Labels.CabJScript;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

internal sealed partial class CabGraphicWindow
{
    private void ApplyPosition()
    {
        if (Selected is not { } item) return;
        var moved = CabGraphicEditor.Move(_editor, item, Input("X"), Input("Y"));
        var fresh = CabGraphicFactory.Build(moved, _layout, JobIndex).Items.First(i => i.Column == 0 && i.Command.Syntax.Range.Offset == item.Command.Syntax.Range.Offset);
        Apply(CabGraphicEditor.Rotate(moved, fresh, Input("Drehung")));
    }
    private void ApplySize()
    {
        if (Selected is not { } item) return;
        var factor = item.Command.Syntax.Unit == CabUnit.Inches ? _layout.Dpi : _layout.Dpi / 25.4;
        Apply(CabGraphicEditor.Resize(_editor, item, Input("Breite") * factor, Input("Höhe") * factor, _layout.Dpi));
    }
    private void Add()
    {
        if (_catalog.SelectedItem is not CabCatalogEntry entry) return;
        if (entry.Availability(_editor.Settings.GenerationForJob(JobIndex)) == CabAvailability.Unsupported) throw new InvalidOperationException("Die Definition ist für die gewählte Generation nicht verfügbar.");
        if (entry.Id == "JS-4.9") { AddImage(); return; }
        var next = CabGraphicEditor.Add(_editor, entry.Id, JobIndex);
        Apply(next);
    }
    private void AddImage()
    {
        var dialog = new OpenFileDialog { Title = "Bild als cab-Ressource hinzufügen", Filter = "Bilder|*.bmp;*.gif;*.png;*.tif;*.tiff" };
        if (dialog.ShowDialog(this) != true) return;
        var type = Path.GetExtension(dialog.FileName).TrimStart('.').ToUpperInvariant();
        if (type == "TIFF") type = "TIF";
        var name = Path.GetFileNameWithoutExtension(dialog.FileName);
        AddImageBytes(type, name, File.ReadAllBytes(dialog.FileName));
    }
    internal void AddImageBytes(string type, string name, byte[] bytes)
    {
        var taken = CabResourceEditor.Resources(_editor).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var unique = name; var suffix = 2;
        while (taken.Contains(unique)) unique = name + suffix++;
        var start = CabJobEditor.Items(_editor).First(i => i.Command?.Syntax.Command == "J" && i.Command.JobIndex == JobIndex);
        var next = CabResourceEditor.Add(_editor, type, unique, bytes, start);
        var print = CabJobEditor.Items(next).First(i => i.Command?.Syntax.Command == "A" && i.Command.JobIndex == JobIndex);
        var at = print.Range.Offset;
        next = CabGraphicEditor.Add(next, "JS-4.9", JobIndex);
        var command = next.Validation.Commands.First(c => c.Syntax.Range.Offset == at);
        var property = CabPropertyBindings.ForCommand(next, command).Single(p => p.Definition.Name == "name");
        Apply(CabPropertyBindings.Replace(next, property, unique));
    }
    private void Properties()
    {
        if (Selected is not { } item) return;
        OpenProperties(item.Command.Syntax.Range.Offset);
    }
    private void LabelProperties()
    {
        var command = _editor.Validation.Commands.FirstOrDefault(c => c.JobIndex == JobIndex && c.Syntax.Command == "S")
            ?? _editor.Validation.Commands.FirstOrDefault(c => c.JobIndex == JobIndex && c.Syntax.Command == "J");
        if (command is not null) OpenProperties(command.Syntax.Range.Offset);
    }
    private void OpenProperties(int offset)
    {
        var window = new CabPropertiesWindow(_layout, _refresh) { Owner = this };
        window.SelectSourceOffset(offset); window.ShowDialog();
        var next = Read(); _history.Record(next); _editor = next; Reload(JobIndex);
    }
    private void DragStart(CabGraphicItem item, FrameworkElement visual, MouseButtonEventArgs args)
    {
        _items.SelectedItem = _items.Items.OfType<CabGraphicItem>().First(i => i.Command.Syntax.Range.Offset == item.Command.Syntax.Range.Offset);
        _dragItem = item; _dragVisual = visual; _dragStart = args.GetPosition(_canvas); _dragMatrix = visual.RenderTransform.Value;
        _canvas.CaptureMouse(); args.Handled = true;
    }
    private void DragMove(object sender, MouseEventArgs args)
    {
        if (_dragVisual is null || args.LeftButton != MouseButtonState.Pressed) return;
        var position = args.GetPosition(_canvas); var matrix = _dragMatrix;
        matrix.OffsetX += position.X - _dragStart.X; matrix.OffsetY += position.Y - _dragStart.Y;
        _dragVisual.RenderTransform = new MatrixTransform(matrix); args.Handled = true;
    }
    private void DragEnd(object sender, MouseButtonEventArgs args)
    {
        if (_dragItem is not { } item || _scene is null) return;
        var position = args.GetPosition(_canvas); _canvas.ReleaseMouseCapture(); _dragItem = null; _dragVisual = null;
        Run(() => Apply(CabGraphicEditor.MoveBy(_editor, _scene, item, position.X - _dragStart.X, position.Y - _dragStart.Y)));
        Draw(); args.Handled = true;
    }
    internal void MoveSelectedBy(double x, double y)
    {
        if (_scene is not null && Selected is { } item) Apply(CabGraphicEditor.MoveBy(_editor, _scene, item, x, y));
    }
}
