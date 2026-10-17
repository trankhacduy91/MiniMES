using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.MachineSimulator.Config;
using MiniMes.MachineSimulator.ConsoleCommands;
using MiniMes.MachineSimulator.Machines;

// Machine simulator (spec M4 2.8): plays the MQTT machines of machines.json.
//   dotnet run --project src/MiniMes.MachineSimulator [-- --config other.json]
var builder = Host.CreateApplicationBuilder(args);
// Next to the exe by default, so "dotnet run --project" works from any folder.
var configFile = builder.Configuration["config"] ?? Path.Combine(AppContext.BaseDirectory, "machines.json");
builder.Configuration.AddJsonFile(Path.GetFullPath(configFile), optional: false, reloadOnChange: false);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

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

await builder.Build().RunAsync();
return 0;
