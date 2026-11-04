using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Domain.Audit;
using MiniMes.Domain.Material;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;

namespace MiniMes.Application.Quality.Holds;

// The one write EF cannot express: INSERT ... SELECT, one HoldRecord per target, set-based (spec 2.6 point 4). Implemented
// in Infrastructure with SQL; runs in the caller's transaction. Serials already Scrapped (Q10) and lots no longer
// holdable are left out by the SQL itself, so a status that changed since the selection is respected.
public interface IHoldWriter
{
    Task<(int Serials, int Lots)> InsertAsync(HoldBatch batch, IReadOnlyCollection<long> serialIds, IReadOnlyCollection<int> lotIds,
        CancellationToken ct);
}

public sealed record HoldBatch(Guid Id, HoldScope Scope, string ScopeRef, string Reason, int? NcrId, string By, DateTime At);

// Spec 2.6: the status changes after the HoldRecords, set-based. ExecuteUpdate is one UPDATE each: the right-hand side
// reads the OLD row, so StatusBeforeHold takes the status before Hold is written. Each UPDATE changes the serial's
// rowversion: a scan holding the old version fails, re-reads and is blocked (Hold vs scan race).
// Raw SQL and ExecuteUpdate bypass the audit interceptor: one AuditLog row per batch is written here (2.6 trap 1).
internal static class HoldWrites
{
    public static async Task<(int Serials, int Lots)> HoldAsync(this IAppDbContext db, IHoldWriter writer, HoldBatch batch,
        IReadOnlyCollection<long> serialIds, IReadOnlyCollection<int> lotIds, CancellationToken ct)
    {
        var counts = await writer.InsertAsync(batch, serialIds, lotIds, ct);
        var rows = db.HoldRecords.Where(h => h.BatchId == batch.Id);

        await db.Serials.Where(s => rows.Any(h => h.TargetType == HoldTargetType.Serial && h.TargetId == s.Id))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.StatusBeforeHold, s => s.Status != SerialStatus.Hold ? (SerialStatus?)s.Status : s.StatusBeforeHold)
                .SetProperty(s => s.Status, SerialStatus.Hold), ct);
        await db.MaterialLots.Where(l => rows.Any(h => h.TargetType == HoldTargetType.Lot && h.TargetId == l.Id))
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.StatusBeforeHold, l => l.Status != MaterialLotStatus.Hold ? (MaterialLotStatus?)l.Status : l.StatusBeforeHold)
                .SetProperty(l => l.Status, MaterialLotStatus.Hold), ct);

        db.AuditLogs.Add(new AuditLog("HoldRecords", batch.Id.ToString(), AuditLog.AllColumns, null,
            JsonSerializer.Serialize(new { Action = "Hold", Scope = batch.Scope.ToString(), batch.ScopeRef, batch.Reason, batch.NcrId, counts.Serials, counts.Lots }),
            batch.By, batch.At));
        return counts;
    }

    // Closes the given active rows, then gives back the old status to the targets that have no active row left (Q8).
    // Returns (rows released, targets restored, targets still held by another hold).
    public static async Task<(int Released, int Restored, int StillHeld)> ReleaseAsync(this IAppDbContext db, IQueryable<HoldRecord> active,
        string by, string note, DateTime at, string auditKey, CancellationToken ct)
    {
        var targets = await active.Where(h => h.ReleasedAt == null).Select(h => new { h.TargetType, h.TargetId }).ToListAsync(ct);
        if (targets.Count == 0) return (0, 0, 0);
        var serialIds = targets.Where(t => t.TargetType == HoldTargetType.Serial).Select(t => t.TargetId).ToList();
        var lotIds = targets.Where(t => t.TargetType == HoldTargetType.Lot).Select(t => (int)t.TargetId).ToList();

        var released = await active.Where(h => h.ReleasedAt == null).ExecuteUpdateAsync(u => u
            .SetProperty(h => h.ReleasedBy, by).SetProperty(h => h.ReleasedAt, at).SetProperty(h => h.ReleaseNote, note), ct);

        var stillActive = db.HoldRecords.Where(h => h.ReleasedAt == null);
        var restored = await db.Serials
            .Where(s => s.Status == SerialStatus.Hold && serialIds.Contains(s.Id)
                        && !stillActive.Any(h => h.TargetType == HoldTargetType.Serial && h.TargetId == s.Id))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, s => s.StatusBeforeHold ?? SerialStatus.InProcess)
                .SetProperty(s => s.StatusBeforeHold, (SerialStatus?)null), ct);
        restored += await db.MaterialLots
            .Where(l => l.Status == MaterialLotStatus.Hold && lotIds.Contains(l.Id)
                        && !stillActive.Any(h => h.TargetType == HoldTargetType.Lot && h.TargetId == l.Id))
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, l => l.StatusBeforeHold ?? MaterialLotStatus.Available)
                .SetProperty(l => l.StatusBeforeHold, (MaterialLotStatus?)null), ct);

        db.AuditLogs.Add(new AuditLog("HoldRecords", auditKey, AuditLog.AllColumns, null,
            JsonSerializer.Serialize(new { Action = "Release", Note = note, Released = released, Restored = restored }), by, at));
        return (released, restored, targets.Count - restored);
    }
}
