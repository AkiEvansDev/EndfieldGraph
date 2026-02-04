using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EndfieldGraph.Models;
using EndfieldGraph.Services;
using EndfieldGraph.Services.Data.Builds;
using EndfieldGraph.Services.Data.Resources;
using EndfieldGraph.Services.Helpers;
using EndfieldGraph.ViewModels.Build;
using EndfieldGraph.ViewModels.Common;
using EndfieldGraph.ViewModels.Resource;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace EndfieldGraph.ViewModels;

public partial class BuildsViewModel : BaseViewModel
{
    private readonly IResourcesStore resourcesStore;
    private readonly IBuildsStore buildsStore;
    private readonly ISnackbarService snackbar;
    private readonly IResourceGraphBuilder graphBuilder;
    private readonly IBuildCalculationService calc;

    private bool loadedOnce;
    private int suppressSaveDepth;

    public ObservableCollection<ResourceViewModel> AllResources { get; } = [];
    public ICollectionView AllResourcesView { get; }

    public ObservableCollection<BuildViewModel> Tabs { get; } = [];

    [ObservableProperty] private BuildViewModel? selected;
    [ObservableProperty] private ResourceGraphLayout graphLayout = new();
    [ObservableProperty] private BuildCalcResult? calculation;

    public BuildsViewModel(
        IResourcesStore resourcesStore,
        IBuildsStore buildsStore,
        ISnackbarService snackbar,
        IResourceGraphBuilder graphBuilder,
        IBuildCalculationService calc
    )
    {
        this.resourcesStore = resourcesStore;
        this.buildsStore = buildsStore;
        this.snackbar = snackbar;
        this.graphBuilder = graphBuilder;
        this.calc = calc;

        AllResourcesView = CollectionViewSource.GetDefaultView(AllResources);
        AllResourcesView.SortDescriptions.Add(new SortDescription(nameof(ResourceViewModel.Name), ListSortDirection.Ascending));

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
                await buildsStore.InitializeAsync();

                LoadResourcesFromStore();
                LoadTabsFromStore();

                if (Tabs.Count == 0)
                {
                    var t = new BuildViewModel(Guid.NewGuid(), "New Build");

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

    #region OnChanged

    partial void OnSelectedChanged(BuildViewModel? value)
    {
        RebuildGraphForSelected();
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (var t in e.NewItems.OfType<BuildViewModel>())
            {
                t.PropertyChanged += OnTabPropertyChanged;
                t.GoalsChanged += OnTabGoalsChanged;
            }

        if (e.OldItems is not null)
            foreach (var t in e.OldItems.OfType<BuildViewModel>())
            {
                t.PropertyChanged -= OnTabPropertyChanged;
                t.GoalsChanged -= OnTabGoalsChanged;
            }

        SaveTabsSnapshot();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e) 
        => SaveTabsSnapshot();

    private void OnTabGoalsChanged()
    {
        SaveTabsSnapshot();
        RebuildGraphForSelected();
    }

    #endregion
    #region Commands

    [RelayCommand]
    private void AddTab()
    {
        var t = new BuildViewModel(Guid.NewGuid(), "New Build");

        Tabs.Add(t);
        Selected = t;

        SaveTabsSnapshot();
    }

    [RelayCommand]
    private void DeleteTab(BuildViewModel? tab)
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
    private void RenameTab(BuildViewModel? tab)
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

        Selected.Goals.Add(new InputViewModel(first.Id, 1));
        SaveTabsSnapshot();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTab))]
    private void RemoveGoal(InputViewModel? goal)
    {
        if (Selected is null || goal is null) return;

        Selected.Goals.Remove(goal);
        SaveTabsSnapshot();
    }

    private bool HasSelectedTab() => Selected is not null;

    #endregion

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

        foreach (var r in resourcesStore.Items.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var vm = new ResourceViewModel(r.Id, r.Name, r.Icon)
            {
                Count = r.Count,
                Seconds = r.Seconds
            };

            foreach (var (id, count) in r.Inputs)
                vm.Inputs.Add(new InputViewModel(id, count));

            AllResources.Add(vm);
        }

        AllResourcesView.Refresh();
    }

    private void LoadTabsFromStore()
    {
        using (SuppressSaveScope())
        {
            Tabs.Clear();

            foreach (var rec in buildsStore.Items.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var tab = new BuildViewModel(rec.Id, string.IsNullOrWhiteSpace(rec.Name) ? "Build" : rec.Name);

                foreach (var (id, count) in rec.Inputs)
                    tab.Goals.Add(new InputViewModel(id, count));

                Tabs.Add(tab);
            }
        }
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
                    Count: 1,
                    Seconds: 0,
                    Inputs: [.. t.Goals
                        .Where(g => g.Id != Guid.Empty && g.Count > 0)
                        .Select(g => (g.Id, Math.Max(1, g.Count)))]
                ))
                .ToList();

            buildsStore.ApplySnapshot(records);
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
            Calculation = null;
            return;
        }

        if (Selected.Goals.Count == 0)
        {
            GraphLayout = new ResourceGraphLayout();
            Calculation = null;
            return;
        }

        var layouts = new List<ResourceGraphLayout>();

        foreach (var goal in Selected.Goals.Where(g => g.Id != Guid.Empty && g.Count > 0))
        {
            var root = AllResources.FirstOrDefault(r => r.Id == goal.Id);
            if (root is null) continue;

            var res = graphBuilder.BuildFor(root, AllResources, desiredRootUnits: Math.Max(1, goal.Count));
            if (res.HasCycle || res.Layout is null)
                continue;

            layouts.Add(res.Layout);
        }

        GraphLayout = graphBuilder.ComposeVertical(layouts, gapY: 160);

        var goals = Selected.Goals
            .Where(g => g.Id != Guid.Empty && g.Count > 0)
            .Select(g => new BuildGoalSpec(g.Id, g.Count))
            .ToList();

        Calculation = calc.Calculate(goals, AllResources);
    }

    private Scope SuppressSaveScope()
    {
        suppressSaveDepth++;
        return new Scope(() => suppressSaveDepth = Math.Max(0, suppressSaveDepth - 1));
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
