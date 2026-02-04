using CommunityToolkit.Mvvm.ComponentModel;
using EndfieldGraph.ViewModels.Common;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace EndfieldGraph.ViewModels.Resource;

public partial class ResourceViewModel : ObservableObject
{
    public event Action? InputsChanged;

    public Guid Id { get; }

    [ObservableProperty] private string name;
    [ObservableProperty] private byte[] icon;

    [ObservableProperty] private int count = 1;
    [ObservableProperty] private int seconds = 2;

    public ObservableCollection<InputViewModel> Inputs { get; } = [];

    public ResourceViewModel(Guid id, string name, byte[] icon)
    {
        Id = id;
        this.name = name;
        this.icon = icon;

        Inputs.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (var it in e.NewItems.OfType<InputViewModel>())
                    it.PropertyChanged += OnInputPropertyChanged;

            if (e.OldItems is not null)
                foreach (var it in e.OldItems.OfType<InputViewModel>())
                    it.PropertyChanged -= OnInputPropertyChanged;

            InputsChanged?.Invoke();
        };

        foreach (var i in Inputs)
            i.PropertyChanged += OnInputPropertyChanged;
    }

    private void OnInputPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => InputsChanged?.Invoke();
}
