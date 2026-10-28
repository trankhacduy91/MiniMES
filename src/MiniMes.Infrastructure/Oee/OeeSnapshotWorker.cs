using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MiniMes.Infrastructure.Oee;

// Spec M5 2.4: wakes every minute and lets the service close whatever is due. PeriodicTimer takes the TimeProvider,
// so a test drives it with FakeTimeProvider.Advance instead of waiting; ticks never overlap (the next one waits).
public sealed class OeeSnapshotWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<OeeSnapshotWorker> log)
    : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period, time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    // The worker is a singleton, the DbContext is scoped: a fresh scope (and context) per run.
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<OeeSnapshotService>().CloseDueBucketsAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An unhandled exception would stop the whole host (.NET 6+); log and try again next minute.
                    log.LogError(ex, "OEE snapshot run failed; retrying next tick");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
