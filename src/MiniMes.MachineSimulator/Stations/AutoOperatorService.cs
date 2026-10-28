using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.MachineSimulator.Config;

namespace MiniMes.MachineSimulator.Stations;

// Spec M4 2.8, --auto-operator: the manual stations (Assembly, Pack) scanned by the simulator, so boards flow through
// the whole routing without someone at every kiosk. Always OK: an NG is reported by a person (kiosk) or by the tester.
// One board per cycle while boards wait; none waiting = check again next cycle. Repair stays manual (repair form).
public sealed class AutoOperatorService(IReadOnlyList<(AutoOperatorOptions Options, AutoStationClient Client)> stations,
    TimeProvider clock, ILogger<AutoOperatorService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Auto operator at {Stations}", string.Join(", ", stations.Select(s => s.Client.StationCode)));
        try
        {
            await Task.WhenAll(stations.Select(s => RunAsync(s.Options, s.Client, stoppingToken)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunAsync(AutoOperatorOptions options, AutoStationClient client, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.CycleTimeSec), clock);
        while (await timer.WaitForNextTickAsync(ct))
            if (await client.NextSerialAsync(ct) is { } serial)
                await client.ScanAsync(serial, ng: false, ct);
    }
}
