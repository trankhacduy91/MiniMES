using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MediatR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Execution;
using MiniMes.Application.Labels;
using MiniMes.Application.Material;
using MiniMes.Application.Oee;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.IntegrationTests.Infrastructure;

// Runs the real API in memory against the container DB. With mqttPort (M4 step 9) the MQTT ingest and its
// background services run too, against the test broker on localhost.
public sealed class ApiFactory(SqlServerFixture sql, int? mqttPort = null) : WebApplicationFactory<Program>
{
    public const string JwtKey = "integration-test-key-at-least-32-chars!";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Testing does not read user-secrets, so secrets come from environment variables
        // (same mechanism as CI). Set before the host is built, i.e. before Program.cs reads config.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", sql.ConnectionString);
        Environment.SetEnvironmentVariable("Jwt__Key", JwtKey);
        Environment.SetEnvironmentVariable("Mqtt__Enabled", mqttPort is null ? "false" : "true"); // no broker: ingest off
        Environment.SetEnvironmentVariable("Oee__SnapshotJobEnabled", "false");   // M5: tests drive the snapshot service themselves
        Environment.SetEnvironmentVariable("Mqtt__Host", "localhost");
        Environment.SetEnvironmentVariable("Mqtt__Port", (mqttPort ?? 1883).ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseEnvironment("Testing"); // Program.cs: Testing -> MigrateAsync + seed
        // Tests never call the public Labelary service: a fake records the ZPL and returns a tiny PNG.
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILabelPreview>(LabelPreview);
            // Added to the interceptors of the real configuration (EF Core 8+), production wiring unchanged.
            services.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(ScanGate));
            // M5: the MQTT host announces stops of 5 s or more, so its test does not wait a real minute.
            if (mqttPort is not null) services.Configure<OeeOptions>(o => o.MicroStopThresholdSec = 5);
            // M6: the M3-M5 tests scan boards without loading reels, the case the flag exists for. Consumption
            // tests switch it on for themselves (MaterialEnforcement).
            services.Configure<MaterialOptions>(o => o.EnforceLoadedMaterial = false);
            // M6 step 5 (g): a second consumption handler, after the real one, that can be made to fail.
            services.AddSingleton(ConsumptionFault);
            services.AddTransient<INotificationHandler<SerialScannedOk>, ConsumptionFaultHandler>();
        });
    }

    public FakeLabelPreview LabelPreview { get; } = new();

    // Replays scan races in a fixed order (see ScanGate).
    public ScanGate ScanGate { get; } = new();

    public ConsumptionFault ConsumptionFault { get; } = new();
}

// Makes the consumption of one serial throw, to prove the scan rolls back with it (M6 2.5).
public sealed class ConsumptionFault
{
    public volatile string? FailForSerialNo;
}

public sealed class ConsumptionFaultHandler(ConsumptionFault fault) : INotificationHandler<SerialScannedOk>
{
    public Task Handle(SerialScannedOk e, CancellationToken ct)
        => e.SerialNo == fault.FailForSerialNo ? throw new InvalidOperationException("Injected consumption failure.") : Task.CompletedTask;
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
