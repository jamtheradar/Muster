using System.Globalization;
using System.Windows.Data;

namespace Muster.App.Views;

/// <summary>
/// Scales a 0-to-1 level into the width of a meter bar, where the converter parameter is the
/// track's full width in pixels.
/// </summary>
/// <remarks>
/// The level arrives as an RMS amplitude and is drawn on a decibel scale over a 60 dB window,
/// which is not decoration: speech sits in the bottom tenth of a linear meter, so a linear bar
/// barely moves while someone talks and reads as a microphone that is not working — the exact
/// wrong answer from a control that exists to say whether it is.
/// </remarks>
public sealed class LevelToWidthConverter : IValueConverter
{
    /// <summary>The quietest level the meter shows at all, in dBFS.</summary>
    private const double Floor = -60;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var track = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            ? width
            : 48;

        if (value is not double level || level <= 0 || double.IsNaN(level))
        {
            return 0d;
        }

        var db = 20 * Math.Log10(Math.Min(level, 1));
        return Math.Clamp((db - Floor) / -Floor, 0, 1) * track;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("A meter is display only.");
}
