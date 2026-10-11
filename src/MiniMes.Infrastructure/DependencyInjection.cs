using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Application.Auth;
using MiniMes.Domain.Users;
using MiniMes.Infrastructure.Auth;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured (user-secrets or env var).");

        // Compatibility level 160 = SQL Server 2022, so EF does not emit newer syntax.
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(cs, sql => sql.UseCompatibilityLevel(160)));

        // Missing or short key = the app refuses to start, instead of failing at first login.
        services.AddOptions<JwtOptions>()
            .Bind(config.GetSection(JwtOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Key) && o.Key.Length >= 32, "Jwt:Key must be at least 32 characters.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
        services.AddSingleton<JwtTokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<DevSeeder>();
        return services;
    }
}
