using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Application.Audit;
using MiniMes.Application.Auth;
using MiniMes.Application.Common;
using MiniMes.Application.Factory;
using MiniMes.Infrastructure.Audit;
using MiniMes.Domain.Users;
using MiniMes.Infrastructure.Auth;
using MiniMes.Infrastructure.Factory;
using MiniMes.Infrastructure.Persistence;
using MiniMes.Infrastructure.Persistence.Interceptors;

namespace MiniMes.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured (user-secrets or env var).");

        // Scoped like the DbContext: the interceptor keeps per-save state (pending Added entries, own transaction).
        services.AddScoped<AuditSaveChangesInterceptor>();
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>()); // same instance per request

        // Compatibility level 160 = SQL Server 2022, so EF does not emit newer syntax.
        // No EnableRetryOnFailure: the retrying strategy rejects the transaction the interceptor opens.
        services.AddDbContext<AppDbContext>((sp, o) => o
            .UseSqlServer(cs, sql => sql.UseCompatibilityLevel(160))
            .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));

        // Missing or short key = the app refuses to start, instead of failing at first login.
        services.AddOptions<JwtOptions>()
            .Bind(config.GetSection(JwtOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Key) && o.Key.Length >= 32, "Jwt:Key must be at least 32 characters.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
        services.AddSingleton<JwtTokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAuditLogQuery, AuditLogQuery>();
        services.AddScoped<ISiteService, SiteService>();
        services.AddScoped<IAreaService, AreaService>();
        services.AddScoped<ILineService, LineService>();
        services.AddScoped<IStationService, StationService>();
        services.AddScoped<DevSeeder>();
        return services;
    }
}
