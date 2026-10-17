using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Application.Machines;
using MiniMes.Domain.Machines;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Machines;

// At ingest start: the runtime store gets each machine's open segment and last stored counter back from the DB.
// Without it the first count after a restart is a mere baseline (delta 0) and the production since the last stored
// count is lost; and the first state message would look like a change and split the open segment.
public static class MachineRuntimeLoader
{
    public static async Task<int> LoadAsync(IServiceScopeFactory scopes, MachineRuntimeStore store, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var open = await db.MachineStateHistory.AsNoTracking()
            .Where(h => h.EndAt == null)
            .ToDictionaryAsync(h => h.MachineId, h => (h.State, h.StartAt), ct);
        var lastCounts = await db.MachineEvents.AsNoTracking()
            .Where(e => e.Kind == MachineEventKind.Count)
            .GroupBy(e => e.MachineId)
            .Select(g => g.OrderByDescending(e => e.MachineTs).ThenByDescending(e => e.Id)
                .Select(e => new { e.MachineId, e.Counter, e.MachineTs }).First())
            .ToDictionaryAsync(e => e.MachineId, ct);

        var machineIds = open.Keys.Union(lastCounts.Keys).ToList();
        foreach (var machineId in machineIds)
        {
            var segment = open.TryGetValue(machineId, out var s) ? s : default((MachineState, DateTime)?);
            var count = lastCounts.GetValueOrDefault(machineId);
            store.Seed(machineId, segment?.Item1, segment?.Item2, count?.Counter, count?.MachineTs);
        }
        return machineIds.Count;
    }
}
