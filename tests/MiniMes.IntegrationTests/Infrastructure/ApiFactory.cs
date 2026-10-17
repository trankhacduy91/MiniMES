using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Labels;
using MiniMes.Infrastructure.Persistence;

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
        // Tests never call the public Labelary service: a fake records the ZPL and returns a tiny PNG.
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILabelPreview>(LabelPreview);
            // Added to the interceptors of the real configuration (EF Core 8+), production wiring unchanged.
            services.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(ScanGate));
        });
    }

    public FakeLabelPreview LabelPreview { get; } = new();

    // Replays scan races in a fixed order (see ScanGate).
    public ScanGate ScanGate { get; } = new();
}

public sealed class FakeLabelPreview : ILabelPreview
{
    public static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public string? LastZpl { get; private set; }

    public Task<byte[]> RenderPngAsync(string zpl, CancellationToken ct)
    {
        LastZpl = zpl;
        return Task.FromResult(Png);
    }
}
