using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;

namespace MiniMes.Domain.Planning;

public enum SerialStatus
{
    Created,    // label may be printed, not on the line yet
    InProcess,  // passed at least one step (M3)
    Ng,         // failed an inspection step, waiting for repair
    Repair,
    Completed,  // passed the last step
    Scrapped,   // cannot be repaired, or its WO was cancelled before it started
    Hold,       // blocked by quality (M7)
}

// One unit of product. Its own aggregate, not a child of WorkOrder: a 10,000 pcs WO would otherwise
// load 10,000 serials on every status change (spec M2 2.3). Not audited: it is a high-volume row (M3 scans).
public class Serial
{
    public const int SerialNoMaxLength = 30;
    public const int ScrapReasonMaxLength = 200;

    public long Id { get; private set; }
    public string SerialNo { get; private set; }
    public int WorkOrderId { get; private set; }
    public int ItemId { get; private set; }
    public int CurrentSeq { get; private set; }     // last routing step passed; 0 = not started (M3 route check)
    public SerialStatus Status { get; private set; } = SerialStatus.Created;
    public string? ScrapReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];   // M3: two scans of one board at once -> one wins, the other re-reads

    private Serial() { SerialNo = string.Empty; }

    public Serial(string serialNo, int workOrderId, int itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNo);
        if (serialNo.Length > SerialNoMaxLength)
            throw new DomainException($"Serial number must be at most {SerialNoMaxLength} characters.");

        SerialNo = serialNo;
        WorkOrderId = workOrderId;
        ItemId = itemId;
    }

    // M3: the board arrives at the Repair station. Scanning it again while it is there changes nothing.
    public void CheckInRepair()
    {
        if (Status is not (SerialStatus.Ng or SerialStatus.Repair))
            throw new DomainException($"Serial {SerialNo} is {Status}; only NG boards are repaired.");
        Status = SerialStatus.Repair;
    }

    // M3: record a scan that route check allowed at a routing step. OK moves the board to that step (the last
    // step completes it); NG keeps the step (the failed test is done again after Repair) and marks it NG.
    public void ApplyScan(int stepSeq, bool isLastStep, ScanResult result)
    {
        if (Status is not (SerialStatus.Created or SerialStatus.InProcess))
            throw new DomainException($"Serial {SerialNo} is {Status}; it cannot be scanned at a routing step.");
        if (stepSeq <= CurrentSeq)
            throw new DomainException($"Serial {SerialNo} already passed step {stepSeq}.");

        if (result == ScanResult.Ng)
        {
            Status = SerialStatus.Ng;
            return;
        }
        CurrentSeq = stepSeq;
        Status = isLastStep ? SerialStatus.Completed : SerialStatus.InProcess;
    }
}
