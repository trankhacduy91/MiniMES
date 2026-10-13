namespace MiniMes.Contracts.MasterData;

// GET /api/items/{id}/explosion: total need of every item below the root, all levels, branches added.
// ExactQty is unrounded; RequiredQty rounds discrete units (pcs) UP and keeps continuous units (kg, g, m)
// exact, so nothing is issued short. Type tells Raw (to issue) from Semi (to build).
public sealed record ExplosionLineDto(
    int ItemId, string Code, string Name, string Type, string Uom, decimal ExactQty, decimal RequiredQty);

public sealed record BomExplosionDto(
    int ItemId, string ItemCode, decimal Qty, DateTime AsOfUtc, IReadOnlyList<ExplosionLineDto> Lines);

// GET /api/items/{id}/bom-tree: the same explosion kept as a tree (WPF TreeView). Root: QtyPer 1, Seq 0.
public sealed record BomTreeNodeDto(
    int ItemId, string Code, string Name, string Type, string Uom,
    decimal QtyPer, decimal ScrapRate, int OperationSeq, decimal RequiredQty,
    IReadOnlyList<BomTreeNodeDto> Children);

// Query string of both endpoints. AsOfUtc null = now: a work order for next week may need another version.
public sealed class ExplosionFilter
{
    public decimal Qty { get; init; } = 1;
    public DateTime? AsOfUtc { get; init; }
}
