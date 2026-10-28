using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.Contracts.Machines;
using MiniMes.MachineSimulator.Config;
using MiniMes.MachineSimulator.Mqtt;
using MiniMes.MachineSimulator.Stations;

namespace MiniMes.MachineSimulator.Machines;

// All machines of machines.json. Per machine three tasks: the cycle, the heartbeat, the MQTT connection.
public sealed class MachineFleet : BackgroundService
{
    private readonly ILogger<MachineFleet> _log;

    public MachineFleet(SimulatorOptions options, TimeProvider clock, ILoggerFactory loggers, StationClients stations)
    {
        _log = loggers.CreateLogger<MachineFleet>();
        var machineLog = loggers.CreateLogger<SimulatedMachine>();
        var mqttLog = loggers.CreateLogger<MachinePublisher>();
        // One Random per machine from the seed: the same demo every run, whatever the thread timing.
        Machines = options.Machines.Select((m, i) =>
        {
            var status = MachineTopic.Build(options.Site, options.Area, m.Line, m.Code, MachineKinds.Status);
            var publisher = new MachinePublisher(options.Mqtt, m.Code, status, clock, mqttLog);
            var rng = new Random(options.Seed + i);
            return new SimulatedMachine(options, m, rng, publisher, clock, machineLog, m.Station is { } st ? stations.Create(st, rng) : null);
        }).ToList();
    }

    public IReadOnlyList<SimulatedMachine> Machines { get; }

    public SimulatedMachine? Find(string code)
        => Machines.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Simulating {Count} machines: {Codes}", Machines.Count, string.Join(", ", Machines.Select(m => m.Code)));
        try
        {
            await Task.WhenAll(Machines.SelectMany(m => new[]
            {
                m.Publisher.RunAsync(stoppingToken), m.RunAsync(stoppingToken), m.HeartbeatAsync(stoppingToken),
            }));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Ctrl+C: stop the loops first, then each machine says "offline" (retained) and disconnects cleanly.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await Task.WhenAll(Machines.Select(m => m.Publisher.StopAsync(cancellationToken)));
        foreach (var m in Machines) m.Publisher.Dispose();
        _log.LogInformation("All machines stopped");
    }
}
