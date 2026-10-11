using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<DevSeeder>();
        return services;
    }
}
