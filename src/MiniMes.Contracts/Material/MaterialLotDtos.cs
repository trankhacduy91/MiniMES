namespace MiniMes.Contracts.Material;

// Spec M6 step 2. N reels of one supplier delivery -> N lots (1 reel = 1 lot). ExpiryDate is a plant-time date: the
// lot is blocked from the first minute of that day (R3, conservative). Null = the material does not expire.
public sealed record ReceiveLotsRequest(int ItemId, string SupplierLot, decimal QtyPerReel, int ReelCount, DateOnly? ExpiryDate);

// Status: Available, Hold, Consumed, PendingIqc, IqcRejected. Instants in UTC.
public sealed record MaterialLotDto(int Id, string LotNo, int ItemId, string ItemCode, string ItemName, string Uom,
    decimal Qty, decimal RemainingQty, string? SupplierLot, DateTime ReceivedAt, DateTime? ExpiryAt, string Status);

// Zpl: one label per lot, in LotNo order, ready for the printer (like the product labels of M2).
public sealed record ReceiveLotsResultDto(IReadOnlyList<MaterialLotDto> Lots, string Zpl);

// Query string of GET /api/material/lots. Search: contains, on LotNo, supplier lot or item code.
public sealed class MaterialLotFilter
{
    public const int MaxPageSize = 200;

    public int? ItemId { get; init; }
    public string? Status { get; init; }
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
