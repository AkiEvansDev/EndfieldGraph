using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;

namespace EndfieldGraph.ViewModels;

public partial class MainWindowViewModel : ViewModel
{
    private bool isInitialized = false;

    [ObservableProperty]
    private string applicationTitle = string.Empty;

    [ObservableProperty]
    private ObservableCollection<object> navigationItems = [];

    [ObservableProperty]
    private ObservableCollection<object> navigationFooter = [];

    public MainWindowViewModel()
    {
        if (!isInitialized)
            InitializeViewModel();
    }

    private void InitializeViewModel()
    {
        ApplicationTitle = "Endfield Graph 0.1";

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
