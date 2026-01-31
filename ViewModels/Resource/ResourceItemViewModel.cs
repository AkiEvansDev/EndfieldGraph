using CommunityToolkit.Mvvm.ComponentModel;
using EndfieldGraph.Services;
using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;

namespace EndfieldGraph.ViewModels.Resource;

public partial class ResourceItemViewModel(Guid id, string name, byte[] iconPngBytes) : ObservableObject
{
    public Guid Id { get; } = id;

    [ObservableProperty] private string name = name;
    [ObservableProperty] private BitmapImage icon = IconPngConverter.ToBitmapImage(iconPngBytes);

    [ObservableProperty] private int outputQty = 1;
    [ObservableProperty] private int craftTimeSec = 2;

    public byte[] IconPngBytes { get; private set; } = iconPngBytes;
    
    public ObservableCollection<ResourceInputViewModel> Inputs { get; } = [];

    public void SetIcon(byte[] newPngBytes)
    {
        IconPngBytes = newPngBytes;
        Icon = IconPngConverter.ToBitmapImage(newPngBytes);
    }
}
