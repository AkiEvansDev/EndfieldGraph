using System.Windows.Media.Imaging;

namespace EndfieldGraph.Services;

public static class DefaultIcons
{
    private static byte[]? _resourcePlaceholderPng;

    public static byte[] ResourcePlaceholderPng
        => _resourcePlaceholderPng ??= LoadPngFromResource("pack://application:,,,/Assets/Icons/resource_placeholder.png");

    private static byte[] LoadPngFromResource(string uri)
    {
        var bitmap = new BitmapImage();

        bitmap.BeginInit();
        bitmap.UriSource = new Uri(uri, UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();

        return IconPngConverter.ToPngBytes(bitmap);
    }
}
