using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Testumgebung.Desktop.Controls;

/// <summary>Codeeingabe mit festen, ab eins gezählten Quelltextzeilen für Debug-Haltepunkte.</summary>
public sealed class NumberedCodeEditor : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(NumberedCodeEditor),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public NumberedCodeEditor()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        var editor = new TextBox
        {
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        editor.SetResourceReference(StyleProperty, "CodeBox");
        editor.SetBinding(TextBox.TextProperty, new Binding(nameof(Text))
        {
            Source = this, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        SetColumn(editor, 1);
        var numbers = new LineNumbers(editor);
        Children.Add(numbers);
        Children.Add(editor);
        editor.TextChanged += (_, _) => { numbers.InvalidateMeasure(); numbers.InvalidateVisual(); };
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => numbers.InvalidateVisual()));
        editor.SizeChanged += (_, _) => numbers.InvalidateVisual();
        editor.Loaded += (_, _) => { numbers.InvalidateMeasure(); numbers.InvalidateVisual(); };
    }

    /// <summary>Zeichnet nur sichtbare Zeilen; die Position kommt aus dem tatsächlichen Textlayout.</summary>
    private sealed class LineNumbers(TextBox editor) : FrameworkElement
    {
        private FormattedText Label(int line) => new(line.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch),
            editor.FontSize, SystemColors.GrayTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override Size MeasureOverride(Size availableSize) =>
            new(Label(Math.Max(1, editor.LineCount)).Width + 18, 0);

        protected override void OnRender(DrawingContext drawing)
        {
            drawing.PushClip(new RectangleGeometry(new Rect(RenderSize)));
            drawing.DrawRectangle(SystemColors.ControlBrush, null, new Rect(RenderSize));
            int first = editor.GetFirstVisibleLineIndex();
            int last = editor.GetLastVisibleLineIndex();
            for (int line = Math.Max(0, first); line <= last; line++)
            {
                int index = editor.GetCharacterIndexFromLineIndex(line);
                if (index < 0) continue;
                var position = editor.GetRectFromCharacterIndex(index);
                if (position.IsEmpty) continue;
                var label = Label(line + 1);
                drawing.DrawText(label, new Point(ActualWidth - label.Width - 8, position.Top));
            }
            drawing.Pop();
        }
    }
}