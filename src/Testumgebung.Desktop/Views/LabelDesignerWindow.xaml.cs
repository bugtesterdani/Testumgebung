using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public partial class LabelDesignerWindow : Window
{
    private readonly LabelLayout _layout;
    private Func<bool>? _replacesManualTemplate;
    private LabelElement? _dragged;
    private Border? _dragVisual;
    private Point _dragOffset;

    internal LabelDesignerWindow(LabelLayout layout)
    {
        _layout = layout;
        InitializeComponent();
        DataContext = layout;
        ReloadElements();
    }

    internal static void Edit(FrameworkElement source, ParameterRowViewModel row)
    {
        var owner = Window.GetWindow(source);
        var layout = ReadLayout(row);
        var originalZpl = layout.ToZpl();
        var dialog = new LabelDesignerWindow(layout)
        {
            Owner = owner,
            _replacesManualTemplate = () => HasManualTemplate(row, originalZpl),
        };
        if (dialog.ShowDialog() != true) return;
        row.Owner.SaveLabelLayout(layout);
    }

    private static bool HasManualTemplate(ParameterRowViewModel row, string originalZpl)
        => row.Value != row.Descriptor.Default && row.Value.ReplaceLineEndings("\n") != originalZpl;

    private static LabelLayout ReadLayout(ParameterRowViewModel row)
    {
        if (!row.Owner.Step.Parameters.TryGetValue(LabelLayout.Parameter, out var json)) return new();
        try
        {
            var layout = LabelLayout.Load(json);
            _ = layout.ToZpl();
            return layout;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return new(); }
    }

    private void ReloadElements(LabelElement? selection = null)
    {
        Elements.ItemsSource = null;
        Elements.ItemsSource = _layout.Elements;
        Elements.SelectedItem = selection ?? _layout.Elements.FirstOrDefault();
        UpdateSheet();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var kind = Enum.Parse<LabelElementKind>((string)((Button)sender).Tag);
        var element = new LabelElement { Kind = kind, X = 12, Y = 12 };
        element.Size = kind switch { LabelElementKind.Qr => 4, LabelElementKind.Line => 3, LabelElementKind.Code128 => 60, _ => 30 };
        Insert(element);
    }

    private void Insert(LabelElement element)
    {
        _layout.Elements.Add(element);
        ReloadElements(element);
    }

    private void OnImage(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
        if (picker.ShowDialog(this) == true) ImportImage(picker.FileName);
    }

    internal void ImportImage(string path)
    {
        try
        {
            Insert(LabelImage.Import(path, _layout.WidthDots, _layout.HeightDots));
            Status.Text = "Bild als Schwarzweißgrafik eingefügt.";
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or UnauthorizedAccessException)
        {
            Status.Text = "Bild konnte nicht geöffnet werden. " + error.Message;
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Elements.SelectedItem is not LabelElement element) return;
        _layout.Elements.Remove(element);
        ReloadElements();
    }

    private void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        var element = Elements.SelectedItem as LabelElement;
        Properties.DataContext = element;
        Properties.Visibility = VisibleIf(element is not null);
        var kind = element?.Kind;
        TextFields.Visibility = VisibleIf(kind is LabelElementKind.Text or LabelElementKind.Code128 or LabelElementKind.Qr);
        SizeFields.Visibility = VisibleIf(kind is not LabelElementKind.Image);
        LengthFields.Visibility = VisibleIf(kind == LabelElementKind.Line);
        SizeLabel.Text = kind == LabelElementKind.Qr ? "QR-Modulgröße (1–10)" : "Höhe / Dicke (Druckpunkte)";
        UpdateSheet();
    }

    private static Visibility VisibleIf(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnPropertiesUpdated(object sender, DataTransferEventArgs e) => UpdateSheet();

    private void UpdateSheet()
    {
        Sheet.Width = _layout.WidthDots;
        Sheet.Height = _layout.HeightDots;
        Sheet.Children.Clear();
        foreach (var element in _layout.Elements) AddPreview(element);
    }

    private void AddPreview(LabelElement element)
    {
        var visual = new Border
        {
            Child = Preview(element),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.DodgerBlue,
            BorderThickness = ReferenceEquals(element, Elements.SelectedItem) ? new Thickness(1) : new Thickness(0),
            Cursor = Cursors.SizeAll,
        };
        Canvas.SetLeft(visual, Math.Clamp(element.X, 0, _layout.WidthDots - 1));
        Canvas.SetTop(visual, Math.Clamp(element.Y, 0, _layout.HeightDots - 1));
        visual.MouseLeftButtonDown += (_, args) => BeginDrag(element, args);
        Sheet.Children.Add(visual);
    }

    private static FrameworkElement Preview(LabelElement element)
    {
        var size = Math.Clamp(element.Size, 1, LabelLayout.MaximumDots);
        var example = element.Value?.Contains('{') == true ? element.Preview : element.Value;
        return element.Kind switch
        {
            LabelElementKind.Image => new Image { Source = LabelImage.Preview(element), Width = element.ImageWidth, Height = element.ImageHeight },
            LabelElementKind.Line => new Border { Background = Brushes.Black, Width = Math.Clamp(element.Length, 1, LabelLayout.MaximumDots), Height = size },
            LabelElementKind.Text => new TextBlock { Text = example, FontSize = size, Foreground = Brushes.Black },
            LabelElementKind.Qr => BarcodePreview($"QR · {example}", Math.Clamp(size, 1, 10) * 25),
            _ => BarcodePreview($"Code 128 · {example}", size),
        };
    }

    private static FrameworkElement BarcodePreview(string text, int height)
        => new Border
        {
            BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Height = height, MinWidth = 85,
            Child = new TextBlock { Text = text, Foreground = Brushes.Black, Margin = new Thickness(3), VerticalAlignment = VerticalAlignment.Center },
        };

    private void BeginDrag(LabelElement element, MouseButtonEventArgs args)
    {
        var position = args.GetPosition(Sheet);
        Elements.SelectedItem = element;
        _dragged = element;
        _dragVisual = (Border)Sheet.Children[_layout.Elements.IndexOf(element)];
        _dragOffset = new(position.X - Canvas.GetLeft(_dragVisual), position.Y - Canvas.GetTop(_dragVisual));
        Sheet.CaptureMouse();
        args.Handled = true;
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (_dragged is not { } element || _dragVisual is not { } visual) return;
        var position = e.GetPosition(Sheet);
        element.X = Math.Clamp((int)Math.Round(position.X - _dragOffset.X), 0, _layout.WidthDots - 1);
        element.Y = Math.Clamp((int)Math.Round(position.Y - _dragOffset.Y), 0, _layout.HeightDots - 1);
        Canvas.SetLeft(visual, element.X);
        Canvas.SetTop(visual, element.Y);
    }

    private void OnDragEnd(object sender, MouseEventArgs e)
    {
        if (_dragged is not { } element) return;
        _dragged = null;
        _dragVisual = null;
        Sheet.ReleaseMouseCapture();
        Properties.DataContext = null;
        Properties.DataContext = element;
    }

    private bool ConfirmManualTemplateReplacement()
    {
        if (_replacesManualTemplate?.Invoke() != true) return true;
        return MessageBox.Show(this, "Die ZPL-Vorlage enthält manuell erstellten Code. Soll der Designer ihn ersetzen?",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = _layout.ToZpl();
            if (!ConfirmManualTemplateReplacement()) return;
            DialogResult = true;
        }
        catch (InvalidDataException error) { Status.Text = error.Message; }
    }
}
