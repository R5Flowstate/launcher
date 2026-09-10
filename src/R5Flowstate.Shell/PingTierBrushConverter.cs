using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace R5Flowstate.Shell;

/// <summary>Latency band to colour, resolved from the theme so it tracks the palette.</summary>
public sealed class PingTierBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string switch
        {
            "Good" => "Accent",
            "Fair" => "PingFair",
            "Poor" => "PingPoor",
            _ => "TextMuted",
        };

        return Application.Current?.TryFindResource(key) as Brush
            ?? (Brush)Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
