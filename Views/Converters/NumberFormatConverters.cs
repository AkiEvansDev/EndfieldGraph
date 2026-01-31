using System.Globalization;
using System.Windows.Data;

namespace EndfieldGraph.Views.Converters;

public sealed class PerMinConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value is double d ? d : 0;
        return v >= 100 ? $"{Math.Round(v):0}/m" : $"{v:0.0}/m";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class CraftsPerMinConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value is double d ? d : 0;
        return v >= 100 ? $"{Math.Round(v):0}/m" : $"{v:0.00}/m";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
