namespace EndfieldGraph.Services.Helpers;

public static class DefaultIcons
{
    private static byte[]? _resourcePlaceholderPng;
    public static byte[] ResourcePlaceholderPng => _resourcePlaceholderPng 
        ??= IconPngConverter.LoadAnyImageAsPngBytes("pack://application:,,,/Assets/Icons/resource_placeholder.png");
}
