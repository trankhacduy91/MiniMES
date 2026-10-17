using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Machines;
using MiniMes.Domain.Machines;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Machines;

// Spec M4 2.5: close the open segment and open the next one, inside the caller's transaction.
// Idempotent, because a batch may be written again after a failure: same state already open = nothing to do.
public static class MachineStateHistoryWriter
{
    public static async Task CloseAndOpenAsync(AppDbContext db, StateChange change, CancellationToken ct)
    {
        var open = await db.MachineStateHistory
            .SingleOrDefaultAsync(h => h.MachineId == change.MachineId && h.EndAt == null, ct);
        if (open is not null)
        {
            if (open.State == change.State) return;
            if (change.At < open.StartAt) return;     // older than the open segment: history is not rewritten
            open.Close(change.At);
            // Saved before the insert: in one SaveChanges EF may send the INSERT first, and the filtered unique index
            // (one open segment per machine) would refuse it.
            await db.SaveChangesAsync(ct);
        }
        db.MachineStateHistory.Add(new MachineStateHistory(change.MachineId, change.State, change.At));
        await db.SaveChangesAsync(ct);
    }
}
