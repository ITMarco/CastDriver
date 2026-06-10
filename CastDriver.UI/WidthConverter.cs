using System.Globalization;
using System.Windows.Data;

namespace CastDriver.UI;

// Multiplies a 0.0–1.0 float by the ConverterParameter to get a pixel width.
// Used to drive the audio level meter bar.
public sealed class WidthConverter : IValueConverter
{
    public static readonly WidthConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level     = value is float f ? f : 0f;
        var maxWidth  = parameter is string s && double.TryParse(s, out var d) ? d : 280.0;
        return Math.Max(0, level * maxWidth);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
