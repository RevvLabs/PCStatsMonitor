using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace PCStatsMonitor.App.Controls;

/// <summary>
/// Converts a hex color string (e.g. "00BCD4") to Avalonia Color — or a
/// SolidColorBrush when the target property expects IBrush (e.g. Border.Background).
/// Used in XAML to pass accent colors via Tag without boxing allocations.
/// </summary>
public sealed class HexColorConverter : IValueConverter
{
    public static readonly HexColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        Color color = Colors.Cyan;
        if (value is string hex && hex.Length == 6
            && uint.TryParse(hex, NumberStyles.HexNumber, null, out uint rgb))
        {
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        return typeof(IBrush).IsAssignableFrom(targetType)
            ? new SolidColorBrush(color)
            : color;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
