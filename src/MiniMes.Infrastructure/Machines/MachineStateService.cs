using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Machines;
using MiniMes.Application.Realtime;
using MiniMes.Contracts.Realtime;
using WireState = MiniMes.Contracts.Machines.MachineState;

namespace MiniMes.Infrastructure.Machines;

// State changes the server infers itself (spec 2.5): Offline from the watchdog or the "offline" status, and the
// "online" status. Same path as a machine message: runtime store first (hot), then the batch writer (cold).
// Every state change is pushed to the line's dashboards at once (not throttled, spec 2.7).
public sealed class MachineStateService(
    MachineRuntimeStore store,
    Channel<IngestItem> channel,
    MachineDirectory machines,
    IRealtimeNotifier notifier,
    TimeProvider clock,
    ILogger<MachineStateService> log)
{
    public async Task MarkOfflineAsync(int machineId, string source, CancellationToken ct)
    {
        var item = store.MarkOffline(machineId, clock.GetUtcNow().UtcDateTime);
        if (item is null) return;   // already Offline
        log.LogWarning("{Machine} is Offline ({Source})", machines.CodeOf(machineId), source);
        await PushStateChangeAsync(item.StateChange, ct);
        await channel.Writer.WriteAsync(item, ct);
    }

    // For every state change, from a machine message (ingest) or inferred here.
    public async Task PushStateChangeAsync(StateChange? change, CancellationToken ct)
    {
        if (change is null || machines.Get(change.MachineId) is not { } m) return;
        await notifier.MachineStateChangedAsync(
            new MachineStateChangedDto(m.Code, m.Line, Enum.Parse<WireState>(change.State.ToString()), change.At), ct);
    }

    public async Task MarkOnlineAsync(int machineId, CancellationToken ct)
    {
        log.LogInformation("{Machine} is online", machines.CodeOf(machineId));
        await channel.Writer.WriteAsync(store.MarkOnline(machineId, clock.GetUtcNow().UtcDateTime), ct);
    }
}
