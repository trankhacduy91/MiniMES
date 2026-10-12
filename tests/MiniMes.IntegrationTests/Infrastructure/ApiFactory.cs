using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MiniMes.IntegrationTests.Infrastructure;

// Runs the real API in memory against the container DB.
public sealed class ApiFactory(SqlServerFixture sql) : WebApplicationFactory<Program>
{
    public const string JwtKey = "integration-test-key-at-least-32-chars!";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Testing does not read user-secrets, so secrets come from environment variables
        // (same mechanism as CI). Set before the host is built, i.e. before Program.cs reads config.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", sql.ConnectionString);
        Environment.SetEnvironmentVariable("Jwt__Key", JwtKey);
        builder.UseEnvironment("Testing"); // Program.cs: Testing -> MigrateAsync + seed
    }
}
