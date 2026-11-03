using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Application.Auth;
using MiniMes.Application.Common;
using MiniMes.Application.Execution;
using MiniMes.Application.Machines;
using MiniMes.Application.Material;
using MiniMes.Application.Quality.Spc;
using MiniMes.Application.Oee;
using MiniMes.Application.MasterData.Boms;
using MiniMes.Application.Planning;
using MiniMes.Domain.Users;
using MiniMes.Infrastructure.Auth;
using MiniMes.Infrastructure.Execution;
using MiniMes.Application.Realtime;
using MiniMes.Application.Traceability;
using MiniMes.Infrastructure.Machines;
using MiniMes.Infrastructure.Oee;
using MiniMes.Infrastructure.Persistence;
using MiniMes.Infrastructure.Persistence.Interceptors;
using MiniMes.Infrastructure.Persistence.Queries;
using MiniMes.Infrastructure.Persistence.Seed;
using MiniMes.Infrastructure.Planning;
using MiniMes.Infrastructure.Realtime;
using MiniMes.Infrastructure.Traceability;

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
        services.AddSingleton<IPasswordVerifier, PasswordVerifier>();
        services.AddSingleton<JwtTokenService>();
        services.AddSingleton<ITokenIssuer>(sp => sp.GetRequiredService<JwtTokenService>());
        services.AddScoped<IBomExplosionQuery, BomExplosionQuery>();
        services.AddScoped<ISequenceNumbers, SequenceNumbers>();
        services.AddScoped<IExecutionReportQueries, ExecutionReportQueries>();
        services.AddScoped<ITraceQueries, TraceQueries>();   // M6
        services.Configure<OeeOptions>(config.GetSection(OeeOptions.SectionName));   // M5
        services.Configure<MaterialOptions>(config.GetSection(MaterialOptions.SectionName));   // M6
        services.Configure<SpcOptions>(config.GetSection(SpcOptions.SectionName));             // M7
        services.AddScoped<IOeeInputReader, OeeInputReader>();
        services.AddScoped<OeeSnapshotService>();
        // Off in Testing: the tests call the service with a FakeTimeProvider (and one thin test drives the worker).
        if (config.GetValue(OeeOptions.SectionName + ":" + nameof(OeeOptions.SnapshotJobEnabled), true))
            services.AddHostedService<OeeSnapshotWorker>();
        // IANA id works on Windows and Linux (.NET uses ICU); a wrong id fails at startup, not at the first WO.
        services.AddSingleton(new PlantTimeZone(TimeZoneInfo.FindSystemTimeZoneById(config["Plant:TimeZone"] ?? "Asia/Ho_Chi_Minh")));
        // M4: MQTT ingest. Off in Testing (no broker there; the MQTT integration tests start their own, step 9).
        services.Configure<MqttIngestOptions>(config.GetSection(MqttIngestOptions.SectionName));
        services.AddSingleton<MachineDirectory>();
        services.AddSingleton(sp => new RecentIdCache(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<MachineRuntimeStore>();
        services.AddSingleton<GatewayStatusStore>();   // [M4b]
        services.AddSingleton<MqttConnectionState>();
        services.AddSingleton(sp => new OfflineDetector(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<MachineStateService>();
        services.AddSignalR();
        services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();
        services.AddSingleton<RealtimePublisher>();
        services.AddSingleton(_ => MachineEventBatchWriter.CreateChannel());
        services.Configure<AndonOptions>(config.GetSection(AndonOptions.SectionName));
        if (config.GetValue(MqttIngestOptions.SectionName + ":" + nameof(MqttIngestOptions.Enabled), true))
        {
            // Hosted services stop in reverse order: the ingest stops first, then the writer drains what is queued.
            services.AddHostedService<MachineEventBatchWriter>();
            services.AddHostedService<MqttIngestService>();
            services.AddHostedService<ProcessValueRetentionJob>();
            services.AddHostedService<OfflineWatchdog>();
            services.AddHostedService(sp => sp.GetRequiredService<RealtimePublisher>());
            services.AddHostedService<AndonWatcherService>();   // reads the runtime store the ingest fills
        }
        services.AddScoped<DevSeeder>();
        services.AddScoped<MasterDataSeeder>();
        services.AddScoped<OeeDemoSeeder>();
        services.AddScoped<TraceabilitySeeder>();   // M6
        services.AddScoped<QualitySeeder>();        // M7
        return services;
    }
}
