using EndfieldGraph.ViewModels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui.Abstractions.Controls;

namespace EndfieldGraph.Views.Pages;

public partial class ResourcesPage : INavigableView<ResourcesViewModel>
{
    public ResourcesViewModel ViewModel { get; }

    public ResourcesPage(ResourcesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    private void OnMoreButtonClick(object sender, RoutedEventArgs e)
    {
        MoreFlyout.IsOpen = !MoreFlyout.IsOpen;
    }

    private void OnTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Focus();
            Keyboard.ClearFocus();
        }));
    }

    private void OnPageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.RightButton == MouseButtonState.Pressed)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Focus();
            Keyboard.ClearFocus();
        }));
    }
}
