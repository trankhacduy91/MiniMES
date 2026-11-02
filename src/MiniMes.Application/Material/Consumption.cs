using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiniMes.Application.Common;
using MiniMes.Application.Execution;
using MiniMes.Domain.Common;
using MiniMes.Domain.Material;

namespace MiniMes.Application.Material;

public sealed class MaterialOptions
{
    public const string SectionName = "Material";

    // R8: block a scan OK when a consumed item has no usable lot loaded. Off = record what is loaded and let the
    // scan pass, for running Execution (M3-M5) without material handling.
    public bool EnforceLoadedMaterial { get; set; } = true;
}

// Spec M6 2.5, first half: asked by the scan (M3) before it writes an OK. Execution knows this interface only,
// not how material is stored. Null = go on; otherwise the message for the operator.
public interface IMaterialGate
{
    Task<string?> CheckAsync(int stationId, int woId, int seq, DateTime utcNow, CancellationToken ct);
}

internal static class Consumption
{
    // What one unit consumes at a routing step: the WO's lot-controlled snapshot lines of that step, per item
    // (one SerialComponent per item, R9), quantities of repeated lines added up.
    public static async Task<List<(int ItemId, decimal QtyPer)>> LinesAtStepAsync(this IAppDbContext db, int woId, int seq, CancellationToken ct)
        => (await db.RequirementsAsync(woId, ct)).Where(r => r.OperationSeq == seq)
            .GroupBy(r => r.ItemId).Select(g => (g.Key, g.Sum(r => r.QtyPerUnit))).ToList();
}

public sealed class MaterialGate(IAppDbContext db, IOptions<MaterialOptions> options) : IMaterialGate
{
    public async Task<string?> CheckAsync(int stationId, int woId, int seq, DateTime utcNow, CancellationToken ct)
    {
        if (!options.Value.EnforceLoadedMaterial) return null;
        var itemIds = (await db.LinesAtStepAsync(woId, seq, ct)).Select(l => l.ItemId).ToList();
        if (itemIds.Count == 0) return null;   // the step consumes nothing traced (test, packing)

        var loaded = await (from l in db.MaterialLoads.AsNoTracking()
                            join lot in db.MaterialLots on l.LotId equals lot.Id
                            where l.StationId == stationId && l.UnloadedAt == null && itemIds.Contains(l.ItemId)
                            select new LoadedLot(l.ItemId, lot.LotNo, lot.Status, lot.ExpiryAt)).ToListAsync(ct);
        var findings = MaterialGateRule.Check(itemIds, loaded, utcNow);
        if (findings.Count == 0) return null;

        var ids = findings.Select(f => f.ItemId).ToList();
        var codes = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Code, ct);
        return "Material: " + string.Join("; ", findings.Select(f => f.Problem switch
        {
            GateProblem.NotLoaded => $"{codes[f.ItemId]} not loaded",
            GateProblem.LotOnHold => $"{codes[f.ItemId]} lot {f.LotNo} is on hold",
            GateProblem.LotExpired => $"{codes[f.ItemId]} lot {f.LotNo} has expired",
            _ => $"{codes[f.ItemId]} lot {f.LotNo} is not usable",
        })) + ".";
    }
}

// Spec M6 2.5, second half (R7): the scan OK was saved; write which lot each consumed item came from and subtract
// it, inside the scan's transaction and DbContext. An exception here rolls the scan back: a scan OK without its
// material record is worse than a failed scan, so nothing is caught.
public sealed class RecordConsumptionHandler(IAppDbContext db, IOptions<MaterialOptions> options, ILogger<RecordConsumptionHandler> log)
    : INotificationHandler<SerialScannedOk>
{
    public async Task Handle(SerialScannedOk e, CancellationToken ct)
    {
        var lines = await db.LinesAtStepAsync(e.WoId, e.Seq, ct);
        if (lines.Count == 0) return;
        var itemIds = lines.Select(l => l.ItemId).ToList();

        var lotByItem = await db.MaterialLoads.AsNoTracking()
            .Where(l => l.StationId == e.StationId && l.UnloadedAt == null && itemIds.Contains(l.ItemId))
            .ToDictionaryAsync(l => l.ItemId, l => l.LotId, ct);
        // Rework (repair ReturnToSeq) sends a board through a step again: it got its parts the first time.
        // Duplicate scans never get here (the scan returns before publishing); this covers the second pass.
        var done = await db.SerialComponents.AsNoTracking()
            .Where(c => c.SerialId == e.SerialId && c.StationId == e.StationId && itemIds.Contains(c.ItemId))
            .Select(c => c.ItemId).ToListAsync(ct);

        foreach (var (itemId, qtyPer) in lines.Where(l => !done.Contains(l.ItemId)))
        {
            if (!lotByItem.TryGetValue(itemId, out var lotId))
            {
                // The gate let the scan through, so the reel was taken off in between (or the gate is off).
                if (options.Value.EnforceLoadedMaterial)
                    throw new DomainException($"Material item {itemId} is no longer loaded at the station: scan again after loading it.");
                log.LogWarning("Serial {SerialNo} at station {StationId}: item {ItemId} not loaded, no lot recorded", e.SerialNo, e.StationId, itemId);
                continue;
            }
            db.SerialComponents.Add(new SerialComponent(e.SerialId, lotId, itemId, qtyPer, e.StationId, e.ScanLogId, e.At));

            // One UPDATE, never read-subtract-write: two boards at once would both read 100 and both write 96.
            // SQL evaluates every SET on the old row, so both CASEs see the RemainingQty before this scan.
            // Floors at 0 (R10, CHECK constraint); runs out -> Consumed, but Hold stays Hold.
            await db.MaterialLots.Where(l => l.Id == lotId).ExecuteUpdateAsync(s => s
                .SetProperty(l => l.RemainingQty, l => l.RemainingQty >= qtyPer ? l.RemainingQty - qtyPer : 0m)
                .SetProperty(l => l.Status, l => l.Status == MaterialLotStatus.Available && l.RemainingQty <= qtyPer
                    ? MaterialLotStatus.Consumed : l.Status), ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
