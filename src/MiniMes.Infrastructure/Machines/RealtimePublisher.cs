using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Machines;
using MiniMes.Application.Realtime;
using MiniMes.Contracts.Realtime;
using WireQuality = MiniMes.Contracts.Machines.DataQuality;

namespace MiniMes.Infrastructure.Machines;

// Spec M4 2.7, throttle: once a second, the latest counter and process readings of the machines that changed.
// A machine sending 5 counts a second still costs each client one event a second, and nothing is lost because the
// value is absolute (cumulative counter), not "+1". State changes do NOT come through here: they are pushed at once
// (MachineStateService), which is what keeps "state on screen < 2 s".
public sealed class RealtimePublisher(
    MachineRuntimeStore store,
    MachineDirectory machines,
    IRealtimeNotifier notifier,
    TimeProvider clock,
    ILogger<RealtimePublisher> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await PublishDirtyAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Realtime publish failed"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task PublishDirtyAsync(CancellationToken ct)
    {
        var dirty = store.TakeDirty();
        foreach (var (machineId, counter, lastSeen) in dirty.Counters)
            if (machines.Get(machineId) is { } m)
                await notifier.MachineCounterUpdatedAsync(new MachineCounterDto(m.Code, m.Line, counter, lastSeen), ct);
        foreach (var (machineId, p) in dirty.Process)
            if (machines.Get(machineId) is { } m)
                await notifier.MachineProcessUpdatedAsync(new MachineProcessDto(m.Code, m.Line, p.Tag, p.Unit, p.Value,
                    Enum.Parse<WireQuality>(p.Quality.ToString()), p.SourceTs), ct);
    }
}
