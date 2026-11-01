using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiniMes.Application.Machines;
using MiniMes.Application.Oee;
using MiniMes.Application.Realtime;
using MiniMes.Contracts.Oee;
using MiniMes.Domain.Common;
using MiniMes.Domain.Machines;
using MiniMes.Domain.Oee;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Machines;

// Spec M4 2.4, the cold path: one reader of the ingest channel, up to 200 rows or 1 second per batch, one transaction.
// [v2] Process readings (ProcessValue, step 4b) go through the same channel and batch: one writer, one retry logic.
// Database down = keep the batch and retry. Meanwhile the bounded channel fills up, the MQTT handler waits on it,
// the client stops reading and the broker keeps our QoS 1 messages (backpressure): the rows arrive late, not lost.
public sealed class MachineEventBatchWriter(
    Channel<IngestItem> channel,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<MachineEventBatchWriter> log) : BackgroundService
{
    public const int MaxBatch = 200;
    public const int ChannelCapacity = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private const int AttemptsOnShutdown = 3;

    public static Channel<IngestItem> CreateChannel() => Channel.CreateBounded<IngestItem>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // never DropOldest: that loses data silently
            SingleReader = true,                      // one writer = rows stored in arrival order (close before open)
        });

    // The batch being collected or retried. A field, so StopAsync can still write it after ExecuteAsync has ended.
    private readonly List<IngestItem> _batch = new(MaxBatch);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                using (var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    window.CancelAfter(Window);       // from the first item, at most 1 s to collect more
                    try
                    {
                        do { while (_batch.Count < MaxBatch && reader.TryRead(out var item)) _batch.Add(item); }
                        while (_batch.Count < MaxBatch && await reader.WaitToReadAsync(window.Token));
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }   // window over
                }
                await FlushAsync(_batch, int.MaxValue, stoppingToken);
                _batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Stopping (deploy, Ctrl+C): what is still queued is written now, otherwise each restart loses up to a second.
    // Done here and not at the end of ExecuteAsync: since .NET 10 ExecuteAsync runs on a background thread, and a host
    // stopped right after starting may cancel it before it even begins, so code at its end may never run.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (ExecuteTask is { IsCompleted: false })
        {
            log.LogError("Machine events: the writer did not stop in time, {Count} queued rows not written", channel.Reader.Count);
            return;
        }
        while (channel.Reader.TryRead(out var item)) _batch.Add(item);
        if (_batch.Count == 0) return;
        log.LogInformation("Machine events: writing {Count} queued rows before stopping", _batch.Count);
        await FlushAsync(_batch, AttemptsOnShutdown, CancellationToken.None);
        _batch.Clear();
    }

    private async Task FlushAsync(List<IngestItem> batch, int maxAttempts, CancellationToken ct)
    {
        var oneByOne = false;
        var done = 0;                                 // rows already committed in one-by-one mode
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!oneByOne)
                {
                    await WriteAsync(batch, ct);
                }
                else
                {
                    for (; done < batch.Count; done++)
                    {
                        try { await WriteAsync([batch[done]], ct); }
                        catch (ConflictException)
                        {
                            log.LogDebug("Message {MsgId} already stored, skipped", batch[done].Event?.MsgId ?? batch[done].ProcessValue?.MsgId);
                        }
                    }
                }
                if (attempt > 1) log.LogInformation("Machine events: batch of {Count} written after {Attempts} attempts", batch.Count, attempt);
                return;
            }
            catch (ConflictException) when (!oneByOne)
            {
                // A msgId already in the table (redelivered after an ingest restart, the RAM cache was empty):
                // the unique index is the last net. Write row by row so only the duplicate is skipped.
                oneByOne = true;
                attempt--;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= maxAttempts)
                {
                    log.LogError(ex, "Machine events: {Count} rows NOT written after {Attempts} attempts", batch.Count - done, attempt);
                    return;
                }
                var delay = TimeSpan.FromSeconds(Math.Min(attempt * 2, 30));
                log.LogError("Machine events: writing {Count} rows failed (attempt {Attempt}, {Error}), retry in {Delay}",
                    batch.Count - done, attempt, ex.Message, delay);
                await Task.Delay(delay, clock, ct);
            }
        }
    }

    private async Task WriteAsync(IReadOnlyList<IngestItem> items, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        foreach (var evt in items.Where(i => i.Event is not null).Select(i => i.Event!))
        {
            // The same objects are written again after a failure; a failed SaveChanges may already have copied an
            // identity value into some of them, and EF would then try to INSERT that explicit Id.
            db.Entry(evt).Property(e => e.Id).CurrentValue = 0L;
            db.MachineEvents.Add(evt);
        }
        foreach (var value in items.Where(i => i.ProcessValue is not null).Select(i => i.ProcessValue!))
        {
            db.Entry(value).Property(v => v.Id).CurrentValue = 0L;
            db.ProcessValues.Add(value);
        }
        await db.SaveChangesAsync(ct);
        var closed = new List<MachineStateHistory>();
        foreach (var change in items.Where(i => i.StateChange is not null).Select(i => i.StateChange!))
            if (await MachineStateHistoryWriter.CloseAndOpenAsync(db, change, ct) is { } segment)   // in arrival order
                closed.Add(segment);

        await tx.CommitAsync(ct);
        await AnnounceUnclassifiedAsync(scope.ServiceProvider, closed, ct);
    }

    // M5 step 7: a stop that just ended, long enough to need a reason and without one (no Andon resolved it), goes to
    // the line's dashboards at once (SignalR DowntimeNeedsClassification). After the commit: never for a rolled-back
    // row. A retried batch may announce twice; the client keeps one row per segment id.
    private static async Task AnnounceUnclassifiedAsync(IServiceProvider services, List<MachineStateHistory> closed, CancellationToken ct)
    {
        var threshold = TimeSpan.FromSeconds(services.GetRequiredService<IOptions<OeeOptions>>().Value.MicroStopThresholdSec);
        var directory = services.GetRequiredService<MachineDirectory>();
        var notifier = services.GetRequiredService<IRealtimeNotifier>();
        foreach (var s in closed.Where(s => s.DowntimeReasonId is null && StopRules.ToClassify.Contains(s.State)
                                            && s.EndAt - s.StartAt >= threshold))
        {
            if (directory.Get(s.MachineId) is not { } machine) continue;
            var minutes = Math.Round((s.EndAt!.Value - s.StartAt).TotalMinutes, 1);
            await notifier.DowntimeNeedsClassificationAsync(new UnclassifiedDowntimeDto(s.Id, s.MachineId, machine.Code,
                machine.Line, s.State.ToString(), s.StartAt, s.EndAt.Value, minutes), ct);
        }
    }
}
