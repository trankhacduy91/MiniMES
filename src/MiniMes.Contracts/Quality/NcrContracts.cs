namespace MiniMes.Contracts.Quality;

// Spec M7 2.5, [v2] 4 states: Open -> UnderReview -> Dispositioned -> Closed (Cancelled from Open / UnderReview).
// Every transition carries the RowVersion the user saw: someone else changed the NCR meanwhile -> 409.

// POST /api/ncrs. Targets by their label code: Type "Serial" + serial number, or "Lot" + lot number.
// SpcAlertId: opened from an SPC alert (the "Create NCR" button of the chart): Source becomes SpcAlert, the alert is
// linked and closes when the NCR closes.
//   Source: SpcAlert | Inspection | ScanNg | Traceability | Customer | Manual
public sealed record CreateNcrRequest(string Source, string? SourceRef, string? DefectCode, string Description, int Qty,
    IReadOnlyList<NcrTargetRequest> Targets, long? SpcAlertId = null);

public sealed record NcrTargetRequest(string Type, string Code);

public sealed record NcrTransitionRequest(string RowVersion);

//   Disposition: UseAsIs | Rework | Scrap | ReturnToVendor (lots only, Q13). Note required (Q14).
public sealed record ProposeDispositionRequest(string Disposition, string Note, string? ConcessionRef, string RowVersion);

public sealed record CloseNcrRequest(string? CapaReference, string RowVersion);

public sealed record CancelNcrRequest(string Reason, string RowVersion);

public sealed record NcrListItemDto(int Id, string NcrNo, string Status, string Source, string? DefectCode, string Description,
    int Qty, int TargetCount, string? Disposition, string CreatedBy, DateTime CreatedAt);

// ActiveHolds: holds placed for this NCR and not released yet (Q12: Close / Cancel refused while > 0).
public sealed record NcrDto(int Id, string NcrNo, string Status, string Source, string? SourceRef, string? DefectCode,
    string Description, int Qty, string? Disposition, string? DispositionNote, string? ConcessionRef, string? CapaReference,
    string CreatedBy, DateTime CreatedAt, string? ReviewedBy, string? ProposedBy, DateTime? ProposedAt, string? ApprovedBy,
    DateTime? ApprovedAt, string? ClosedBy, DateTime? ClosedAt, string? CancelReason, IReadOnlyList<NcrTargetDto> Targets,
    int ActiveHolds, long? SpcAlertId, string RowVersion);

// Status = the target's own status now (serial: InProcess, Hold...; lot: Available, Hold...).
public sealed record NcrTargetDto(string Type, long Id, string Code, string Status);
