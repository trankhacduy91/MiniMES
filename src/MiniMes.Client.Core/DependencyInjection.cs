using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.ViewModels;

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

        // Transient: a fresh ViewModel each time a screen opens, no stale state from the last user.
        services.AddTransient<UserLoginViewModel>();
        services.AddTransient<OperatorLoginViewModel>();
        services.AddTransient<HomeViewModel>();
        return services;
    }
}
