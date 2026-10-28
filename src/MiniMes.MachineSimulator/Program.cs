using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.MachineSimulator.Config;
using MiniMes.MachineSimulator.ConsoleCommands;
using MiniMes.MachineSimulator.Machines;
using MiniMes.MachineSimulator.Stations;

// Machine simulator (spec M4 2.8): plays the MQTT machines of machines.json and, for machines with a station, the
// automatic stations that scan through the API (step 9). M4b: machines with "opcua" also run an OPC UA server.
//   dotnet run --project src/MiniMes.MachineSimulator [-- --config other.json] [--auto-operator]
// --auto-operator: also scan the manual stations listed under autoOperators (a flag without value, so it is taken
// out before the configuration reads the command line, which expects "--key value" pairs).
var autoOperator = args.Contains("--auto-operator");
args = args.Where(a => a != "--auto-operator").ToArray();
var builder = Host.CreateApplicationBuilder(args);
// Next to the exe by default, so "dotnet run --project" works from any folder.
var configFile = builder.Configuration["config"] ?? Path.Combine(AppContext.BaseDirectory, "machines.json");
builder.Configuration.AddJsonFile(Path.GetFullPath(configFile), optional: false, reloadOnChange: false);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
// The OPC UA stack logs every step and session at Information; its categories are Opc.Ua.* and our server subclass.
builder.Logging.AddFilter("Opc.Ua", LogLevel.Warning);
builder.Logging.AddFilter("MiniMes.MachineSimulator.OpcUa.ReflowServer+OvenServer", LogLevel.Warning);

var options = builder.Configuration.Get<SimulatorOptions>() ?? new SimulatorOptions();
var errors = options.Validate().ToList();
if (errors.Count > 0)
{
    Console.Error.WriteLine($"{configFile} is not valid:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", errors)}");
    return 1;
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MachineFleet>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MachineFleet>());
builder.Services.AddHostedService<CommandLoop>();

// Step 9: station role. Short timeout: a slow API makes the machine wait (Idle), it must not freeze the cycle.
builder.Services.AddSingleton(_ => new HttpClient
{
    BaseAddress = new Uri((options.Api?.BaseUrl ?? "http://localhost").TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(5),
});
builder.Services.AddSingleton<StationClients>();
if (autoOperator && options.AutoOperators.Count > 0)
    builder.Services.AddHostedService(sp =>
    {
        var clients = sp.GetRequiredService<StationClients>();
        var rng = new Random(options.Seed + 1000);
        return new AutoOperatorService(
            options.AutoOperators.Select(o => (o, clients.Create(new StationOptions { Code = o.Station }, rng))).ToList(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<AutoOperatorService>>());
    });

await builder.Build().RunAsync();
return 0;
