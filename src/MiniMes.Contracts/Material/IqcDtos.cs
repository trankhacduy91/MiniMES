namespace MiniMes.Contracts.Material;

// [v2] Spec M6 step 2b. One row per (item, supplier lot) that still has reels waiting for incoming inspection.
public sealed record PendingIqcDto(int ItemId, string ItemCode, string ItemName, string Uom, string SupplierLot,
    int LotCount, decimal TotalQty, DateTime FirstReceivedAt, DateTime? EarliestExpiryAt);

// Result: Pass | Fail. Fail needs an Incoming defect code; Pass with defects needs a note (still within the AQL).
public sealed record RecordIqcRequest(int ItemId, string SupplierLot, int SampleQty, int DefectQty, string Result,
    string? DefectCode, string? Note);

public sealed record IqcRecordDto(long Id, int ItemId, string ItemCode, string SupplierLot, int LotCount, int SampleQty,
    int DefectQty, string Result, string? DefectCode, string? Note, string InspectedBy, DateTime InspectedAt);
