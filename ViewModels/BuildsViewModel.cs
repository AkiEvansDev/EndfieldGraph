using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EndfieldGraph.Services;
using EndfieldGraph.ViewModels.Resource;
using EndfieldGraph.Views.Controls;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace EndfieldGraph.ViewModels;

public partial class BuildsViewModel : ViewModel
{
    private readonly IResourcesStore resourcesStore;
    private readonly ITabsStore tabsStore;
    private readonly ISnackbarService snackbar;
    private readonly IResourceGraphBuilder graphBuilder;

    private bool loadedOnce;
    private int suppressSaveDepth;

    public ObservableCollection<ResourceItemViewModel> AllResources { get; } = [];
    public ICollectionView AllResourcesView { get; }

    public ObservableCollection<BuildTabViewModel> Tabs { get; } = [];

    [ObservableProperty] private BuildTabViewModel? selected;

    [ObservableProperty] private ResourceGraphLayout graphLayout = new();

    public BuildsViewModel(IResourcesStore resourcesStore, ITabsStore tabsStore, ISnackbarService snackbar, IResourceGraphBuilder graphBuilder)
    {
        this.resourcesStore = resourcesStore;
        this.tabsStore = tabsStore;
        this.snackbar = snackbar;
        this.graphBuilder = graphBuilder;

        AllResourcesView = CollectionViewSource.GetDefaultView(AllResources);
        AllResourcesView.SortDescriptions.Add(new SortDescription(nameof(ResourceItemViewModel.Name), ListSortDirection.Ascending));

        Tabs.CollectionChanged += OnTabsCollectionChanged;
    }

    public override async Task OnNavigatedToAsync()
    {
        await base.OnNavigatedToAsync();

        try
        {
            if (!loadedOnce)
            {
                loadedOnce = true;

                await resourcesStore.InitializeAsync();
                await tabsStore.InitializeAsync();

                LoadResourcesFromStore();
                LoadTabsFromStore();

                if (Tabs.Count == 0)
                {
                    var t = new BuildTabViewModel(Guid.NewGuid(), "New Build");

                    HookTab(t);
                    Tabs.Add(t);
                    Selected = t;

                    SaveTabsSnapshot();
                }
                else
                {
                    Selected = Tabs.FirstOrDefault();
                }
            }
            else
            {
                LoadResourcesFromStore();
                SyncTabsWithResources();

                if (Selected is not null)
                {
                    var cur = Selected;
                    Selected = null;
                    Selected = cur;
                }
            }

            RebuildGraphForSelected();
        }
        catch (Exception ex)
        {
            snackbar.Show("Error", ex.Message, ControlAppearance.Danger,
                new SymbolIcon(SymbolRegular.ErrorCircle24),
                TimeSpan.FromSeconds(6)
            );
        }
    }

    partial void OnSelectedChanged(BuildTabViewModel? value)
    {
        RebuildGraphForSelected();
    }

    private void SyncTabsWithResources()
    {
        if (Tabs.Count == 0) return;

        var valid = new HashSet<Guid>(AllResources.Where(r => r.Id != Guid.Empty).Select(r => r.Id));

        var changed = false;

        foreach (var tab in Tabs)
        {
            for (int i = tab.Goals.Count - 1; i >= 0; i--)
            {
                var g = tab.Goals[i];
                if (g.Id == Guid.Empty || !valid.Contains(g.Id))
                {
                    tab.Goals.RemoveAt(i);
                    changed = true;
                    continue;
                }
            }
        }

        if (changed)
            SaveTabsSnapshot();
    }

    private void LoadResourcesFromStore()
    {
        AllResources.Clear();

        foreach (var r in resourcesStore.Resources.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var vm = new ResourceItemViewModel(r.Id, r.Name, r.IconPngBytes)
            {
                OutputQty = r.OutputQty,
                CraftTimeSec = r.CraftTimeSec
            };

            foreach (var (id, qty) in r.Inputs)
                vm.Inputs.Add(new ResourceInputViewModel(id, qty));

            AllResources.Add(vm);
        }

        AllResourcesView.Refresh();
    }

