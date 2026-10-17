using MiniMes.Domain.Common;

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
}
