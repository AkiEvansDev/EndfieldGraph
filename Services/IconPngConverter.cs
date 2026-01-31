using System.IO;
using System.Windows.Media.Imaging;

namespace EndfieldGraph.Services;

public static class IconPngConverter
{
    public static byte[] LoadAnyImageAsPngBytes(string filePath)
    {
        var bitmap = new BitmapImage();

        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();

        return ToPngBytes(bitmap);
    }

    public static byte[] ToPngBytes(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static BitmapImage ToBitmapImage(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var img = new BitmapImage();

        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();

        return img;
    }
}
