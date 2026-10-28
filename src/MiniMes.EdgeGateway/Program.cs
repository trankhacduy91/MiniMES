using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MiniMes.EdgeGateway;
using MiniMes.EdgeGateway.Config;
using MiniMes.EdgeGateway.Drivers;
using MiniMes.EdgeGateway.Drivers.Modbus;
using MiniMes.EdgeGateway.Drivers.OpcUa;
using MiniMes.EdgeGateway.Health;
using MiniMes.EdgeGateway.Mqtt;
using MiniMes.EdgeGateway.Pipeline;

// Edge gateway (spec M4b): reads the PLCs (OPC UA, Modbus TCP), normalises the readings and publishes the M4 MQTT
// contract. A program on its own: it shares only MiniMes.Contracts with the MES, never its database.
//   dotnet run --project src/MiniMes.EdgeGateway [-- --config other.tags.json]
var builder = Host.CreateApplicationBuilder(args);
// Next to the exe by default, so "dotnet run --project" works from any folder.
var configFile = builder.Configuration["config"] ?? Path.Combine(AppContext.BaseDirectory, "gateway.tags.json");
builder.Configuration.AddJsonFile(Path.GetFullPath(configFile), optional: false, reloadOnChange: false);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Opc.Ua", LogLevel.Warning);   // the OPC UA stack logs every session step at Information
// Socket failures come with a full stack trace on every retry; the driver already logs one line with the reason.
builder.Logging.AddFilter("Opc.Ua.Bindings", LogLevel.Critical);
// "Hosting failed to start" + stack trace: a failed start is reported below, any other crash still prints its trace.
builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.Critical);

// A wrong configuration stops the gateway now, not after it has run half blind for a shift.
builder.Services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();
builder.Services.AddOptions<GatewayOptions>().Bind(builder.Configuration).ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<GatewayOptions>>().Value);
builder.Services.AddSingleton(TimeProvider.System);

// Drivers write, the pipeline reads, one at a time and in order.
var channel = Channel.CreateUnbounded<DeviceEvent>(new UnboundedChannelOptions { SingleReader = true });
builder.Services.AddSingleton(channel.Reader);
builder.Services.AddSingleton(channel.Writer);

builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<GatewayOptions>();
    var autoAccept = o.OpcUa.AutoAcceptUntrustedCertificates ?? builder.Environment.IsDevelopment();
    return new OpcUaClientConfiguration(o, autoAccept, new HostTelemetry(sp.GetRequiredService<ILoggerFactory>()));
});
builder.Services.AddSingleton<IEnumerable<IDeviceDriver>>(sp => sp.GetRequiredService<GatewayOptions>().Devices
    .Select(d => d.Protocol switch
    {
        Protocols.OpcUa => (IDeviceDriver)ActivatorUtilities.CreateInstance<OpcUaDriver>(sp, d),
        Protocols.Modbus => ActivatorUtilities.CreateInstance<ModbusDriver>(sp, d),
        _ => throw new InvalidOperationException($"no driver for protocol {d.Protocol}"),   // the validator refuses it first
    })
    .ToList());

builder.Services.AddSingleton<Normalizer>();
builder.Services.AddSingleton<HealthPublisher>();
builder.Services.AddSingleton<GatewayMqttClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayMqttClient>());
builder.Services.AddHostedService<PipelineService>();
builder.Services.AddHostedService<DriverHost>();

try
{
    await builder.Build().RunAsync();
    return 0;
}
catch (OptionsValidationException ex)
{
    Console.Error.WriteLine($"{configFile} is not valid:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", ex.Failures)}");
    return 1;
}
