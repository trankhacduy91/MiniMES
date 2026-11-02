using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Infrastructure.Persistence;
using MiniMes.Infrastructure.Persistence.Seed;

namespace MiniMes.Infrastructure;

public static class DatabaseInitializer
{
    // Development: migrations are applied by hand (dotnet ef database update), only seed here.
    // Testing: the Testcontainers DB is always brand new, so apply the same migrations, then seed.
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, bool applyMigrations)
    {
        using var scope = services.CreateScope();
        if (applyMigrations)
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync();
        await scope.ServiceProvider.GetRequiredService<MasterDataSeeder>().SeedAsync(); // needs Line 1 from DevSeeder
        // M5: the sample OEE shift, Development only (1,000 scans at L1-SMT-01 would get in the way of the tests).
        if (!applyMigrations) await scope.ServiceProvider.GetRequiredService<OeeDemoSeeder>().SeedAsync();
        // M6: the standard trace scenario (spec M6 section 3), Development only; tests seed their own, tagged.
        if (!applyMigrations) await scope.ServiceProvider.GetRequiredService<TraceabilitySeeder>().SeedAsync();
    }
}
