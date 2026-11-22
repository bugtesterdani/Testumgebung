using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Desktop.Views;

internal sealed partial class CabSourceWindow
{
    private sealed record DiagnosticRow(CabDiagnostic Diagnostic)
    {
        public override string ToString() => $"{Diagnostic.Severity} · Zeile {Diagnostic.Line}, Spalte {Diagnostic.Column} · {Diagnostic.Message} ({Diagnostic.ManualReference})";
    }

    private void Format(int index, RichTextBox box)
    {
        var section = _editor.Sections[index].Editable!;
        var highlights = CabSourceHighlights.Read(_editor).Where(h => h.Range.Offset < section.Range.End && h.Range.End > section.Range.Offset).ToArray();
        var cuts = highlights.SelectMany(h => new[] { section.CharacterOffset(Math.Max(section.Range.Offset, h.Range.Offset)),
            section.CharacterOffset(Math.Min(section.Range.End, h.Range.End)) }).Append(0).Append(section.Text.Length).Distinct().Order().ToArray();
        var paragraph = new Paragraph { Margin = new(0), LineHeight = 18 };
        for (var i = 0; i + 1 < cuts.Length; i++)
        {
            var start = cuts[i]; var end = cuts[i + 1];
            var token = highlights.LastOrDefault(h => h.Range.Offset <= section.ByteOffset(start) && h.Range.End >= section.ByteOffset(end));
            var run = new Run(section.Text[start..end]);
            if (token is not null) Decorate(run, token);
            paragraph.Inlines.Add(run);
        }
        box.Document.Blocks.Clear(); box.Document.Blocks.Add(paragraph);
    }

    private TextBlock Gutter(int index)
    {
        var section = _editor.Sections[index]; var text = section.Editable!;
        var diagnostics = _editor.Validation.Diagnostics.Where(d => d.Range.Offset >= section.Range.Offset && d.Range.Offset < section.Range.End).ToArray();
        var lines = diagnostics.Select(d => text.Text[..text.CharacterOffset(d.Range.Offset)].Count(c => c == '\n')).ToHashSet();
        var firstLine = System.Text.Encoding.Latin1.GetString(_editor.Document.Bytes(new(0, section.Range.Offset))).ReplaceLineEndings("\n").Count(c => c == '\n') + 1;
        return new TextBlock { Text = string.Join("\n", Enumerable.Range(0, text.Text.Count(c => c == '\n') + 1)
            .Select(line => $"{firstLine + line,4} {(lines.Contains(line) ? "●" : " ")}")),
            FontFamily = new("Consolas"), FontSize = 14, LineHeight = 18, Foreground = Brushes.Firebrick, Margin = new(0, 3, 6, 0),
            ToolTip = string.Join("\n", diagnostics.Select(d => new DiagnosticRow(d).ToString())) };
    }

    private static void Decorate(Run run, CabSourceHighlight token)
    {
        run.ToolTip = token.Tooltip;
        run.Foreground = token.Style switch
        {
            CabSourceStyle.LabelCommand => Brushes.DarkBlue,
            CabSourceStyle.ImmediateCommand => Brushes.DarkViolet,
            CabSourceStyle.EscapeCommand => Brushes.SaddleBrown,
            CabSourceStyle.Comment => Brushes.Gray,
            CabSourceStyle.ContentField => Brushes.DarkMagenta,
            _ => Brushes.DarkCyan,
        };
        if (token.Style == CabSourceStyle.ContentField) run.FontWeight = FontWeights.Bold;
    }

    private void Navigate()
    {
        if (_diagnostics.SelectedItem is not DiagnosticRow row) return;
        var index = Enumerable.Range(0, _editor.Sections.Count)
            .FirstOrDefault(i => _editor.Sections[i].Range.Offset <= row.Diagnostic.Range.Offset
                && _editor.Sections[i].Range.End >= row.Diagnostic.Range.Offset, -1);
        if (!_editors.TryGetValue(index, out var box)) return;
        var section = _editor.Sections[index].Editable!;
        var start = At(box, section.CharacterOffset(row.Diagnostic.Range.Offset));
        var end = At(box, section.CharacterOffset(Math.Min(section.Range.End, row.Diagnostic.Range.End)));
        box.Selection.Select(start, end); _active = box; box.Focus(); start.Paragraph?.BringIntoView();
    }

    private static TextPointer At(RichTextBox box, int character)
    {
        var pointer = box.Document.ContentStart;
        while (pointer is not null)
        {
            var text = pointer.GetTextInRun(LogicalDirection.Forward);
            if (text.Length > 0)
            {
                if (character <= text.Length) return pointer.GetPositionAtOffset(character)!;
                character -= text.Length;
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        return box.Document.ContentEnd;
    }
}
