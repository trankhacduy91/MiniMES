namespace MiniMes.Contracts.Traceability;

// Spec M6 step 8. Instants in UTC; the client shows plant time.

// Location: Shipped, OnPallet, InCarton, CompletedUnpacked, Wip, Scrapped (spec 2.2b). CartonNo, PalletNo, ShipmentRef:
// the board's current carton, pallet and delivery order (M3b); null while it is not packed.
public sealed record ForwardTraceDto(string SerialNo, string SerialStatus, string WoNo, int Depth, string LotNo, string? StationCode,
    DateTime ConsumedAt, string? OperatorCode, string Location, string? CartonNo, string? PalletNo, string? ShipmentRef);

public sealed record LocationCountDto(string Location, int Count);

// StartLots: the lots the search started from (one lot, or every reel of a supplier lot).
public sealed record ForwardTraceResultDto(IReadOnlyList<string> StartLots, IReadOnlyList<LocationCountDto> ByLocation,
    IReadOnlyList<ForwardTraceDto> Serials);

public sealed record BackwardTraceDto(int Depth, string LotNo, string? ChildLotNo, string ItemCode, string? SupplierLot,
    string? StationCode, DateTime ConsumedAt, string? OperatorCode, string? LoadedBy, bool FromRepair = false);

public static class TraceNodeKind
{
    public const string Serial = "serial";
    public const string Lot = "lot";
}

// One node of the backward tree, loaded when its parent is opened (spec 2.6). Under a serial: the lots it consumed
// (station, time, operator, who loaded the reel; FromRepair = a part replaced at Repair, OperatorCode = the technician).
// Under a lot: the lots it was split from (Qty taken from each).
public sealed record TraceNodeDto(string Kind, string Key, string ItemCode, string? SupplierLot, string? StationCode, DateTime? ConsumedAt,
    string? OperatorCode, string? LoadedBy, decimal? Qty, bool HasChildren, bool FromRepair = false);

// A lot of the family a Hold leaves as it is (Consumed, waiting for IQC...), with its status (M7 hold preview).
public sealed record SkippedLotDto(string LotNo, string Status);
