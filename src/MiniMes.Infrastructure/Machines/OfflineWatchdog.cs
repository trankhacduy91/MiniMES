using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Machines;

namespace MiniMes.Infrastructure.Machines;

// Spec 2.5: every 5 s, machines silent for more than 30 s become Offline. The rule (with the grace period) is
// OfflineDetector; this only ticks. Heartbeat is still needed next to the LWT: the LWT only catches a dead TCP
// connection, not a machine whose software hangs while the connection stays open (the simulator's "mute").
public sealed class OfflineWatchdog(
    OfflineDetector detector,
    MachineRuntimeStore store,
    MqttConnectionState connection,
    MachineStateService states,
    TimeProvider clock,
    ILogger<OfflineWatchdog> log) : BackgroundService
{
    // 1 s, not the 5 s of the spec snippet: a confirmed LWT shows within ~4 s instead of up to 8 s.
    // A tick only walks a handful of in-memory snapshots.
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var verdict in detector.Find(store.All(), connection.ConnectedSince))
                        await states.MarkOfflineAsync(verdict.MachineId, verdict.Reason, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Offline watchdog failed");   // never stop the host
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
