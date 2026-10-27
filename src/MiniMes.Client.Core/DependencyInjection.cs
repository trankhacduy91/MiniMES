using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Realtime;
using MiniMes.Client.Core.ViewModels;
using MiniMes.Client.Core.ViewModels.Execution;
using MiniMes.Client.Core.ViewModels.Kiosk;
using MiniMes.Client.Core.ViewModels.Machines;
using MiniMes.Client.Core.ViewModels.MasterData;
using MiniMes.Client.Core.ViewModels.Planning;

namespace MiniMes.Client.Core;

public static class DependencyInjection
{
    // Shared by BackOffice and Kiosk; each app adds its own Views and INavigation.
    public static IServiceCollection AddMiniMesClientCore(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<ClientOptions>()
            .Bind(config)
            .Validate(o => Uri.TryCreate(o.ApiBaseUrl, UriKind.Absolute, out _), "ApiBaseUrl must be an absolute URL.")
            .ValidateOnStart();

        services.AddSingleton<ITokenStore, TokenStore>(); // one session per app
        services.AddTransient<AuthHandler>();
        services.AddHttpClient<ApiClient>((sp, http) =>
            {
                var o = sp.GetRequiredService<IOptions<ClientOptions>>().Value;
                // Trailing slash so relative URLs ("api/...") append instead of replacing the last segment.
                http.BaseAddress = new Uri(o.ApiBaseUrl.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            })
            .AddHttpMessageHandler<AuthHandler>();

        services.AddTransient<IAuthClient, AuthClient>();

        // M4: live machine data. Each app registers its IUiDispatcher (WPF Dispatcher).
        services.AddSingleton<IRealtimeClientFactory, RealtimeClientFactory>();
        services.AddTransient<IMachineApi, MachineApi>();

        // Transient: a fresh ViewModel each time a screen opens, no stale state from the last user.
        services.AddTransient<UserLoginViewModel>();
        services.AddTransient<OperatorLoginViewModel>();
        services.AddTransient<HomeViewModel>();
        return services;
    }

    // BackOffice only: the Kiosk has no master data screens and no IDialogService, and DI validation
    // (ValidateOnBuild in Development) would fail on ViewModels it cannot build.
    public static IServiceCollection AddBackOfficeViewModels(this IServiceCollection services)
    {
        services.AddTransient<IMasterDataApi, MasterDataApi>();
        services.TryAddSingleton(TimeProvider.System); // "this year" of the plant calendar; tests use a fake
        services.AddSingleton<IPageNavigator, PageNavigator>(); // one content area; the shell clears it on logout

        services.AddTransient<BackOfficeShellViewModel>();
        services.AddTransient<ItemListViewModel>();
        services.AddTransient<ItemEditViewModel>();
        services.AddTransient<BomViewModel>();
        services.AddTransient<RoutingEditorViewModel>();
        services.AddTransient<WorkCentersViewModel>();
        services.AddTransient<MachinesViewModel>();
        services.AddTransient<ShiftsViewModel>();
        services.AddTransient<PlantCalendarViewModel>();
        services.AddTransient<DefectCodesViewModel>();
        services.AddTransient<DowntimeReasonsViewModel>();
        services.AddTransient<ImportViewModel>();

        // M2: work orders
        services.AddTransient<IWorkOrderApi, WorkOrderApi>();
        services.AddTransient<WorkOrderListViewModel>();
        services.AddTransient<WorkOrderCreateViewModel>();
        services.AddTransient<WorkOrderDetailViewModel>();

        // M3: production reports
        services.AddTransient<IReportApi, ReportApi>();
        services.AddTransient<ExecutionReportViewModel>();

        // M4: line dashboard
        services.AddTransient<LineDashboardViewModel>();
        return services;
    }

    // Kiosk only (M3): the station screens. The app registers IKioskNavigation and IKioskFeedback itself.
    public static IServiceCollection AddKioskViewModels(this IServiceCollection services)
    {
        services.AddTransient<IExecutionApi, ExecutionApi>();
        services.AddTransient<IWorkOrderApi, WorkOrderApi>();
        services.AddSingleton<IKioskContext, KioskContext>(); // state, not a ViewModel; cleared at logout
        services.TryAddSingleton(TimeProvider.System);        // flash duration, scan timeout; tests use a fake

        services.AddTransient<StationSelectViewModel>();
        services.AddTransient<ScanViewModel>();
        services.AddTransient<RepairViewModel>();
        services.AddTransient<AndonPanelViewModel>();   // M4: part of the scan screen
        return services;
    }
}
