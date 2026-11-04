using MiniMes.Contracts.Traceability;

namespace MiniMes.Contracts.Quality;

// Spec M7 2.6: what to hold. Scope: Lot (the lot, its delivery and split lots, and every serial made from them) |
// Serials (a list) | Carton | Pallet (the live serials inside) | MachineWindow (serials scanned OK at the machine's
// work center in [From, To), UTC). Only the fields of the scope are read. Reason: required to hold, not to preview.
// NcrId links the hold to an open NCR; CreateNcr opens one (targets: the lots for a lot hold, else the serials).
public sealed record HoldTargetsRequest(
    string Scope,
    string? LotNo = null,
    IReadOnlyList<string>? SerialNos = null,
    string? CartonNo = null,
    string? PalletNo = null,
    string? MachineCode = null,
    DateTime? From = null,
    DateTime? To = null,
    string? Reason = null,
    int? NcrId = null,
    bool CreateNcr = false);

public sealed record HoldStatusCountDto(string Status, int Count);

// "This lot went into 87 boards (55 InProcess, 24 Completed, 8 already Hold); 3 of them are shipped. Hold all?"
public sealed record HoldPreviewDto(
    string Scope,
    string ScopeRef,
    int SerialCount,
    IReadOnlyList<HoldStatusCountDto> SerialsByStatus,
    int AlreadyHeld,
    int Shipped,                                   // in a shipped carton/pallet: held in the data, tell the customer
    int SkippedScrapped,                           // Q10: a scrapped board is never held
    IReadOnlyList<string> Lots,                    // lots that will be held
    IReadOnlyList<SkippedLotDto> SkippedLots);     // lots of the family left as they are (PendingIqc, Consumed...)

public sealed record HoldResultDto(Guid BatchId, int Serials, int Lots, int? NcrId, string? NcrNo);

// One Hold action. Active* = targets of the batch still held by it.
public sealed record HoldBatchDto(
    Guid BatchId,
    string Scope,
    string? ScopeRef,
    string Reason,
    int? NcrId,
    string? NcrNo,
    int ActiveSerials,
    int ActiveLots,
    int Released,
    string HeldBy,
    DateTime HeldAt);

public sealed record ReleaseHoldRequest(string Note);

// StillHeld: targets released from this batch but held by another one (Q8), their status stays Hold.
public sealed record ReleaseResultDto(Guid BatchId, int Released, int Restored, int StillHeld);
