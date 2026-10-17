using MiniMes.Domain.Common;
using MiniMes.Domain.Planning;

namespace MiniMes.Domain.Execution;

// What the repair technician did to an NG board (spec M3 1.2): the action, the part replaced (traceability
// of the new component) and where the board goes back in the routing. Insert-only like ScanLog.
public class RepairLog
{
    public const int ActionMaxLength = 200;
    public const int ComponentMaxLength = 100;

    public long Id { get; private set; }
    public long SerialId { get; private set; }
    public string SerialNo { get; private set; }
    public int WorkOrderId { get; private set; }
    public int StationId { get; private set; }
    public string DefectCode { get; private set; }          // the defect that was repaired
    public string Action { get; private set; }              // "Reflow solder joint U3"
    public string? ComponentReplaced { get; private set; }  // "U3 IC-MCU-QFN lot L2610-07"; null when nothing was replaced
    public int? ReturnToSeq { get; private set; }           // rework: the step the board must do again; null = retest the failed step
    public string RepairedBy { get; private set; }
    public DateTime RepairedAt { get; private set; }

    private RepairLog() { SerialNo = DefectCode = Action = RepairedBy = string.Empty; }

    public RepairLog(Serial serial, int stationId, string defectCode, string action, string? componentReplaced,
        int? returnToSeq, string repairedBy, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defectCode);
        if (string.IsNullOrWhiteSpace(action)) throw new DomainException("Repair needs the action taken.");
        if (action.Length > ActionMaxLength) throw new DomainException($"Action must be at most {ActionMaxLength} characters.");
        if (componentReplaced is { Length: > ComponentMaxLength })
            throw new DomainException($"ComponentReplaced must be at most {ComponentMaxLength} characters.");
        ArgumentException.ThrowIfNullOrWhiteSpace(repairedBy);

        SerialId = serial.Id;
        SerialNo = serial.SerialNo;
        WorkOrderId = serial.WorkOrderId;
        StationId = stationId;
        DefectCode = defectCode;
        Action = action.Trim();
        ComponentReplaced = string.IsNullOrWhiteSpace(componentReplaced) ? null : componentReplaced.Trim();
        ReturnToSeq = returnToSeq;
        RepairedBy = repairedBy;
        RepairedAt = utcNow;
    }
}
