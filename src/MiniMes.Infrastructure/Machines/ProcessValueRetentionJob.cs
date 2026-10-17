using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Machines;

// [v2] Spec 2.3b: ProcessValues keeps 30 days. Deleted in chunks of 5,000 rows, each its own short statement:
// one DELETE of a whole day would hold locks long enough to block the batch writer and grow the transaction log.
public sealed class ProcessValueRetentionJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ProcessValueRetentionJob> log)
    : BackgroundService
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    public const int ChunkSize = 5_000;
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        do
        {
            try
            {
                var deleted = await DeleteExpiredAsync(stoppingToken);
                if (deleted > 0) log.LogInformation("Process values: {Count} rows older than {Days} days deleted", deleted, Keep.TotalDays);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogError(ex, "Process value retention failed, next try in {Every}", Every); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // By ReceivedAt (server clock), not SourceTs: a gateway with a wrong clock must not get its fresh rows deleted.
    // ReceivedAt grows with the clustered Id, so the oldest rows sit at the start of the table and each chunk is
    // found without an extra index (only (MachineTagId, SourceTs) exists, to keep inserts cheap).
    public async Task<int> DeleteExpiredAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - Keep;
        var total = 0;
        int deleted;
        do
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE TOP ({ChunkSize}) FROM dbo.ProcessValues WHERE ReceivedAt < {cutoff}", ct);
            total += deleted;
        }
        while (deleted == ChunkSize);
        return total;
    }
}
