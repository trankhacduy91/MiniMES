using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MiniMes.EdgeGateway.Queue;

// Spec M4b 2.7: every minute the cap of the outbox (rule G11); every 10 s, while messages wait, how many and how old,
// so "the broker is away and the queue grows" is visible in the log. Step 6 also publishes these numbers in the
// gateway status for the dashboard.
public sealed class OutboxMaintenanceService(SqliteOutbox outbox, TimeProvider clock, ILogger<OutboxMaintenanceService> log)
    : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);
    private const int TrimEveryTicks = 6;   // 6 x 10 s = once a minute

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (++tick % TrimEveryTicks == 0) await outbox.TrimAsync(stoppingToken);
                var stats = await outbox.StatsAsync(stoppingToken);
                if (stats.QueueLength > 0)
                    log.LogWarning("Outbox: QueueLength {QueueLength}, oldest {OldestAgeSec} s, dropped {DroppedCount}",
                        stats.QueueLength, stats.OldestAgeSec, stats.DroppedCount);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
