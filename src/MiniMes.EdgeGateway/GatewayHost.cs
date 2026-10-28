using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MiniMes.EdgeGateway.Config;
using MiniMes.EdgeGateway.Drivers;
using MiniMes.EdgeGateway.Drivers.Modbus;
using MiniMes.EdgeGateway.Drivers.OpcUa;
using MiniMes.EdgeGateway.Health;
using MiniMes.EdgeGateway.Mqtt;
using MiniMes.EdgeGateway.Pipeline;
using MiniMes.EdgeGateway.Queue;

namespace MiniMes.EdgeGateway;

// The whole gateway as one host builder: Program.cs runs it, the end-to-end test (step 7) runs the very same one
// in-process against a test broker. Arguments: --config <file> (default gateway.tags.json next to the exe),
// --environment Development (auto-accept OPC UA certificates). Environment variables override the file (Docker:
// mqtt__host=mosquitto, devices__0__endpoint=opc.tcp://simulator:4840).
public static class GatewayHost
{
    public static (HostApplicationBuilder Builder, string ConfigFile) CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // Next to the exe by default, so "dotnet run --project" works from any folder.
        var configFile = Path.GetFullPath(builder.Configuration["config"] ?? Path.Combine(AppContext.BaseDirectory, "gateway.tags.json"));
        builder.Configuration.AddJsonFile(configFile, optional: false, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();   // after the file: the container's settings win

        // Spec 2.9: a Windows Service on the plant's industrial PC (sc.exe create, README); a plain console elsewhere.
        builder.Services.AddWindowsService(o => o.ServiceName = "MiniMesGateway");

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.AddFilter("Opc.Ua", LogLevel.Warning);   // the OPC UA stack logs every session step at Information
        // Socket failures come with a full stack trace on every retry; the driver already logs one line with the reason.
        builder.Logging.AddFilter("Opc.Ua.Bindings", LogLevel.Critical);
        // "Hosting failed to start" + stack trace: a failed start is reported by Program.cs, any other crash still prints its trace.
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.Critical);

        var services = builder.Services;
        // A wrong configuration stops the gateway now, not after it has run half blind for a shift.
        services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();
        services.AddOptions<GatewayOptions>().Bind(builder.Configuration).ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<GatewayOptions>>().Value);
        services.AddSingleton(TimeProvider.System);

        // Drivers write, the pipeline reads, one at a time and in order.
        var channel = Channel.CreateUnbounded<DeviceEvent>(new UnboundedChannelOptions { SingleReader = true });
        services.AddSingleton(channel.Reader);
        services.AddSingleton(channel.Writer);

        var development = builder.Environment.IsDevelopment();
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<GatewayOptions>();
            var autoAccept = o.OpcUa.AutoAcceptUntrustedCertificates ?? development;
            return new OpcUaClientConfiguration(o, autoAccept, new HostTelemetry(sp.GetRequiredService<ILoggerFactory>()));
        });
        services.AddSingleton<IEnumerable<IDeviceDriver>>(sp => sp.GetRequiredService<GatewayOptions>().Devices
            .Select(d => d.Protocol switch
            {
                Protocols.OpcUa => (IDeviceDriver)ActivatorUtilities.CreateInstance<OpcUaDriver>(sp, d),
                Protocols.Modbus => ActivatorUtilities.CreateInstance<ModbusDriver>(sp, d),
                _ => throw new InvalidOperationException($"no driver for protocol {d.Protocol}"),   // the validator refuses it first
            })
            .ToList());

        // The outbox is opened once at start (creates the file and the table); a gateway without its queue must not run.
        services.AddSingleton(sp =>
        {
            var q = sp.GetRequiredService<GatewayOptions>().Queue;
            var path = Path.IsPathRooted(q.Path) ? q.Path : Path.Combine(AppContext.BaseDirectory, q.Path);
            return SqliteOutbox.OpenAsync(path, q.MaxRows, TimeSpan.FromDays(q.MaxAgeDays), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<SqliteOutbox>>()).GetAwaiter().GetResult();
        });
        services.AddSingleton<Normalizer>();
        services.AddSingleton<HealthPublisher>();
        services.AddSingleton<GatewayMqttClient>();
        services.AddHostedService(sp => sp.GetRequiredService<GatewayMqttClient>());
        services.AddSingleton<GatewayStatusPublisher>();
        services.AddHostedService<ForwarderService>();
        services.AddHostedService<OutboxMaintenanceService>();
        services.AddHostedService<PipelineService>();
        services.AddHostedService<DriverHost>();
        // Registered after the MQTT client: hosted services stop in reverse order, so the clean "offline" goes out first.
        services.AddHostedService(sp => sp.GetRequiredService<GatewayStatusPublisher>());

        return (builder, configFile);
    }
}
