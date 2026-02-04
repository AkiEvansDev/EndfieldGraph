using CommunityToolkit.Mvvm.ComponentModel;

namespace EndfieldGraph.ViewModels.Common;

public partial class InputViewModel(Guid id, int count) : ObservableObject
{
    [ObservableProperty] private Guid id = id;
    [ObservableProperty] private int count = count;
}
