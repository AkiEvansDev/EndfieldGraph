using CommunityToolkit.Mvvm.ComponentModel;

namespace EndfieldGraph.ViewModels.Resource;

public partial class ResourceInputViewModel(Guid id, int qty) : ObservableObject
{
    [ObservableProperty] private Guid id = id;
    [ObservableProperty] private int qty = qty;
}
