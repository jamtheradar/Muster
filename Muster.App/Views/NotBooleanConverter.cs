using System.Globalization;
using System.Windows.Data;

namespace Muster.App.Views;

/// <summary>
/// Inverts a bool for binding. Used to disable a button while the work it starts is in flight,
/// where the view model already exposes the positive form and a second inverted property would be
/// two things to keep in step.
/// </summary>
public sealed class NotBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not bool flag || !flag;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not bool flag || !flag;
}
