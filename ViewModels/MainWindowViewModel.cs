using CommunityToolkit.Mvvm.ComponentModel;
using EndfieldGraph.Services;
using EndfieldGraph.ViewModels.Common;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;

namespace EndfieldGraph.ViewModels;

public partial class MainWindowViewModel : BaseViewModel
{
    private bool isInitialized = false;

    [ObservableProperty] private string applicationTitle = string.Empty;
    [ObservableProperty] private ObservableCollection<object> navigationItems = [];
    [ObservableProperty] private ObservableCollection<object> navigationFooter = [];

    public MainWindowViewModel(IUpdateService updateService)
    {
        if (!isInitialized)
            InitializeViewModel();

        _ = updateService.CheckForUpdatesAsync();
    }

    private void InitializeViewModel()
    {
        var v = VersionUtil.GetCurrentVersion();
        ApplicationTitle = $"Endfield Graph v{v.Major}.{v.Minor}";

        NavigationItems =
        [
            new NavigationViewItem()
            {
                Content = "Builds",
                Icon = new SymbolIcon { Symbol = SymbolRegular.BoxMultipleCheckmark24 },
                TargetPageType = typeof(Views.Pages.BuildsPage),
            },
        ];

        NavigationFooter =
        [
            new NavigationViewItem()
            {
                Content = "Resources",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Box24 },
                TargetPageType = typeof(Views.Pages.ResourcesPage),
            },
        ];

        isInitialized = true;
    }
}
