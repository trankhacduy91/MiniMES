using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Application.Machines;
using MiniMes.Domain.Machines;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Machines;

// At ingest start: the runtime store gets each machine's open segment and last stored counter back from the DB.
// Without it the first count after a restart is a mere baseline (delta 0) and the production since the last stored
// count is lost; and the first state message would look like a change and split the open segment.
// Step 8: also what each machine made in the current plant hour, so the tile does not restart from 0 after a restart.
public static class MachineRuntimeLoader
{
    public static async Task<int> LoadAsync(IServiceScopeFactory scopes, MachineRuntimeStore store, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hour = store.HourStartUtc(scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);

        var open = await db.MachineStateHistory.AsNoTracking()
            .Where(h => h.EndAt == null)
            .ToDictionaryAsync(h => h.MachineId, h => (h.State, h.StartAt), ct);
        var lastCounts = await db.MachineEvents.AsNoTracking()
            .Where(e => e.Kind == MachineEventKind.Count)
            .GroupBy(e => e.MachineId)
            .Select(g => g.OrderByDescending(e => e.MachineTs).ThenByDescending(e => e.Id)
                .Select(e => new { e.MachineId, e.Counter, e.MachineTs }).First())
            .ToDictionaryAsync(e => e.MachineId, ct);
        // Index (MachineId, MachineTs) INCLUDE (Kind, Delta) covers this.
        var thisHour = await db.MachineEvents.AsNoTracking()
            .Where(e => e.Kind == MachineEventKind.Count && e.MachineTs >= hour)
            .GroupBy(e => e.MachineId)
            .Select(g => new { MachineId = g.Key, Sum = g.Sum(e => e.Delta ?? 0) })
            .ToDictionaryAsync(e => e.MachineId, e => e.Sum, ct);

        var machineIds = open.Keys.Union(lastCounts.Keys).ToList();
        foreach (var machineId in machineIds)
        {
            var segment = open.TryGetValue(machineId, out var s) ? s : default((MachineState, DateTime)?);
            var count = lastCounts.GetValueOrDefault(machineId);
            store.Seed(machineId, segment?.Item1, segment?.Item2, count?.Counter, count?.MachineTs,
                thisHour.ContainsKey(machineId) ? hour : null, thisHour.GetValueOrDefault(machineId));
        }
        return machineIds.Count;
    }
}
