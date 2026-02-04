using EndfieldGraph.Services;
using EndfieldGraph.Services.Data;
using EndfieldGraph.Services.Data.Builds;
using EndfieldGraph.Services.Data.Resources;
using EndfieldGraph.ViewModels;
using EndfieldGraph.Views;
using EndfieldGraph.Views.Pages;
using EndfieldGraph.Views.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.DependencyInjection;

namespace EndfieldGraph;

public partial class App
{
    private static readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureAppConfiguration(c =>
        {
            var basePath =
                Path.GetDirectoryName(AppContext.BaseDirectory)
                ?? throw new DirectoryNotFoundException(
                    "Unable to find the base directory of the application."
                );
            _ = c.SetBasePath(basePath);
        })
        .ConfigureServices(
            (context, services) =>
            {
                _ = services.AddNavigationViewPageProvider();

                _ = services.AddHostedService<ApplicationHostService>();
                _ = services.AddSingleton<INavigationService, NavigationService>();
                _ = services.AddSingleton<IContentDialogService, ContentDialogService>();
                _ = services.AddSingleton<ISnackbarService, SnackbarService>();

                _ = services.AddSingleton<IArchiveCodec<ResourceRecord>, ResourcesArchiveCodec>();
                
                _ = services.AddSingleton<IArchiveService<ResourceRecord>>(sp =>
                    new ZipArchiveService<ResourceRecord>(sp.GetRequiredService<IArchiveCodec<ResourceRecord>>())
                );

                _ = services.AddSingleton<IResourcesStore, ResourcesStore>();
                _ = services.AddSingleton<IBuildsStore, BuildsStore>();

                _ = services.AddSingleton<IResourceGraphBuilder, ResourceGraphBuilder>();
                _ = services.AddSingleton<IBuildCalculationService, BuildCalculationService>();

                _ = services.AddSingleton<INavigationWindow, MainWindow>();
                _ = services.AddSingleton<MainWindowViewModel>();

                _ = services.AddSingleton<BuildsPage>();
                _ = services.AddSingleton<BuildsViewModel>();
                _ = services.AddSingleton<ResourcesPage>();
                _ = services.AddSingleton<ResourcesViewModel>();

                _ = services.AddTransient<GraphWindow>();

                _ = services.AddSingleton<IUpdateService, GitHubUpdateService>();
            }
        )
        .Build();

    public static IServiceProvider Services
    {
        get { return _host.Services; }
    }

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        await _host.StartAsync();
    }

    private async void OnExit(object sender, ExitEventArgs e)
    {
        await _host.StopAsync();

        _host.Dispose();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        try
        {
            var snackbar = Services.GetService<ISnackbarService>();

            snackbar?.Show(
                "Error",
                e.Exception.Message,
                ControlAppearance.Danger,
                new SymbolIcon(SymbolRegular.ErrorCircle24),
                TimeSpan.FromSeconds(6)
            );
        }
        catch
        {

        }
    }
}
