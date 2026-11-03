namespace MiniMes.Contracts.Packing;

// POST /api/stations/{id}/pack (M3b): scan PACK through the route check + put the board in the station's open carton,
// in one transaction. The station packs the WO of its open session.
public sealed record PackRequest(string SerialNo);

// Always HTTP 200 for a business answer, like ScanResponse:
//   Result       "Ok" (in a carton) | "Blocked" (refused, nothing written)
//   ReasonCode   null when Ok; a scan block (SkippedStep, SerialHold...) or a packing one (WrongItem...)
//   Duplicate    true = the board was already in this carton; nothing written, the carton is shown again
//   Repacked     true = the board had passed PACK already (unpacked, or scanned at PACK before M3b) and went into a
//                carton without a second PACK scan
//   CartonClosed true = this board filled the carton: it closed itself (take it away, the label prints)
//   LabelZpl     the carton label to send to the printer when CartonClosed (already logged); null if it could not be
//                made: the carton is closed anyway, print it with POST /api/cartons/{no}/labels/print
public sealed record PackResponse(
    string Result,
    string Message,
    string? ReasonCode,
    bool Duplicate,
    bool Repacked,
    string? CartonNo,
    int CartonQty,
    int QtyPerCarton,
    bool CartonClosed,
    string? LabelZpl = null);

// POST /api/cartons/{no}/labels/print, /api/pallets/{no}/labels/print: print (again) a closed carton or pallet.
// Reason required when it was printed before (P11, M2 R13).
public sealed record PrintPackingLabelRequest(string? Reason);

// PrintNo 1 = first print; the ZPL goes to the printer (the client sends it, like M2).
public sealed record PackingLabelDto(string TargetKey, int PrintNo, string Zpl);

// GET /api/stations/{id}/packing-status?woId=: what the Packing screen shows. woId defaults to the open session's WO.
// OpenPallet (step 6, Palletize mode): the station's open pallet of the WO's item, cartons newest first.
public sealed record PackingStatusDto(int StationId, int? WorkOrderId, string? WoNo, string? ItemCode, int? QtyPerCarton,
    OpenCartonDto? OpenCarton, int? CartonsPerPallet = null, OpenPalletDto? OpenPallet = null);

public sealed record OpenPalletDto(string PalletNo, int CartonCount, IReadOnlyList<PalletCartonDto> Cartons);

// Serials newest first (the board just scanned is on top).
public sealed record OpenCartonDto(string CartonNo, int Qty, DateTime OpenedAt, IReadOnlyList<PackedSerialDto> Serials);

public sealed record PackedSerialDto(string SerialNo, DateTime PackedAt, string PackedBy);

// ---------- step 5: palletize, partial close, unpack, remove from pallet, ship ----------

// POST /api/stations/{id}/palletize: a closed carton onto the station's open pallet of its item. Always 200 for a
// business answer, like PackResponse: Result "Ok" | "Blocked"; Duplicate = already on this pallet.
// PalletClosed = this carton filled the pallet: it closed itself and LabelZpl is its label (null if not made).
public sealed record PalletizeRequest(string CartonNo);

public sealed record PalletizeResponse(
    string Result,
    string Message,
    string? ReasonCode,
    bool Duplicate,
    string? PalletNo,
    int CartonCount,
    int CartonsPerPallet,
    bool PalletClosed,
    string? LabelZpl = null);

// POST /api/cartons/{no}/close-partial, /api/pallets/{no}/close-partial: the short last one, with why. A supervisor
// logged in sends no badge; at the kiosk (operator token) the supervisor scans a badge (like the FIFO exception, M6).
public sealed record ClosePartialRequest(string Reason, string? SupervisorBadge = null);

// POST /api/cartons/{no}/unpack (Supervisor): one serial out of the carton; the carton opens again to be filled up.
public sealed record UnpackRequest(string SerialNo, string Reason);

// POST /api/pallets/{no}/ship (Supervisor): the delivery order number the pallet leaves with.
public sealed record ShipPalletRequest(string ShipmentRef);

// GET /api/cartons/{no}; also the answer of the carton actions. OnHold is derived: a live serial inside is on Hold
// (P10). LabelZpl = the label printed by this action (partial close), else null. Serials newest first.
public sealed record CartonDto(string CartonNo, string WoNo, string ItemCode, int Qty, string Status, bool IsPartial,
    string? PalletNo, bool OnHold, DateTime OpenedAt, DateTime? ClosedAt, string? ClosedBy, string? CloseReason,
    IReadOnlyList<PackedSerialDto> Serials, string? LabelZpl = null);

// GET /api/pallets/{no}; also the answer of the pallet actions. TotalQty = boards on the pallet.
public sealed record PalletDto(string PalletNo, string ItemCode, int CartonCount, int TotalQty, string Status, bool IsPartial,
    bool OnHold, DateTime? ClosedAt, string? ClosedBy, string? CloseReason, string? ShipmentRef, DateTime? ShippedAt, string? ShippedBy,
    IReadOnlyList<PalletCartonDto> Cartons, string? LabelZpl = null);

public sealed record PalletCartonDto(string CartonNo, string WoNo, int Qty, bool IsPartial, bool OnHold);
