using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EndfieldGraph.Services;
using EndfieldGraph.ViewModels.Resource;
using EndfieldGraph.Views.Windows;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Data;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace EndfieldGraph.ViewModels;

public partial class ResourcesViewModel : ViewModel
{
    private readonly IResourcesStore store;
    private readonly IContentDialogService dialog;
    private readonly ISnackbarService snackbar;
    private readonly IResourceGraphBuilder graphBuilder;
    private readonly IServiceProvider services;

    private bool loadedOnce = false;
    private int suppressSaveDepth = 0;

    public ObservableCollection<ResourceItemViewModel> Resources { get; } = [];
    public IEnumerable<ResourceItemViewModel> InputResources => Resources
        .Where(r => r.Id != Selected?.Id);

    public ICollectionView ResourcesView { get; }

    [ObservableProperty]
    private ResourceItemViewModel? selected;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private bool sortAscending = true;

    [ObservableProperty] 
    private string graphWarning = "";

    public ResourcesViewModel(
        IResourcesStore store, 
        IContentDialogService dialog, 
        ISnackbarService snackbar,
        IResourceGraphBuilder graphBuilder,
        IServiceProvider services)
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
        if (obj is not ResourceItemViewModel r)
            return false;

        var q = SearchText?.Trim();
        if (string.IsNullOrWhiteSpace(q))
            return true;

        return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchTextChanged(string value)
    {
        ResourcesView.Refresh();
    }
    
    partial void OnSortAscendingChanged(bool value)
    {
        ApplySorting();
    }
    
    partial void OnSelectedChanged(ResourceItemViewModel? value)
    {
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

    [RelayCommand]
    private void ToggleSort()
    {
        SortAscending = !SortAscending;
    }

    [RelayCommand]
    private void AddResource()
    {
        var vm = new ResourceItemViewModel(
            Guid.NewGuid(), 
            string.IsNullOrWhiteSpace(SearchText) ? "_ New Resource" : SearchText, 
            DefaultIcons.ResourcePlaceholderPng
        );

        Resources.Add(vm);
        SearchText = "";
        Selected = vm;

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

        Selected.Inputs.Add(new ResourceInputViewModel(first.Id, 1));
        RecalcGraphWarning();
        RequestSave();
    }

    [RelayCommand(CanExecute = nameof(IsSelected))]
    private void RemoveInput(ResourceInputViewModel? input)
    {
        if (Selected is null || input is null) return;

        Selected.Inputs.Remove(input);
        RecalcGraphWarning();
        RequestSave();
    }

    [RelayCommand(CanExecute = nameof(IsSelected))]
    private void ShowInput(ResourceInputViewModel? input)
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
                    SecondaryButtonText = "Cancel",
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
        Selected = Resources.FirstOrDefault();

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

        var png = IconPngConverter.LoadAnyImageAsPngBytes(ofd.FileName);
        Selected.SetIcon(png);

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

    private bool IsGraph() => Selected is not null && string.IsNullOrWhiteSpace(GraphWarning);
    private bool IsInput() => Selected is not null && Resources?.Where(r => r.Id != Selected.Id && !Selected.Inputs.Any(i => i.Id == r.Id)).Count() > 0;
    private bool IsSelected() => Selected is not null;

    private void ApplySorting()
    {
        ResourcesView.SortDescriptions.Clear();
        ResourcesView.SortDescriptions.Add(
            new SortDescription(
                nameof(ResourceItemViewModel.Name),
                SortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending
            )
        );
        ResourcesView.Refresh();
    }

    private void OnResourcesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var it in e.OldItems.OfType<ResourceItemViewModel>())
            {
                it.PropertyChanged -= OnResourcePropertyChanged;
                it.Inputs.CollectionChanged -= OnInputsCollectionChanged;

                foreach (var input in it.Inputs)
                    input.PropertyChanged -= OnResourcePropertyChanged;
            }

        if (e.NewItems is not null)
            foreach (var it in e.NewItems.OfType<ResourceItemViewModel>())
            {
                it.PropertyChanged += OnResourcePropertyChanged;
                it.Inputs.CollectionChanged += OnInputsCollectionChanged;

                foreach (var input in it.Inputs)
                    input.PropertyChanged += OnResourcePropertyChanged;
            }

        RequestSave();
    }

    private void OnInputsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var i in e.OldItems.OfType<ResourceInputViewModel>())
                i.PropertyChanged -= OnResourcePropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (var i in e.NewItems.OfType<ResourceInputViewModel>())
                i.PropertyChanged += OnResourcePropertyChanged;
        }

        RequestSave();
    }

    private readonly string[] watched =
    [
        nameof(ResourceItemViewModel.Name),
        nameof(ResourceItemViewModel.Icon),
        nameof(ResourceItemViewModel.OutputQty),
        nameof(ResourceItemViewModel.CraftTimeSec),
        nameof(ResourceInputViewModel.Id),
        nameof(ResourceInputViewModel.Qty),
    ];

    private void OnResourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResourceItemViewModel.Name))
            ApplySorting();

        if (e.PropertyName is nameof(ResourceInputViewModel.Id))
            RecalcGraphWarning();

        if (watched.Contains(e.PropertyName))
            RequestSave();
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

            foreach (var r in store.Resources.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var vm = new ResourceItemViewModel(r.Id, r.Name, r.IconPngBytes)
                {
                    OutputQty = r.OutputQty,
                    CraftTimeSec = r.CraftTimeSec
                };

                foreach (var (id, qty) in r.Inputs)
                    vm.Inputs.Add(new ResourceInputViewModel(id, qty));

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
                    r.IconPngBytes,
                    r.OutputQty <= 0 ? 1 : r.OutputQty,
                    r.CraftTimeSec,
                    [.. r.Inputs.Where(i => i.Id != Guid.Empty && i.Qty > 0).Select(i => (i.Id, i.Qty))]
                ))
                .ToList();

            store.ApplySnapshot(records);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
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

    private void ShowError(string message)
    {
        snackbar.Show("Error", message, ControlAppearance.Danger,
            new SymbolIcon(SymbolRegular.ErrorCircle24),
            TimeSpan.FromSeconds(6)
        );
    }
}
