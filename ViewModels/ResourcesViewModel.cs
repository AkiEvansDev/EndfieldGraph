using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EndfieldGraph.Models;
using EndfieldGraph.Services;
using EndfieldGraph.Services.Data;
using EndfieldGraph.Services.Data.Resources;
using EndfieldGraph.Services.Helpers;
using EndfieldGraph.ViewModels.Build;
using EndfieldGraph.ViewModels.Common;
using EndfieldGraph.ViewModels.Resource;
using EndfieldGraph.Views.Windows;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace EndfieldGraph.ViewModels;

public partial class ResourcesViewModel : BaseViewModel
{
    private readonly IResourcesStore store;
    private readonly IContentDialogService dialog;
    private readonly ISnackbarService snackbar;
    private readonly IResourceGraphBuilder graphBuilder;
    private readonly IServiceProvider services;

    private bool loadedOnce = false;
    private int suppressSaveDepth = 0;

    public ObservableCollection<ResourceViewModel> Resources { get; } = [];
    public IEnumerable<ResourceViewModel> InputResources => Resources
        .Where(r => r.Id != Selected?.Id);

    public ICollectionView ResourcesView { get; }

    [ObservableProperty] private ResourceViewModel? selected;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool sortAscending = true;
    [ObservableProperty] private string graphWarning = "";

    public ResourcesViewModel(
        IResourcesStore store,
        IContentDialogService dialog,
        ISnackbarService snackbar,
        IResourceGraphBuilder graphBuilder,
        IServiceProvider services
    )
    {
        this.store = store;
        this.dialog = dialog;
        this.snackbar = snackbar;
        this.graphBuilder = graphBuilder;
        this.services = services;

        ResourcesView = CollectionViewSource.GetDefaultView(Resources);
        ResourcesView.Filter = FilterResource;
        ApplySorting();

        Resources.CollectionChanged += OnResourcesCollectionChanged;
    }

