using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MiniMes.Client.Core;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;

namespace MiniMes.BackOffice.Wpf;

// WPF has no built-in DI: build the same Generic Host ASP.NET Core uses (DI, appsettings.json, logging).
public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Content root = exe folder, so appsettings.json is found even when started from another folder.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = e.Args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Services.AddMiniMesClientCore(builder.Configuration);
        builder.Services.AddSingleton<Navigation>();
        builder.Services.AddSingleton<INavigation>(sp => sp.GetRequiredService<Navigation>());
        builder.Services.AddSingleton<MainWindow>();
        _host = builder.Build();

        try
        {
            await _host.StartAsync(); // runs ValidateOnStart: bad ApiBaseUrl fails here, not at first click
        }
        catch (OptionsValidationException ex)
        {
            MessageBox.Show(string.Format(Strings.App_ConfigError, string.Join(Environment.NewLine, ex.Failures)),
                Strings.BackOffice_Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var navigation = _host.Services.GetRequiredService<Navigation>();
        var window = _host.Services.GetRequiredService<MainWindow>();
        window.DataContext = navigation;
        navigation.ShowLogin();
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
