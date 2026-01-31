using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Abstractions.Controls;

namespace EndfieldGraph.ViewModels;

public abstract class ViewModel : ObservableObject, INavigationAware
{
    public virtual Task OnNavigatedToAsync()
    {
        OnNavigatedTo();

        return Task.CompletedTask;
    }

    public virtual void OnNavigatedTo() { }

    public virtual Task OnNavigatedFromAsync()
    {
        OnNavigatedFrom();

        return Task.CompletedTask;
    }

    public virtual void OnNavigatedFrom() { }
}