    public override async Task OnNavigatedToAsync()
    {
        await base.OnNavigatedToAsync();

        if (loadedOnce) return;
        loadedOnce = true;

        try
        {
            await store.InitializeAsync();
            LoadFromStore();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private bool FilterResource(object obj)
    {
        if (obj is not ResourceViewModel r)
            return false;

        if (r.Id == Selected?.Id)
            return true;

        var q = SearchText?.Trim();
        if (string.IsNullOrWhiteSpace(q))
            return true;

        return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    #region OnChanged

    partial void OnSearchTextChanged(string value)
    {
        ResourcesView.Refresh();
    }

    partial void OnSortAscendingChanged(bool value)
    {
        ApplySorting();
    }

    partial void OnSelectedChanged(ResourceViewModel? value)
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
            ResourcesView.Refresh();

        DeleteSelectedCommand.NotifyCanExecuteChanged();
        PickIconCommand.NotifyCanExecuteChanged();
        AddInputCommand.NotifyCanExecuteChanged();

        RecalcGraphWarning();
        ViewGraphCommand.NotifyCanExecuteChanged();
    }

    partial void OnGraphWarningChanged(string value)
    {
        ViewGraphCommand.NotifyCanExecuteChanged();
    }

    private void OnResourcesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (var r in e.NewItems.OfType<ResourceViewModel>())
            {
                r.PropertyChanged += OnResourcePropertyChanged;
                r.InputsChanged += RequestSave;
            }

        if (e.OldItems is not null)
            foreach (var r in e.OldItems.OfType<ResourceViewModel>())
            {
                r.PropertyChanged -= OnResourcePropertyChanged;
                r.InputsChanged -= RequestSave;
            }

        RequestSave();
    }

    private void OnResourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
        => RequestSave();

    #endregion
    #region Commands

    [RelayCommand]
    private void ToggleSort()
    {
        SortAscending = !SortAscending;
    }

    [RelayCommand]
    private void AddResource()
    {
        var search = SearchText;
        SearchText = "";

        var vm = new ResourceViewModel(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(search) ? "_ New Resource" : search,
            DefaultIcons.ResourcePlaceholderPng
        );

        Resources.Add(vm);
        Selected = vm;

        SearchText = search;

        ApplySorting();
        RequestSave();
    }

    [RelayCommand(CanExecute = nameof(IsInput))]
    private void AddInput()
    {
        if (Selected is null) return;

        var first = Resources.FirstOrDefault(r => r.Id != Selected.Id && !Selected.Inputs.Any(i => i.Id == r.Id));
        if (first is null)
            return;

        Selected.Inputs.Add(new InputViewModel(first.Id, 1));

        RecalcGraphWarning();
        RequestSave();
    }

    [RelayCommand(CanExecute = nameof(IsSelected))]
    private void RemoveInput(InputViewModel? input)
    {
        if (Selected is null || input is null) return;

        Selected.Inputs.Remove(input);

        RecalcGraphWarning();
        RequestSave();
    }

    [RelayCommand(CanExecute = nameof(IsSelected))]
    private void ShowInput(InputViewModel? input)
    {
        if (input is null) return;

        Selected = Resources.FirstOrDefault(r => r.Id == input.Id);
    }

    [RelayCommand(CanExecute = nameof(IsSelected))]
    private async Task DeleteSelected()
    {
        if (Selected is null)
            return;

        var removed = Selected;
        var removedId = Selected.Id;

        var usageCount = Resources
            .Where(r => r.Id != removedId)
            .Sum(r => r.Inputs.Count(i => i.Id == removedId));

        if (usageCount > 0)
        {
            var result = await dialog.ShowAsync(
                new ContentDialog
                {
                    Title = "Remove resource?",
                    Content = $"Resource is used in {usageCount} recipe(s). Do you want to remove it anyway?",
                    PrimaryButtonText = "Remove",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Secondary
                },
                default
            );

            if (result != ContentDialogResult.Primary)
                return;
        }

        foreach (var r in Resources)
        {
            if (r.Id == removedId)
                continue;

            var toRemove = r.Inputs
                .Where(i => i.Id == removedId)
                .ToList();

            foreach (var input in toRemove)
                r.Inputs.Remove(input);
        }

        Resources.Remove(removed);
        Selected = ResourcesView.Cast<ResourceViewModel>().FirstOrDefault();

        RequestSave();
    }

    [RelayCommand(CanExecute = nameof(IsSelected))]
    private void PickIcon()
    {
        if (Selected is null)
            return;

        var ofd = new OpenFileDialog
        {
            Title = "Choose icon",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.ico|All files|*.*"
        };

        if (ofd.ShowDialog() != true)
            return;

        Selected.Icon = IconPngConverter.LoadAnyImageAsPngBytes(ofd.FileName);

        RequestSave();
    }

    [RelayCommand(CanExecute = nameof(IsGraph))]
    private void ViewGraph()
    {
        if (Selected is null) return;

        var result = graphBuilder.BuildFor(Selected, Resources, desiredRootUnits: 1);
        if (result.HasCycle)
        {
            RecalcGraphWarning();
            return;
        }

        if (result.Layout is null) return;

        var w = services.GetService(typeof(GraphWindow)) as GraphWindow ?? new GraphWindow();

        w.Title = $"Graph: {Selected.Name}";
        w.SetLayout(result.Layout);
        w.Show();
    }

    private bool IsGraph() => Selected is not null && string.IsNullOrWhiteSpace(GraphWarning);
    private bool IsInput() => Selected is not null && Resources?.Where(r => r.Id != Selected.Id && !Selected.Inputs.Any(i => i.Id == r.Id)).Count() > 0;
    private bool IsSelected() => Selected is not null;

    #endregion

    private void RecalcGraphWarning()
    {
        GraphWarning = "";
        if (Selected is null) return;

        var res = graphBuilder.BuildFor(Selected, Resources, desiredRootUnits: 1);
        if (res.HasCycle && res.Cycle is not null)
        {
            var names = res.Cycle.Path
                .Select(id => Resources.FirstOrDefault(r => r.Id == id)?.Name ?? id.ToString("N"))
                .ToList();

            GraphWarning = "Cycle detected: " + string.Join(" → ", names);
        }
    }

    private void ApplySorting()
    {
        ResourcesView.SortDescriptions.Clear();
        ResourcesView.SortDescriptions.Add(
            new SortDescription(
                nameof(ResourceViewModel.Name),
                SortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending
            )
        );
        ResourcesView.Refresh();
    }

    private void RequestSave()
    {
        if (suppressSaveDepth > 0)
            return;

        ApplyChangesToStore();
        AddInputCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var sfd = new SaveFileDialog
        {
            Title = "Export resources",
            Filter = "Endfield resources|*.egres|Zip|*.zip",
            FileName = "resources.egres"
        };

        if (sfd.ShowDialog() != true)
            return;

        try
        {
            ApplyChangesToStore();
            await store.ExportAsync(sfd.FileName);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportReplaceAsync()
    {
        await ImportInternalAsync(ImportMode.ReplaceAll);
    }

    [RelayCommand]
    private async Task ImportMergeAsync()
    {
        await ImportInternalAsync(ImportMode.MergeById);
    }

    private async Task ImportInternalAsync(ImportMode mode)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Import resources",
            Filter = "Endfield resources|*.egres;*.zip|All files|*.*"
        };

        if (ofd.ShowDialog() != true)
            return;

        try
        {
            await store.ImportAsync(ofd.FileName, mode);
            LoadFromStore();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void LoadFromStore()
    {
        using (SuppressSaveScope())
        {
            Resources.Clear();

            foreach (var r in store.Items.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var vm = new ResourceViewModel(r.Id, r.Name, r.Icon)
                {
                    Count = r.Count,
                    Seconds = r.Seconds
                };

                foreach (var (id, count) in r.Inputs)
                    vm.Inputs.Add(new InputViewModel(id, count));

                Resources.Add(vm);
            }

            Selected = Resources.FirstOrDefault();
            ResourcesView.Refresh();
        }
    }

    private void ApplyChangesToStore()
    {
        try
        {
            var records = Resources
                .Select(r => new ResourceRecord(
                    r.Id,
                    r.Name.Trim(),
                    r.Icon,
                    r.Count <= 0 ? 1 : r.Count,
                    r.Seconds,
                    [.. r.Inputs.Where(i => i.Id != Guid.Empty && i.Count > 0).Select(i => (i.Id, i.Count))]
                ))
                .ToList();

            store.ApplySnapshot(records);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        snackbar.Show("Error", message, ControlAppearance.Danger,
            new SymbolIcon(SymbolRegular.ErrorCircle24),
            TimeSpan.FromSeconds(6)
        );
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
