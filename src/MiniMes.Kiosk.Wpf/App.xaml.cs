using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MiniMes.Client.Core;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Realtime;
using MiniMes.Client.Core.Resources;

namespace MiniMes.Kiosk.Wpf;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = e.Args,
            ContentRootPath = AppContext.BaseDirectory, // appsettings.json next to the exe
        });

        builder.Services.AddMiniMesClientCore(builder.Configuration);
        // A kiosk is bolted to one station: refuse to start without it rather than fail at every scan.
        builder.Services.AddOptions<ClientOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.StationCode), "StationCode is required on a kiosk.");
        builder.Services.AddKioskViewModels();
        builder.Services.AddSingleton<IKioskFeedback, SystemSoundFeedback>();
        builder.Services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();   // M4: SignalR pushes -> UI thread
        builder.Services.AddSingleton<Navigation>();
        builder.Services.AddSingleton<INavigation>(sp => sp.GetRequiredService<Navigation>());
        builder.Services.AddSingleton<IKioskNavigation>(sp => sp.GetRequiredService<Navigation>());
        builder.Services.AddSingleton<MainWindow>();
        _host = builder.Build();

        try
        {
            await _host.StartAsync(); // ValidateOnStart: bad ApiBaseUrl / missing StationCode fail here
        }
        catch (OptionsValidationException ex)
        {
            MessageBox.Show(string.Format(Strings.App_ConfigError, string.Join(Environment.NewLine, ex.Failures)),
                Strings.Kiosk_WindowTitle, MessageBoxButton.OK, MessageBoxImage.Error);
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
