using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Controls;

namespace EndfieldGraph.Views.Pages;

public partial class BuildsPage : INavigableView<ViewModels.BuildsViewModel>
{
    public ViewModels.BuildsViewModel ViewModel { get; }

    public BuildsPage(ViewModels.BuildsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
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

    private void OnTabRenameBoxIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.Visibility != Visibility.Visible) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            tb.Focus();
            tb.SelectAll();
        }));
    }

    private void OnTabRenameBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.DataContext is not ViewModels.BuildTabViewModel tab) return;

        tab.CommitRename();
    }

    private void OnTabRenameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.DataContext is not ViewModels.BuildTabViewModel tab) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            tab.CommitRename();
            Keyboard.ClearFocus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            tab.CancelRename();
            Keyboard.ClearFocus();
        }
    }
}
