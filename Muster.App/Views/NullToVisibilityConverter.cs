using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Muster.App.Views;

/// <summary>
/// Null collapses, anything else shows. Used for the master-detail panes in the settings screen,
/// where "nothing selected" and "nothing to show" are the same thing.
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("One way only.");
}
