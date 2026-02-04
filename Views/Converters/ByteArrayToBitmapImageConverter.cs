using EndfieldGraph.Services.Helpers;
using System.Globalization;
using System.Windows.Data;

namespace EndfieldGraph.Views.Converters;

public sealed class ByteArrayToBitmapImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is byte[] bytes && bytes.Length > 0)
        {
            try
            {
                return IconPngConverter.ToBitmapImage(bytes);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
