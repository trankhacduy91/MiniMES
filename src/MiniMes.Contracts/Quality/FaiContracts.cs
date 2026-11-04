namespace MiniMes.Contracts.Quality;

// [v2] Spec M7 2.5b: first article inspection. Times are UTC.

// A WO whose first article waits for QC. SampleReady = the sample passed the last inspection step (Q17).
public sealed record FaiQueueItemDto(
    int WorkOrderId,
    string WoNo,
    string ItemCode,
    string Line,
    string WoStatus,
    string? SampleSerialNo,
    string? SampleStatus,
    bool SampleReady);

// One line of the worksheet. Kind "Auto" | "Manual". Passed: Auto computed now; Manual null on the worksheet
// (QC ticks it), true/false in a recorded FAI.
public sealed record FaiCheckDto(string Code, string Text, string Kind, bool? Passed, string? Detail);

public sealed record FaiRecordDto(
    long Id,
    string SerialNo,
    int TemplateVersion,
    string Result,
    bool AutoChecksPassed,
    string? DefectCode,
    string? Note,
    string InspectedBy,
    DateTime InspectedAt,
    IReadOnlyList<FaiCheckDto> Items);

// The FAI worksheet of a WO: the automatic part already filled from MES data, the manual part for QC, earlier
// attempts below. NotReadyReason says why it cannot be judged now (no sample, sample not through Test...).
// RowVersion of the WO: send it back with the result (409 when the WO changed meanwhile, e.g. another QC judged).
public sealed record FaiWorksheetDto(
    int WorkOrderId,
    string WoNo,
    string ItemCode,
    string Line,
    string? FaiStatus,
    string? SampleSerialNo,
    string? SampleStatus,
    bool SampleReady,
    string? NotReadyReason,
    int? TemplateVersion,
    IReadOnlyList<FaiCheckDto> Items,
    bool AutoChecksPassed,
    IReadOnlyList<FaiRecordDto> History,
    string RowVersion);

// Result "Pass" | "Fail". CheckedItems: codes of the manual items QC ticked. DefectCode: Fail only, FAI-FAIL when empty.
public sealed record RecordFaiRequest(string Result, IReadOnlyList<string>? CheckedItems, string? Note, string? DefectCode,
    string RowVersion);

// Pushed to the line group after a FAI is recorded: the kiosks drop (Passed) or keep (Pending) the yellow banner.
public sealed record FaiStatusChangedDto(int WorkOrderId, string WoNo, string Line, string FaiStatus, string? FaiSerialNo, string Result);
