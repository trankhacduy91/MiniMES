namespace MiniMes.Domain.Material;

// Spec M6 2.4: three levels, not two. Warning = allowed after a supervisor confirms (FIFO only).
public enum VerifyOutcome
{
    Accepted,
    Warning,
    Rejected,
}

// Machine-readable reason: the kiosk picks its text from Strings.resx, reports count by it.
public enum VerifyCode
{
    Ok,
    LotNotFound,
    ItemNotInBom,
    ItemNotForStation,
    LotOnHold,
    LotNotIqcPassed,         // [v2] R15, no override
    LotEmpty,
    LotExpired,
    FifoOlderLotAvailable,
}

// Spec M6 R13: every load attempt, rejected ones and overrides included: proof for a customer that the system
// stopped wrong reels, and data on who loads wrong reels. Insert-only (R11).
public class MaterialVerificationLog
{
    public long Id { get; private set; }
    public int StationId { get; private set; }
    public string ScannedLotNo { get; private set; }     // what was scanned, even when no such lot exists
    public int? LotId { get; private set; }
    public int? ItemId { get; private set; }
    public int? WorkOrderId { get; private set; }
    public VerifyOutcome Outcome { get; private set; }
    public VerifyCode Code { get; private set; }
    public string? SuggestedLotNo { get; private set; }  // the older lot FIFO pointed to
    public string By { get; private set; }
    public DateTime At { get; private set; }
    public string? OverrideBy { get; private set; }
    public string? OverrideReason { get; private set; }

    private MaterialVerificationLog() { ScannedLotNo = By = string.Empty; }

    public MaterialVerificationLog(int stationId, string scannedLotNo, int? lotId, int? itemId, int? workOrderId,
        VerifyOutcome outcome, VerifyCode code, string? suggestedLotNo, string by, DateTime at,
        string? overrideBy = null, string? overrideReason = null)
    {
        ArgumentNullException.ThrowIfNull(scannedLotNo);
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        StationId = stationId;
        // A label that does not scan as a lot is still logged; long garbage is cut, not refused.
        ScannedLotNo = scannedLotNo.Length > MaterialLot.LotNoMaxLength ? scannedLotNo[..MaterialLot.LotNoMaxLength] : scannedLotNo;
        LotId = lotId;
        ItemId = itemId;
        WorkOrderId = workOrderId;
        Outcome = outcome;
        Code = code;
        SuggestedLotNo = suggestedLotNo;
        By = by;
        At = at;
        OverrideBy = overrideBy;
        OverrideReason = overrideReason;
    }
}
