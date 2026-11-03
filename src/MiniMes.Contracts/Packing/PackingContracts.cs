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
public sealed record PackingStatusDto(int StationId, int? WorkOrderId, string? WoNo, string? ItemCode, int? QtyPerCarton,
    OpenCartonDto? OpenCarton);

// Serials newest first (the board just scanned is on top).
public sealed record OpenCartonDto(string CartonNo, int Qty, DateTime OpenedAt, IReadOnlyList<PackedSerialDto> Serials);

public sealed record PackedSerialDto(string SerialNo, DateTime PackedAt, string PackedBy);
