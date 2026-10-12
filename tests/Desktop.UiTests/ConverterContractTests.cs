using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Opc.Ua;
using Testumgebung.Contracts;
using Testumgebung.Desktop.Converters;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed class ConverterContractTests
{
    private static object Forward(IValueConverter converter, object? value, object? parameter = null)
        => converter.Convert(value, typeof(object), parameter, CultureInfo.InvariantCulture);

    [Fact]
    public void Ratings_states_and_icons_have_frozen_brushes_and_stable_display_text()
    {
        var color = new RatingToBrushConverter();
        var glyph = new RatingToGlyphConverter();
        var text = new RatingToTextConverter();
        foreach (var rating in Enum.GetValues<ResultRating>())
        {
            var brush = Assert.IsType<SolidColorBrush>(Forward(color, rating));
            Assert.True(brush.IsFrozen);
            Assert.Equal(rating.ToDisplay(), Forward(text, rating));
            if (rating != ResultRating.NResult) Assert.NotEmpty(Assert.IsType<string>(Forward(glyph, rating)));
        }
        Assert.Same(Forward(color, ResultRating.Fail), Forward(color, ResultRating.Error));
        Assert.NotEqual(Forward(color, ResultRating.Pass), Forward(color, ResultRating.Fail));
        Assert.Equal("—", Forward(text, null));
        Assert.Equal("", Forward(glyph, null));
        foreach (var state in Enum.GetValues<StationStatusKind>()) Assert.True(((Brush)Forward(new StatusKindToBrushConverter(), state)).IsFrozen);
        foreach (var state in Enum.GetValues<ItemState>())
        {
            Assert.True(((Brush)Forward(new ItemStateToBrushConverter(), state)).IsFrozen);
            Assert.IsType<string>(Forward(new ItemStateToGlyphConverter(), state));
        }
        foreach (var icon in Enum.GetValues<NodeIcon>()) Assert.NotEmpty(Assert.IsType<string>(Forward(new NodeIconToGlyphConverter(), icon)));
        foreach (var marker in Enum.GetValues<RunMarker>()) Assert.IsAssignableFrom<Brush>(Forward(new RunMarkerToBrushConverter(), marker));
        Assert.IsAssignableFrom<Brush>(Forward(new StatusKindToBrushConverter(), null));
        Assert.IsAssignableFrom<Brush>(Forward(new ItemStateToBrushConverter(), null));
        Assert.Equal("", Forward(new ItemStateToGlyphConverter(), null));
        Assert.NotEmpty(Assert.IsType<string>(Forward(new NodeIconToGlyphConverter(), null)));
    }

    [Fact]
    public void Visibility_obeys_null_empty_inversion_and_radio_button_reverse_rules()
    {
        var boolean = new BoolToVisibilityConverter();
        Assert.Equal(Visibility.Visible, Forward(boolean, true));
        Assert.Equal(Visibility.Collapsed, Forward(boolean, false));
        Assert.Equal(Visibility.Collapsed, Forward(boolean, null));
        Assert.Equal(Visibility.Collapsed, Forward(boolean, true, "invert"));
        Assert.Equal(Visibility.Visible, Forward(boolean, false, "invert"));
        var empty = new NullToVisibilityConverter();
        foreach (var value in new object?[] { null, "", Array.Empty<int>() })
        {
            Assert.Equal(Visibility.Collapsed, Forward(empty, value));
            Assert.Equal(Visibility.Visible, Forward(empty, value, "invert"));
        }
        foreach (var value in new object[] { "abc", new[] { 1 }, 0, false }) Assert.Equal(Visibility.Visible, Forward(empty, value));
        var equals = new EnumEqualsConverter();
        Assert.Equal(true, Forward(equals, ResultRating.Pass, "Pass"));
        Assert.Equal(false, Forward(equals, ResultRating.Fail, "Pass"));
        Assert.Equal(true, Forward(equals, null));
        Assert.Equal(false, Forward(equals, null, "Pass"));
        Assert.Equal(ResultRating.Pass, equals.ConvertBack(true, typeof(ResultRating), "Pass", CultureInfo.InvariantCulture));
        Assert.Same(Binding.DoNothing, equals.ConvertBack(false, typeof(ResultRating), "Pass", CultureInfo.InvariantCulture));
        Assert.Same(Binding.DoNothing, equals.ConvertBack(true, typeof(ResultRating), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, Forward(new EnumToVisibilityConverter(), ResultRating.Pass, "Pass"));
        Assert.Equal(Visibility.Collapsed, Forward(new EnumToVisibilityConverter(), ResultRating.Fail, "Pass"));
    }

    [Fact]
    public void Enum_labels_cover_every_supported_choice_and_unknown_values_have_fallback()
    {
        var converter = new EnumDisplayConverter();
        var types = new[] { typeof(Testumgebung.Domain.Definitions.RepeatMode), typeof(Testumgebung.Domain.Definitions.RepeatResultPolicy),
            typeof(Testumgebung.Hardware.CommKind), typeof(Testumgebung.Domain.Definitions.ProgressPosition), typeof(Testumgebung.Domain.Code.PortDirection), typeof(VarType) };
        foreach (var type in types)
            foreach (var value in Enum.GetValues(type)) Assert.NotEmpty(Assert.IsType<string>(Forward(converter, value)));
        Assert.Equal("", Forward(converter, null));
        Assert.Equal("Unbekannt", Forward(converter, "Unbekannt"));
    }

    [Fact]
    public void Plot_conversion_preserves_coordinates_freezes_results_and_clamps_bars()
    {
        var points = Assert.IsType<PointCollection>(Forward(new PlotPointsConverter(), new[] { new PlotPoint(1, 2), new PlotPoint(3, 4) }));
        Assert.True(points.IsFrozen); Assert.Equal(new[] { new Point(1, 2), new Point(3, 4) }, points);
        Assert.Empty(Assert.IsType<PointCollection>(Forward(new PlotPointsConverter(), null)));
        Assert.Equal(new Thickness(60, 0, 0, 0), Forward(new DepthToMarginConverter(), 3));
        Assert.Equal(new Thickness(), Forward(new DepthToMarginConverter(), null));
        var bars = new BarHeightConverter();
        Assert.Equal(0d, Forward(bars, -1d)); Assert.Equal(0d, Forward(bars, null));
        Assert.Equal(80d, Forward(bars, 2d)); Assert.Equal(1d, Forward(bars, 0.001d));
        Assert.Equal(50d, Forward(bars, 0.5d, "100")); Assert.Equal(40d, Forward(bars, 0.5d, "invalid"));
        var colors = new SeriesBrushConverter();
        for (var i = 0; i < 12; i++) Assert.True(((Brush)Forward(colors, i)).IsFrozen);
        Assert.Same(Forward(colors, 0), Forward(colors, 6)); Assert.Same(Forward(colors, 0), Forward(colors, null));
    }

    [Fact]
    public void One_way_converters_cannot_write_back_into_the_model()
    {
        foreach (var type in typeof(RatingToBrushConverter).Assembly.GetTypes().Where(t => t.IsPublic && !t.IsAbstract
            && typeof(IValueConverter).IsAssignableFrom(t) && t != typeof(EnumEqualsConverter)))
        {
            var converter = (IValueConverter)Activator.CreateInstance(type)!;
            Assert.Same(Binding.DoNothing, converter.ConvertBack("invalid", typeof(object), null, CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void Opc_variants_preserve_supported_scalar_types_and_convert_numeric_widths()
    {
        var from = typeof(OpcUaClient).GetMethod("FromVariant", BindingFlags.Static | BindingFlags.NonPublic)!;
        var to = typeof(OpcUaClient).GetMethod("ToVariant", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var input in new object?[] { null, false, true, "text", (sbyte)-1, (byte)1, (short)-1, (ushort)1, -1, 1u, -1L, 1UL, 1f, 1d })
        {
            var actual = from.Invoke(null, [input]);
            if (input is sbyte or byte or short or ushort or int or uint or long or ulong) Assert.Equal(System.Convert.ToInt64(input), actual);
            else if (input is float or double) Assert.Equal(System.Convert.ToDouble(input), actual);
            else Assert.Equal(input, actual);
            var variant = Assert.IsType<Variant>(to.Invoke(null, [input]));
            Assert.Equal(input, variant.Value);
        }
        var date = new DateTime(2026, 10, 10);
        Assert.Equal(date.ToString(), from.Invoke(null, [date]));
        Assert.Equal(date, Assert.IsType<Variant>(to.Invoke(null, [date])).Value);
        Assert.Equal(-1L, from.Invoke(null, [ulong.MaxValue]));
    }
}
