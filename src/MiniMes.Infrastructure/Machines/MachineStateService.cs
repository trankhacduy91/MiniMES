using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Machines;

namespace MiniMes.Infrastructure.Machines;

// State changes the server infers itself (spec 2.5): Offline from the watchdog or the "offline" status, and the
// "online" status. Same path as a machine message: runtime store first (hot), then the batch writer (cold).
// Step 6 adds the SignalR notification here.
public sealed class MachineStateService(
    MachineRuntimeStore store,
    Channel<IngestItem> channel,
    MachineDirectory machines,
    TimeProvider clock,
    ILogger<MachineStateService> log)
{
    public async Task MarkOfflineAsync(int machineId, string source, CancellationToken ct)
    {
        var item = store.MarkOffline(machineId, clock.GetUtcNow().UtcDateTime);
        if (item is null) return;   // already Offline
        log.LogWarning("{Machine} is Offline ({Source})", machines.CodeOf(machineId), source);
        await channel.Writer.WriteAsync(item, ct);
    }

    public async Task MarkOnlineAsync(int machineId, CancellationToken ct)
    {
        log.LogInformation("{Machine} is online", machines.CodeOf(machineId));
        await channel.Writer.WriteAsync(store.MarkOnline(machineId, clock.GetUtcNow().UtcDateTime), ct);
    }
}
