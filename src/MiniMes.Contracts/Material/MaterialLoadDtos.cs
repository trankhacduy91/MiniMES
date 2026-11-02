namespace MiniMes.Contracts.Material;

// Spec M6 step 4. The kiosk is already at a station with a WO session open, so a load is just the lot label (+ an
// optional feeder slot). A FIFO warning is answered by a second request with a supervisor's badge and a reason.
public sealed record LoadMaterialRequest(string LotNo, string? SlotNo = null, string? SupervisorBadge = null, string? OverrideReason = null);

// Like a scan, the verdict is a normal answer (200): Outcome Accepted | Warning | Rejected, Code = VerifyCode
// (Ok, LotNotFound, ItemNotInBom, ItemNotForStation, LotOnHold, LotNotIqcPassed, LotEmpty, LotExpired,
// FifoOlderLotAvailable). Loaded = the new load when the reel went on (Accepted, or a Warning a supervisor approved).
public sealed record LoadMaterialResponse(string Outcome, string Code, string Message, string? SuggestedLotNo,
    StationMaterialDto? Loaded);

// One line of the station's material checklist: what the WO needs here (Required) and which lot is on it now.
// A lot loaded for an item the WO does not need here is listed too (Required = false), so it can be taken off.
public sealed record StationMaterialDto(int ItemId, string ItemCode, string ItemName, string Uom, bool Required,
    decimal? QtyPerUnit, int? LoadId, string? LotNo, decimal? RemainingQty, string? SlotNo, string? LoadedBy,
    DateTime? LoadedAt, string? FifoOverrideBy);

// R10: what is really left on the reel (counted or weighed). Null = keep the theoretical number.
public sealed record UnloadMaterialRequest(decimal? RemainingQty);
