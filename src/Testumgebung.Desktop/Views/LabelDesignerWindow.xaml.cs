using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Testumgebung.Presentation.ViewModels;
using Testumgebung.Engine.Blocks;

namespace Testumgebung.Desktop.Views;

public partial class LabelDesignerWindow : Window
{
    private readonly LabelLayout _layout;
    private readonly bool _cab;
    private Func<bool>? _replacesManualTemplate;
    private LabelElement? _dragged;
    private Border? _dragVisual;
    private Point _dragOffset;

    internal LabelDesignerWindow(LabelLayout layout, bool cab = false)
    {
        _layout = layout;
        _cab = cab;
        InitializeComponent();
        GapFields.Visibility = VisibleIf(cab);
        OpenCabButton.Visibility = VisibleIf(cab);
        SaveCabButton.Visibility = VisibleIf(cab);
        ((FrameworkElement)CabSetupCodePage.Parent).Visibility = VisibleIf(cab);
        CabSetupCodePage.Text = layout.CabSource?.SetupCodePage ?? "UTF-8";
        CabValidationWindow.Attach((Panel)CabSetupCodePage.Parent, this, layout, cab);
        Gs1Identifiers.ItemsSource = Gs1LabelData.Identifiers;
        Gs1Identifiers.SelectedIndex = 1;

        if (cab)
        {
            Title = "cab-Etikett gestalten";
            TextLabel.Text = "Text / Barcodeinhalt, z. B. {Kennung}";
        }
        DataContext = layout;
        ReloadElements();
    }

