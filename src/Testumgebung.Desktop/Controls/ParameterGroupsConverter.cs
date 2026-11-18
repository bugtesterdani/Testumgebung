using System.Collections;
using System.Globalization;
using System.Windows.Data;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Controls;

/// <summary>Each properties panel receives its own grouped view; parameter values remain on the original rows.</summary>
public sealed class ParameterGroupsConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IEnumerable rows) return null;
        var view = new ListCollectionView(rows.Cast<ParameterRowViewModel>().ToArray());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ParameterRowViewModel.Section)));
        return view;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
