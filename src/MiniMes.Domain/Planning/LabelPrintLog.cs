using MiniMes.Domain.Common;

namespace MiniMes.Domain.Planning;

// One row per label printed (rule R13). PrintNo > 1 is a reprint and must say why: two physical labels with
// the same serial is how two boards end up sharing one identity. Insert-only, like a scan log: not audited.
public class LabelPrintLog
{
    public const int ReasonMaxLength = 200;

    public long Id { get; private set; }
    public long SerialId { get; private set; }
    public string SerialNo { get; private set; }
    public int WorkOrderId { get; private set; }
    public int PrintNo { get; private set; }            // 1 = first print; unique per serial, so two racing reprints cannot share a number
    public DateTime PrintedAt { get; private set; }     // UTC
    public string PrintedBy { get; private set; }
    public string? Reason { get; private set; }

    private LabelPrintLog() { SerialNo = PrintedBy = string.Empty; }

    public LabelPrintLog(Serial serial, int printNo, string printedBy, string? reason, DateTime utcNow)
    {
        if (printNo < 1) throw new DomainException("PrintNo starts at 1.");
        if (printNo > 1 && string.IsNullOrWhiteSpace(reason))
            throw new DomainException($"Serial {serial.SerialNo} was already printed; a reprint needs a reason.");
        if (reason is { Length: > ReasonMaxLength }) throw new DomainException($"Reason must be at most {ReasonMaxLength} characters.");
        if (serial.Status == SerialStatus.Scrapped)
            throw new DomainException($"Serial {serial.SerialNo} is scrapped; its label must not be printed.");
        ArgumentException.ThrowIfNullOrWhiteSpace(printedBy);

        SerialId = serial.Id;
        SerialNo = serial.SerialNo;
        WorkOrderId = serial.WorkOrderId;
        PrintNo = printNo;
        PrintedBy = printedBy;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        PrintedAt = utcNow;
    }
}