    internal static void Edit(FrameworkElement source, ParameterRowViewModel row)
    {
        var owner = Window.GetWindow(source);
        var cab = row.IsCabLabel;
        LabelLayout layout;
        try { layout = ReadLayout(row, cab); }
        catch (Exception error) when (error is InvalidDataException or FormatException)
        {
            MessageBox.Show(owner, error.Message, "LBL kann nicht im Designer geöffnet werden", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var original = RenderValue(layout, cab);
        var dialog = new LabelDesignerWindow(layout, cab)
        {
            Owner = owner,
            _replacesManualTemplate = () => !cab && HasManualTemplate(row, original),
        };
        if (cab && HasManualTemplate(row, original))
            dialog.Status.Text = "Vorhandene LBL geladen. Änderungen werden mit „Vorlage übernehmen“ gespeichert. Druckauflösung prüfen.";
        if (dialog.ShowDialog() != true) return;
        if (cab) row.Owner.SaveCabLabelLayout(layout);
        else row.Owner.SaveLabelLayout(layout);
    }

    private static string RenderValue(LabelLayout layout, bool cab)
        => cab ? Convert.ToBase64String(CabLabelLayout.Render(layout)) : layout.ToZpl();

    private static bool HasManualTemplate(ParameterRowViewModel row, string original)
        => row.Value != row.Descriptor.Default && row.Value.ReplaceLineEndings("\n") != original;

    internal static LabelLayout ReadLayout(ParameterRowViewModel row, bool cab)
    {
        var parameter = cab ? CabLabelLayout.Parameter : LabelLayout.Parameter;
        if (!row.Owner.Step.Parameters.TryGetValue(parameter, out var json))
            return cab && row.Value.Length > 0 ? CabLabelImport.Read(Convert.FromBase64String(row.Value)) : new();
        try
        {
            var layout = LabelLayout.Load(json);
            _ = RenderValue(layout, cab);
            return layout;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            if (cab) throw new InvalidDataException("Das gespeicherte cab-Layout ist ungültig. " + error.Message, error);
            return new();
        }
    }

    private void OnOpenCab(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "cab-LBL-Druckdateien (*.lbl)|*.lbl", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        OpenCabFile(picker.FileName);
    }

    internal void OpenCabFile(string path)
    {
        try { ImportCab(File.ReadAllBytes(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { Status.Text = error.Message; }
    }

    private void OnSaveCab(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "cab-LBL-Druckdateien (*.lbl)|*.lbl", DefaultExt = ".lbl", FileName = "Etikett.lbl" };
        if (picker.ShowDialog(this) != true) return;
        SaveCabFile(picker.FileName);
    }

    internal void SaveCabFile(string path)
    {
        try
        {
            File.WriteAllBytes(path, CabLabelLayout.Render(_layout));
            Status.Text = "LBL-Datei gespeichert.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { Status.Text = error.Message; }
    }

    internal void ImportCab(byte[] bytes)
    {
        var imported = CabLabelImport.Read(bytes, _layout.Dpi, CabSetupCodePage.Text);
        _layout.WidthMm = imported.WidthMm;
        _layout.HeightMm = imported.HeightMm;
        _layout.GapMm = imported.GapMm;
        _layout.Elements = imported.Elements;
        _layout.CabSource = imported.CabSource;
        DataContext = null;
        DataContext = _layout;
        ReloadElements();
        Status.Text = "LBL vollständig geladen. Darstellbare Elemente können bearbeitet werden; weitere Inhalte bleiben in der Datei erhalten.";
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
        element.ExampleValues = "Kennung=SN-123";
        if (element.IsGs1)
        {
            element.Value = "(01){GTIN}";
            element.ExampleValues = "GTIN=09506000134376";
        }
        element.Size = kind switch { LabelElementKind.Qr or LabelElementKind.DataMatrix or LabelElementKind.Gs1DataMatrix or LabelElementKind.Gs1Qr => 4, LabelElementKind.Line => 3, LabelElementKind.Code128 => 60, _ => 30 };
        Insert(element);
    }

    private void OnInsertGs1Identifier(object sender, RoutedEventArgs e)
    {
        if (Gs1Identifiers.SelectedItem is not Gs1ApplicationIdentifier identifier) return;
        BarcodeContent.SelectedText = $"({identifier.Id})";
        BarcodeContent.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        BarcodeContent.Focus();
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
        var matrix = IsMatrix(kind);
        TextFields.Visibility = VisibleIf(matrix || kind is LabelElementKind.Text or LabelElementKind.Code128);
        Gs1Fields.Visibility = VisibleIf(element?.IsGs1 == true);
        SizeFields.Visibility = VisibleIf(kind is not LabelElementKind.Image);
        LengthFields.Visibility = VisibleIf(kind == LabelElementKind.Line);
        SizeLabel.Text = matrix ? "Modulgröße (1–10 Druckpunkte)" : "Höhe / Dicke (Druckpunkte)";
        UpdateSheet();
    }

    private static bool IsMatrix(LabelElementKind? kind) => kind is LabelElementKind.Qr or LabelElementKind.DataMatrix
        or LabelElementKind.Gs1DataMatrix or LabelElementKind.Gs1Qr;

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
        var example = element.PreviewText();
        return element.Kind switch
        {
            LabelElementKind.Image => new Image { Source = LabelImage.Preview(element), Width = element.ImageWidth, Height = element.ImageHeight },
            LabelElementKind.Line => new Border { Background = Brushes.Black, Width = Math.Clamp(element.Length, 1, LabelLayout.MaximumDots), Height = size },
            LabelElementKind.Text => new TextBlock { Text = example, FontSize = size, Foreground = Brushes.Black },
            LabelElementKind.Qr => BarcodePreview($"QR · {example}", Math.Clamp(size, 1, 10) * 25),
            LabelElementKind.DataMatrix => BarcodePreview($"DataMatrix · {example}", Math.Clamp(size, 1, 10) * 25),
            LabelElementKind.Gs1DataMatrix => BarcodePreview($"GS1 DataMatrix · {example}", Math.Clamp(size, 1, 10) * 25),
            LabelElementKind.Gs1Qr => BarcodePreview($"GS1 QR · {example}", Math.Clamp(size, 1, 10) * 25),
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
        var message = _cab ? "Soll der Designer die eingebettete cab-Druckdatei ersetzen?"
            : "Die ZPL-Vorlage enthält manuell erstellten Code. Soll der Designer ihn ersetzen?";
        return MessageBox.Show(this, message,
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = RenderValue(_layout, _cab);
            if (!ConfirmManualTemplateReplacement()) return;
            DialogResult = true;
        }
        catch (InvalidDataException error) { Status.Text = error.Message; }
    }
}
