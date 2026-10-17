namespace MiniMes.Contracts.Planning;

// POST /api/work-orders/preview: what Release would freeze for this item and quantity, computed from the
// master data in effect now. Nothing is saved.
public sealed record PreviewWorkOrderRequest(int ItemId, int PlannedQty);

public sealed record WorkOrderPreviewDto(
    int ItemId,
    string ItemCode,
    int PlannedQty,
    int BomVersion,
    int RoutingVersion,
    DateTime AsOfUtc,
    IReadOnlyList<WorkOrderMaterialDto> Materials,
    IReadOnlyList<WorkOrderOperationDto> Operations);

// RequiredQty is exact; RequiredQtyRounded is what to issue (pcs rounded up, kg kept exact, ADR-005).
public sealed record WorkOrderMaterialDto(
    int ComponentItemId,
    string ComponentCode,
    string ComponentName,
    string Uom,
    int OperationSeq,
    decimal QtyPerUnit,
    decimal RequiredQty,
    decimal RequiredQtyRounded,
    decimal IssuedQty,
    decimal ConsumedQty);

public sealed record WorkOrderOperationDto(
    int Seq,
    string OperationCode,
    int WorkCenterId,
    string WorkCenterCode,
    decimal IdealCycleTimeSec,
    bool IsInspection,
    bool IsMandatory,
    string? SopUrl,
    int QtyGood,
    int QtyNg);

// Dates are UTC. Priority 1 = most urgent (default 5).
public sealed record CreateWorkOrderRequest(
    int ItemId,
    int LineId,
    int PlannedQty,
    DateTime PlannedStart,
    DateTime PlannedEnd,
    int Priority = 5,
    bool RequireFai = true);

// Draft only. RowVersion = the value the client loaded: a change by someone else in between -> 409.
public sealed record UpdateWorkOrderRequest(
    int LineId,
    int PlannedQty,
    DateTime PlannedStart,
    DateTime PlannedEnd,
    bool RequireFai,
    string RowVersion);

public sealed record WorkOrderSummaryDto(
    int Id,
    string WoNo,
    int ItemId,
    string ItemCode,
    int LineId,
    string LineCode,
    int PlannedQty,
    int QtyGoodLastStep,
    DateTime PlannedStart,
    DateTime PlannedEnd,
    string Status,
    int Priority,
    string? FaiStatus);

public sealed record WorkOrderDetailDto(
    int Id,
    string WoNo,
    int ItemId,
    string ItemCode,
    string ItemName,
    int LineId,
    string LineCode,
    int PlannedQty,
    DateTime PlannedStart,
    DateTime PlannedEnd,
    string Status,
    int Priority,
    bool RequireFai,
    string? FaiStatus,
    string? FaiSerialNo,
    int? BomVersion,
    int? RoutingVersion,
    DateTime? ReleasedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? HoldReason,
    string? CompleteReason,
    string? CancelReason,
    string? ExternalRef,
    int QtyGoodLastStep,
    int SerialCount,
    IReadOnlyList<string> AllowedActions,   // the UI enables buttons from this, never from Status
    string RowVersion,                      // base64; send it back with the next change
    IReadOnlyList<WorkOrderMaterialDto> Materials,
    IReadOnlyList<WorkOrderOperationDto> Operations);

// Body of every status change (release, hold, resume, complete, close, cancel). RowVersion = what the client
// loaded: if someone changed the WO since, the action is refused with 409. Reason only where the rule needs one.
public sealed record WorkOrderTransitionRequest(string RowVersion, string? Reason = null);

// [v2] PUT /api/work-orders/{id}/priority. 1 = most urgent.
public sealed record ChangePriorityRequest(int Priority, string RowVersion);

// [v2] GET /api/lines/{id}/dispatch-list: what the line should run next (kiosk, M3). Released, InProgress
// and OnHold orders, most urgent first, then by planned start.
public sealed record DispatchListItemDto(
    int WorkOrderId,
    string WoNo,
    string ItemCode,
    int PlannedQty,
    int QtyGoodLastStep,
    int Priority,
    DateTime PlannedStart,
    string Status,
    string? FaiStatus);

public sealed record SerialDto(long Id, string SerialNo, string Status, int CurrentSeq);

// Query string of GET /api/work-orders/{id}/serials.
public sealed class SerialFilter
{
    public const int MaxPageSize = 500;

    public string? Status { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 100;
}

// Query string of GET /api/work-orders.
public sealed class WorkOrderFilter
{
    public const int MaxPageSize = 200;

    public string? Status { get; init; }
    public int? LineId { get; init; }
    public string? Search { get; init; }    // contains, on WoNo or item code
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
