using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Application.MasterData.Boms;
using MiniMes.Application.MasterData.Routings;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Boms;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Planning;

// What Release freezes into a WO: the approved BOM and routing in effect at asOf, as snapshot values.
internal sealed record WorkOrderRecipe(
    int BomVersion, int RoutingVersion, IReadOnlyList<MaterialSnapshot> Materials, IReadOnlyList<RoutingStepSnapshot> Steps);

internal static class WorkOrderQueries
{
    // Rule R1. Every BOM in effect is loaded in one query and exploded in memory (as bom-tree does), because the
    // snapshot needs material per consuming step, which the CTE's per-item totals do not keep.
    public static async Task<WorkOrderRecipe> LoadRecipeAsync(this IAppDbContext db, Item item, DateTime asOfUtc, CancellationToken ct)
    {
        var boms = await db.Boms.AsNoTracking().Include(b => b.Lines).EffectiveAt(asOfUtc).ToListAsync(ct);
        var linesByItem = boms.ToDictionary(b => b.ItemId, b => (IReadOnlyList<BomLine>)b.Lines);
        var bom = boms.SingleOrDefault(b => b.ItemId == item.Id)
                  ?? throw new DomainException($"Item '{item.Code}' has no approved BOM in effect at {asOfUtc:yyyy-MM-dd HH:mm} UTC.");
        var routing = await db.Routings.AsNoTracking().Include(r => r.Steps).EffectiveAt(asOfUtc)
                          .SingleOrDefaultAsync(r => r.ItemId == item.Id, ct)
                      ?? throw new DomainException($"Item '{item.Code}' has no approved routing in effect at {asOfUtc:yyyy-MM-dd HH:mm} UTC.");

        // Per 1 unit: Release multiplies by PlannedQty, so editing the quantity never needs a new explosion.
        var tree = BomExplosion.BuildTree(item.Id, 1m, id => linesByItem.GetValueOrDefault(id));
        var ids = BomExplosion.Totals(tree).Keys.ToList();
        var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToListAsync(ct);
        BomQueries.EnsureSemisHaveBom(items, linesByItem.Keys.ToHashSet(), asOfUtc);

        var materials = BomExplosion.LeafMaterials(tree)
            .Select(m => new MaterialSnapshot(m.ItemId, m.OperationSeq, m.Qty)).ToList();
        var steps = routing.Steps.OrderBy(s => s.Seq)
            .Select(s => new RoutingStepSnapshot(s.Seq, s.OperationCode, s.WorkCenterId, s.IdealCycleTimeSec,
                s.IsInspection, s.IsMandatory, s.SopUrl))
            .ToList();
        return new WorkOrderRecipe(bom.Version, routing.Version, materials, steps);
    }

    public static async Task<WorkOrder> LoadWorkOrderAsync(this IAppDbContext db, int id, CancellationToken ct)
        => await db.WorkOrders.Include(w => w.Materials).Include(w => w.Operations)   // HasOutput needs Operations
.SingleOrDefaultAsync(w => w.Id == id, ct)
           ?? throw new KeyNotFoundException($"Work order {id} not found.");

    public static async Task<WorkOrderDetailDto> GetWorkOrderDtoAsync(this IAppDbContext db, int id, CancellationToken ct)
    {
        var wo = await db.WorkOrders.AsNoTracking().Include(w => w.Materials).Include(w => w.Operations)
.SingleOrDefaultAsync(w => w.Id == id, ct)
                 ?? throw new KeyNotFoundException($"Work order {id} not found.");
        var item = await db.Items.AsNoTracking().SingleAsync(i => i.Id == wo.ItemId, ct);
        var lineCode = await db.Lines.Where(l => l.Id == wo.LineId).Select(l => l.Code).SingleAsync(ct);
        var serialCount = await db.Serials.CountAsync(s => s.WorkOrderId == wo.Id, ct);
        var (materials, operations) = await db.ToDtosAsync(
            wo.Materials.Select(m => (new MaterialSnapshot(m.ComponentItemId, m.OperationSeq, m.QtyPerUnit), m.RequiredQty, m.IssuedQty, m.ConsumedQty)),
            wo.Operations.Select(o => (new RoutingStepSnapshot(o.Seq, o.OperationCode, o.WorkCenterId, o.IdealCycleTimeSec,
                o.IsInspection, o.IsMandatory, o.SopUrl), o.QtyGood, o.QtyNg)), ct);

        return new WorkOrderDetailDto(wo.Id, wo.WoNo, item.Id, item.Code, item.Name, wo.LineId, lineCode, wo.PlannedQty,
            wo.PlannedStart, wo.PlannedEnd, wo.Status.ToString(), wo.Priority, wo.RequireFai, wo.FaiStatus?.ToString(),
            wo.FaiSerialNo, wo.BomVersion, wo.RoutingVersion, wo.ReleasedAt, wo.StartedAt, wo.CompletedAt, wo.HoldReason,
            wo.CompleteReason, wo.CancelReason, wo.ExternalRef, wo.QtyGoodLastStep, serialCount,
            wo.AllowedActions.Select(a => a.ToString()).ToList(), Convert.ToBase64String(wo.RowVersion),
            materials, operations);
    }

    // Shared by preview (from master data) and detail (from the frozen snapshot): adds codes, names and rounding.
    public static async Task<(List<WorkOrderMaterialDto>, List<WorkOrderOperationDto>)> ToDtosAsync(
        this IAppDbContext db,
        IEnumerable<(MaterialSnapshot M, decimal Required, decimal Issued, decimal Consumed)> materials,
        IEnumerable<(RoutingStepSnapshot S, int Good, int Ng)> operations, CancellationToken ct)
    {
        var mats = materials.ToList();
        var ops = operations.ToList();
        var itemIds = mats.Select(m => m.M.ComponentItemId).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var wcIds = ops.Select(o => o.S.WorkCenterId).Distinct().ToList();
        var wcCodes = await db.WorkCenters.AsNoTracking().Where(w => wcIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);

        var materialDtos = mats
            .OrderBy(m => m.M.OperationSeq).ThenBy(m => items[m.M.ComponentItemId].Code)
            .Select(m =>
            {
                var i = items[m.M.ComponentItemId];
                return new WorkOrderMaterialDto(i.Id, i.Code, i.Name, i.Uom, m.M.OperationSeq, m.M.QtyPerUnit,
                    m.Required, Units.RoundRequirement(i.Uom, m.Required), m.Issued, m.Consumed);
            })
            .ToList();
        var operationDtos = ops.OrderBy(o => o.S.Seq)
            .Select(o => new WorkOrderOperationDto(o.S.Seq, o.S.OperationCode, o.S.WorkCenterId, wcCodes[o.S.WorkCenterId],
                o.S.IdealCycleTimeSec, o.S.IsInspection, o.S.IsMandatory, o.S.SopUrl, o.Good, o.Ng))
            .ToList();
        return (materialDtos, operationDtos);
    }
}
