using MiniMes.Domain.Planning;

namespace MiniMes.Domain.Execution;

// Everything route check needs, read beforehand by the Scan handler: the decision itself touches no
// database and no clock, so every case is a plain unit test (spec M3 2.1).
public sealed record ScanContext(
    SerialState? Serial,                       // null = no serial with this number
    WorkOrderStatus? WoStatus,                 // status of the serial's WO
    int SessionWoId,                           // the WO the station's open session is running
    StationInfo Station,
    IReadOnlyList<RouteStep> Operations,       // the WO's frozen routing (WorkOrderOperation), any order
    FaiStatus? FaiStatus,                      // [v2] of the serial's WO
    string? FaiSerialNo,
    LastScan? LastScanHere,                    // the serial's latest accepted scan at THIS station, if any
    ScanResult RequestedResult,
    DateTime NowUtc,
    TimeSpan DuplicateWindow);

public sealed record SerialState(string SerialNo, int WorkOrderId, SerialStatus Status, int CurrentSeq);

// WorkCenterId null on a production station = not linked to any routing step yet (configuration error).
public sealed record StationInfo(int Id, int? WorkCenterId, bool IsRepair);

public sealed record RouteStep(int Seq, string OperationCode, int WorkCenterId);

public sealed record LastScan(DateTime At, ScanResult Result);

public enum RouteVerdict
{
    Allow,
    Duplicate,   // same serial, station and result within the window: answer like the first scan, write nothing
    Block,
}

// Stable codes the clients map to their own text (.resx); never compare messages.
public enum BlockReason
{
    None,
    NoOpenSession,   // the station has no logged-in operator/WO: checked by the handler before route check
    SerialNotFound,
    WoNotActive,
    WrongWo,
    SerialHold,
    SerialScrapped,
    SerialCompleted,
    MustGoRepair,
    NotDefective,
    StationNotInRoute,
    FaiNotApproved,   // [v2]
    SkippedStep,
    AlreadyPassed,
}

// Step = the routing step done at this station (null at Repair or when blocked).
// AssignFaiSample = this serial becomes the WO's first article; the handler records it with a conditional
// UPDATE so two stations cannot both pick a sample (spec 2.2).
public sealed record RouteDecision(RouteVerdict Verdict, BlockReason Reason, string Message)
{
    public RouteStep? Step { get; init; }
    public bool AssignFaiSample { get; init; }

    public static RouteDecision Block(BlockReason reason, string message) => new(RouteVerdict.Block, reason, message);
}
