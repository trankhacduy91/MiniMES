using MiniMes.Domain.Common;
using MiniMes.Domain.Planning;

namespace MiniMes.Domain.Execution;

// Who works at which station on which work order, from login to logout (spec M3 1.2). A scan needs an open
// session: the log then knows the operator, and the next shift never scans under the previous one's name.
// At most one open session per station (filtered unique index); opening a new one closes the old one.
public class StationSession : IAudited
{
    public long Id { get; private set; }
    public int StationId { get; private set; }
    public int WorkOrderId { get; private set; }
    public string OperatorCode { get; private set; }
    public DateTime OpenedAt { get; private set; }
    public DateTime? ClosedAt { get; private set; }

    private StationSession() { OperatorCode = string.Empty; }

    public StationSession(int stationId, int workOrderId, string operatorCode, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorCode);
        StationId = stationId;
        WorkOrderId = workOrderId;
        OperatorCode = operatorCode;
        OpenedAt = utcNow;
    }

    // M3 step 5: a station only runs a WO of its own line that is on the line now (the dispatch list of M2:
    // Released, InProgress, OnHold). OnHold is allowed so the line can log in; route check blocks its scans.
    public static void EnsureCanRun(int stationLineId, int woLineId, string woNo, WorkOrderStatus woStatus)
    {
        if (woLineId != stationLineId)
            throw new DomainException($"Work order {woNo} runs on another line.");
        if (woStatus is not (WorkOrderStatus.Released or WorkOrderStatus.InProgress or WorkOrderStatus.OnHold))
            throw new DomainException($"Work order {woNo} is {woStatus}; it is not on the line.");
    }

    public bool IsOpen => ClosedAt is null;

    public void Close(DateTime utcNow)
    {
        if (!IsOpen) throw new DomainException("This station session is already closed.");
        ClosedAt = utcNow;
    }
}
