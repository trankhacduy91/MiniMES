using MiniMes.Domain.Common;

namespace MiniMes.Domain.Planning;

// One routing step of a released WO, copied from the approved routing version. M5 reads the cycle time
// from here (not from the live routing) and M3 opens SopUrl from here.
public class WorkOrderOperation : IAudited
{
    public int Id { get; private set; }
    public int WorkOrderId { get; private set; }
    public int Seq { get; private set; }
    public string OperationCode { get; private set; }
    public int WorkCenterId { get; private set; }
    public decimal IdealCycleTimeSec { get; private set; }
    public bool IsInspection { get; private set; }
    public bool IsMandatory { get; private set; }
    public string? SopUrl { get; private set; }       // [v2] snapshot too: a running WO keeps the SOP of its routing version
    public int QtyGood { get; private set; }          // counted by M3 scans (atomic SQL update, not read-modify-write)
    public int QtyNg { get; private set; }

    private WorkOrderOperation() { OperationCode = string.Empty; }

    internal WorkOrderOperation(RoutingStepSnapshot step)
    {
        if (step.Seq <= 0) throw new DomainException("Seq must be greater than 0.");
        if (step.IdealCycleTimeSec <= 0) throw new DomainException("IdealCycleTimeSec must be greater than 0.");

        Seq = step.Seq;
        OperationCode = step.OperationCode;
        WorkCenterId = step.WorkCenterId;
        IdealCycleTimeSec = step.IdealCycleTimeSec;
        IsInspection = step.IsInspection;
        IsMandatory = step.IsMandatory;
        SopUrl = step.SopUrl;
    }

    // Production writes the counters with a set-based UPDATE (M3); this is for building states in unit tests.
    internal void RecordOutput(int good, int ng)
    {
        QtyGood += good;
        QtyNg += ng;
    }
}

// Input of WorkOrder.Release: one step of the approved routing.
public sealed record RoutingStepSnapshot(int Seq, string OperationCode, int WorkCenterId, decimal IdealCycleTimeSec,
    bool IsInspection, bool IsMandatory, string? SopUrl);
