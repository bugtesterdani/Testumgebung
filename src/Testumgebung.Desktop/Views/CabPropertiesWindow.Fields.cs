using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Desktop.Views;

internal sealed partial class CabPropertiesWindow
{
    private void ShowFields()
    {
        _fields.Children.Clear();
        if (Selected is not { } item) return;
        _fields.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        if (item.Syntax.Kind == CabNodeKind.Comment || item.Command is not { } command)
        {
            _fields.Children.Add(new TextBlock { Text = "Kommentare, unbekannte Befehle und geschützte Blöcke bleiben im Quelltext erhalten.", TextWrapping = TextWrapping.Wrap });
            _fields.Children.Add(new TextBox { Text = _editor.Document.Decode(item.Syntax) ?? "Geschützte Bytes", IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
            return;
        }
        CommandFields(command);
        ContentFields(command);
        foreach (var resource in CabResourceEditor.Resources(_editor).Where(r => ReferenceEquals(r.Command, command) || r.Block.Range == command.Syntax.Range)) Resource(resource);
    }

    private void CommandFields(CabBoundCommand command)
    {
        foreach (var property in CabPropertyBindings.ForCommand(_editor, command))
            if (property.IsPresent || !property.Definition.IsOptional || _optional.IsChecked == true) PropertyRow(command, property);
    }

    private void ContentFields(CabBoundCommand command)
    {
        foreach (var content in CabContentValidation.Bind(_editor.Document, _editor.Validation.Commands).Where(c => ReferenceEquals(c.Command, command)))
            foreach (var field in content.Syntax.Fields)
            {
                _fields.Children.Add(new TextBlock { Text = field.Definition.Title, FontWeight = FontWeights.Bold, Margin = new(0, 12, 0, 0) });
                foreach (var property in CabPropertyBindings.ForContent(_editor, content, field))
                    if (property.IsPresent || !property.Definition.IsOptional || _optional.IsChecked == true) PropertyRow(command, property);
            }
    }

    private void PropertyRow(CabBoundCommand command, CabBoundProperty property)
    {
        var definition = property.Definition;
        var row = new StackPanel { Margin = new(0, 6, 0, 3), ToolTip = definition.Help };
        row.Children.Add(new TextBlock { Text = definition.Name + " · " + definition.ControlText + " · " + definition.Unit + " · " + definition.Generation + " · " + definition.AvailabilityText });
        var description = new TextBlock { Text = definition.Rule.Parameter.Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 700 };
        row.Children.Add(description);
        var editor = new TextBox { Text = property.EditorValue, IsReadOnly = !property.IsTextEditable, MinWidth = 200 };
        System.Windows.Automation.AutomationProperties.SetName(editor, definition.Name);
        row.Children.Add(editor);
        ContentButton(row, editor, property);
        if (definition.Choices.Count > 0) Choices(row, editor, definition);
        if (definition.Range is not null) row.Children.Add(new TextBlock { Text = "Bereich: " + definition.RangeText });
        if (definition.Control == CabPropertyControl.Answer)
            row.Children.Add(new TextBlock { Text = "Druckerantwort · Ausgabe, keine schreibbare Eingabe" });
        else if (property.IsTextEditable) ApplyButton(row, editor, command, property);
        _fields.Children.Add(row);
    }

    private void ContentButton(Panel row, TextBox editor, CabBoundProperty property)
    {
        if (property.IsTextEditable && property.IsPresent && property.Content is null && property.Definition.Name is "text" or "data" or "value")
            AddButton(row, "Inhaltsfeld einfügen …", () =>
            {
                var context = CabContentValidation.Bind(_editor.Document, _editor.Validation.Commands)
                    .FirstOrDefault(c => ReferenceEquals(c.Parameter, property.Source));
                var dialog = new CabContentWindow(property.Definition.Generation, _editor.Settings.Preview, context?.PreviousNames) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.Insertion is not { } text) return;
                var start = editor.SelectionStart; var length = editor.SelectionLength;
                editor.Text = editor.Text.Remove(start, length).Insert(start, text);
                editor.SelectionStart = start + text.Length; editor.Focus();
            });
    }

    private void ApplyButton(Panel row, TextBox editor, CabBoundCommand command, CabBoundProperty property)
    {
            var definition = property.Definition;
            var buttons = new WrapPanel();
            AddButton(buttons, property.IsPresent ? "Übernehmen" : definition.IsOptional ? "Optionalen Parameter hinzufügen" : "Pflichtparameter ergänzen", () =>
            {
                var next = property.IsPresent ? CabPropertyBindings.Replace(_editor, property, editor.Text)
                    : property.Content is null ? CabOptionalParameter.Add(_editor, command, definition, editor.Text)
                    : CabContentOptional.Add(_editor, property.Content,
                        property.Field!, definition, editor.Text);
                Apply(next);
            });
            row.Children.Add(buttons);
    }

    internal static void Choices(Panel row, TextBox editor, CabPropertyDefinition definition)
    {
        var choices = new ComboBox { MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var choice in definition.Choices)
            choices.Items.Add(new ComboBoxItem { Content = choice.ToString(), Tag = choice.Value, IsEnabled = choice.IsEnabled && definition.IsEnabled,
                ToolTip = choice.Description + " · " + choice.Availability });
        choices.SelectionChanged += (_, _) =>
        {
            if (choices.SelectedItem is not ComboBoxItem { Tag: string value }) return;
            var example = CabPropertyTemplates.Example(definition.Rule.Entry.Id, definition.Name, value);
            if (definition.Control == CabPropertyControl.Options)
            {
                var separator = definition.Rule.ChoiceMode == CabChoiceMode.PlusList ? "" : ",";
                editor.Text = editor.Text.Length == 0 ? example : editor.Text + separator + example;
            }
            else editor.Text = example;
        };
        row.Children.Add(choices);
    }

    private void Resource(CabResource resource)
    {
        var panel = new StackPanel { Margin = new(0, 12, 0, 6) };
        panel.Children.Add(new TextBlock { Text = resource.Type + " · " + resource.Name, FontWeight = FontWeights.Bold });
        Run(() => ShowResource(panel, resource));
        AddButton(panel, "Datei ersetzen …", () =>
        {
            var dialog = new OpenFileDialog { Title = "Dateidaten für " + resource.Type + " ersetzen", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) Apply(CabResourceEditor.Replace(_editor, resource, File.ReadAllBytes(dialog.FileName)));
        });
        _fields.Children.Add(panel);
    }

    private void ShowResource(Panel panel, CabResource resource)
    {
        var data = CabResourceEditor.Read(_editor, resource);
        panel.Children.Add(new TextBlock { Text = data.Length + " Bytes · " + resource.Block.Command });
        if (resource.Type.ToUpperInvariant() is not ("BMP" or "GIF" or "PNG" or "TIF")) return;
        using var stream = new MemoryStream(data);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        panel.Children.Add(new Image { Source = image, MaxWidth = 420, MaxHeight = 240, HorizontalAlignment = HorizontalAlignment.Left });
    }
}
