using CommunityToolkit.Mvvm.ComponentModel;
using EndfieldGraph.ViewModels.Resource;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace EndfieldGraph.ViewModels;

public partial class BuildTabViewModel : ObservableObject
{
    public event Action? GoalsChanged;

    public Guid Id { get; }

    [ObservableProperty] private string name;
    [ObservableProperty] private bool isRenaming;
    [ObservableProperty] private string renameText = "";

    public ObservableCollection<ResourceInputViewModel> Goals { get; } = [];

    public BuildTabViewModel(Guid id, string name)
    {
        Id = id;
        this.name = name;

        Goals.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (var it in e.NewItems.OfType<ResourceInputViewModel>())
                    it.PropertyChanged += OnGoalPropertyChanged;

            if (e.OldItems is not null)
                foreach (var it in e.OldItems.OfType<ResourceInputViewModel>())
                    it.PropertyChanged -= OnGoalPropertyChanged;

            GoalsChanged?.Invoke();
        };

        foreach (var g in Goals)
            g.PropertyChanged += OnGoalPropertyChanged;
    }

    private void OnGoalPropertyChanged(object? sender, PropertyChangedEventArgs e) => GoalsChanged?.Invoke();

    public void BeginRename()
    {
        RenameText = Name;
        IsRenaming = true;
    }

    public void CommitRename()
    {
        var t = (RenameText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(t))
            t = "Build";

        Name = t;
        IsRenaming = false;
    }

    public void CancelRename()
    {
        RenameText = Name;
        IsRenaming = false;
    }
}
