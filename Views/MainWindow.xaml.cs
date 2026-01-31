using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EndfieldGraph.Views;

public partial class MainWindow : INavigationWindow
{
    public ViewModels.MainWindowViewModel ViewModel { get; }

    public MainWindow(ViewModels.MainWindowViewModel viewModel, INavigationService navigationService, IContentDialogService dialogService, ISnackbarService snackbarService)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        navigationService.SetNavigationControl(RootNavigation);
        dialogService.SetDialogHost(RootContentDialogHost);
        snackbarService.SetSnackbarPresenter(SnackbarPresenter);
    }

    public INavigationView GetNavigation() => RootNavigation;

    public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);

    public void SetPageService(INavigationViewPageProvider navigationViewPageProvider)
        => RootNavigation.SetPageProviderService(navigationViewPageProvider);

    public void ShowWindow() => Show();

    public void CloseWindow() => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        Application.Current.Shutdown();
    }

    public void SetServiceProvider(IServiceProvider serviceProvider) { }

    private void Navigation_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            RootNavigation.Focus();
            Keyboard.ClearFocus();
        }));
    }
}