namespace MiniMes.Application.Traceability;

// Spec M6 2.2: the recursive trace queries, written as SQL in Infrastructure (a recursive CTE is not LINQ).

// Forward: one row per affected serial. Depth = how many genealogy steps from the starting lot to the lot the board
// actually used (0 = that lot itself). When a board used several lots of the tree, the closest one is shown.
public sealed class ForwardTraceRow
{
    public string SerialNo { get; init; } = string.Empty;
    public string SerialStatus { get; init; } = string.Empty;
    public string WoNo { get; init; } = string.Empty;
    public int Depth { get; init; }
    public string LotNo { get; init; } = string.Empty;          // the lot consumed by the board
    public string? StationCode { get; init; }
    public DateTime ConsumedAt { get; init; }                   // UTC
    public string? OperatorCode { get; init; }                  // from the scan that consumed it, or the repair technician
    // The current carton / pallet / delivery order (M3b, spec 2.2b); null while the board is not packed.
    public string? CartonNo { get; init; }
    public string? PalletNo { get; init; }
    public string? ShipmentRef { get; init; }
    // Shipped / OnPallet / InCarton / CompletedUnpacked / Wip / Scrapped.
    public string Location { get; init; } = string.Empty;
}

// Backward: one row per lot in the board's history. Depth 0 = a lot loaded on a machine and consumed by the board;
// Depth 1+ = its ancestors through LotGenealogy (ChildLotNo = the lot it was split into). Station, time and operator
// are those of the consumption the row comes from; LoadedBy only for Depth 0 (an ancestor was never on the machine).
public sealed class BackwardTraceRow
{
    public int Depth { get; init; }
    public string LotNo { get; init; } = string.Empty;
    public string? ChildLotNo { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public string? SupplierLot { get; init; }
    public string? StationCode { get; init; }
    public DateTime ConsumedAt { get; init; }
    public string? OperatorCode { get; init; }                  // the scan's operator, or the repair technician
    public string? LoadedBy { get; init; }
    public bool FromRepair { get; init; }                       // a part replaced at Repair, not consumed by a scan (step 10b)
}

public interface ITraceQueries
{
    // Unknown lot or supplier lot: an empty list, not an error (a search screen asks this all day).
    Task<IReadOnlyList<ForwardTraceRow>> ForwardByLotAsync(string lotNo, CancellationToken ct);
    Task<IReadOnlyList<ForwardTraceRow>> ForwardBySupplierLotAsync(string supplierLot, int itemId, CancellationToken ct);
    Task<IReadOnlyList<BackwardTraceRow>> BackwardAsync(string serialNo, CancellationToken ct);
}
