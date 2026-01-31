using EndfieldGraph.Views;
using Microsoft.Extensions.Hosting;
using System.Windows;
using Wpf.Ui;

namespace EndfieldGraph.Services;

public class ApplicationHostService(IServiceProvider serviceProvider) : IHostedService
{
    private INavigationWindow? _navigationWindow;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await HandleActivationAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }

    private async Task HandleActivationAsync()
    {
        if (!Application.Current.Windows.OfType<MainWindow>().Any())
        {
            _navigationWindow = (serviceProvider.GetService(typeof(INavigationWindow)) as INavigationWindow)!;
            _navigationWindow!.ShowWindow();

            _ = _navigationWindow.Navigate(typeof(Views.Pages.BuildsPage));
        }

        await Task.CompletedTask;
    }
}
