using System.Globalization;

namespace RouteWeatherPlanner.Converters;

/// <summary>Flips a bool — handy for e.g. binding IsEnabled to the inverse of IsBusy.</summary>
public class InvertedBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}