    private void LoadTabsFromStore()
    {
        using (SuppressSaveScope())
        {
            Tabs.Clear();

            foreach (var rec in tabsStore.Resources.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var tab = new BuildTabViewModel(rec.Id, string.IsNullOrWhiteSpace(rec.Name) ? "Build" : rec.Name);

                foreach (var (id, qty) in rec.Inputs)
                    tab.Goals.Add(new ResourceInputViewModel(id, qty));

                HookTab(tab);
                Tabs.Add(tab);
            }
        }
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (var t in e.NewItems.OfType<BuildTabViewModel>())
                HookTab(t);

        if (e.OldItems is not null)
            foreach (var t in e.OldItems.OfType<BuildTabViewModel>())
                UnhookTab(t);

        SaveTabsSnapshot();
    }

    private void HookTab(BuildTabViewModel tab)
    {
        tab.PropertyChanged += OnTabPropertyChanged;
        tab.GoalsChanged += OnTabGoalsChanged;
    }

    private void UnhookTab(BuildTabViewModel tab)
    {
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.GoalsChanged -= OnTabGoalsChanged;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e) => SaveTabsSnapshot();
    private void OnTabGoalsChanged()
    {
        SaveTabsSnapshot();
        RebuildGraphForSelected();
    }

    [RelayCommand]
    private void AddTab()
    {
        var t = new BuildTabViewModel(Guid.NewGuid(), "New Build");

        Tabs.Add(t);
        Selected = t;

        SaveTabsSnapshot();
    }

    [RelayCommand]
    private void DeleteTab(BuildTabViewModel? tab)
    {
        if (tab is null) return;

        var wasSelected = ReferenceEquals(tab, Selected);
        Tabs.Remove(tab);

        if (Tabs.Count == 0)
            AddTab();
        else if (wasSelected)
            Selected = Tabs.FirstOrDefault();

        SaveTabsSnapshot();
    }

    [RelayCommand]
    private void RenameTab(BuildTabViewModel? tab)
    {
        var wasSelected = ReferenceEquals(tab, Selected);
        tab?.BeginRename();

        if (wasSelected)
            SaveTabsSnapshot();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTab))]
    private void AddGoal()
    {
        if (Selected is null) return;

        var taken = new HashSet<Guid>(Selected.Goals.Select(g => g.Id));
        var first = AllResources.FirstOrDefault(r => r.Id != Guid.Empty && !taken.Contains(r.Id));

        if (first is null)
            return;

        Selected.Goals.Add(new ResourceInputViewModel(first.Id, 1));
        SaveTabsSnapshot();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTab))]
    private void RemoveGoal(ResourceInputViewModel? goal)
    {
        if (Selected is null || goal is null) return;

        Selected.Goals.Remove(goal);
        SaveTabsSnapshot();
    }

    private bool HasSelectedTab() => Selected is not null;

    private Scope SuppressSaveScope()
    {
        suppressSaveDepth++;
        return new Scope(() => suppressSaveDepth = Math.Max(0, suppressSaveDepth - 1));
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private void SaveTabsSnapshot()
    {
        if (suppressSaveDepth > 0) return;

        try
        {
            var records = Tabs
                .Where(t => t.Id != Guid.Empty)
                .Select(t => new ResourceRecord(
                    t.Id,
                    (t.Name ?? "").Trim(),
                    DefaultIcons.ResourcePlaceholderPng,
                    OutputQty: 1,
                    CraftTimeSec: 0,
                    Inputs: [.. t.Goals
                        .Where(g => g.Id != Guid.Empty && g.Qty > 0)
                        .Select(g => (g.Id, Math.Max(1, g.Qty)))]
                ))
                .ToList();

            tabsStore.ApplySnapshot(records);
        }
        catch (Exception ex)
        {
            snackbar.Show("Error", ex.Message, ControlAppearance.Danger,
                new SymbolIcon(SymbolRegular.ErrorCircle24),
                TimeSpan.FromSeconds(6)
            );
        }
    }

    private void RebuildGraphForSelected()
    {
        if (Selected is null)
        {
            GraphLayout = new ResourceGraphLayout();
            return;
        }

        if (Selected.Goals.Count == 0)
        {
            GraphLayout = new ResourceGraphLayout();
            return;
        }

        var layouts = new List<ResourceGraphLayout>();

        foreach (var goal in Selected.Goals.Where(g => g.Id != Guid.Empty && g.Qty > 0))
        {
            var root = AllResources.FirstOrDefault(r => r.Id == goal.Id);
            if (root is null) continue;

            var res = graphBuilder.BuildFor(root, AllResources, desiredRootUnits: Math.Max(1, goal.Qty));
            if (res.HasCycle || res.Layout is null)
                continue;

            layouts.Add(res.Layout);
        }

        GraphLayout = graphBuilder.ComposeVertical(layouts, gapY: 160);
    }
}
