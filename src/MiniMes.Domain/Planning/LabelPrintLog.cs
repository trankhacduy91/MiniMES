using MiniMes.Domain.Common;
using MiniMes.Domain.Packing;

namespace MiniMes.Domain.Planning;

// What a label is for. M2 printed serial labels only; M3b adds carton and pallet labels to the same log.
public enum LabelType
{
    Serial,
    Carton,
    Pallet,
}

// One row per label printed (rule R13). PrintNo > 1 is a reprint and must say why: two physical labels with
// the same serial is how two boards end up sharing one identity. Insert-only, like a scan log: not audited.
// M3b step 4: TargetKey is the code printed as the barcode (serial, carton or pallet number); PrintNo counts per
// (LabelType, TargetKey). SerialId/SerialNo are only set for a serial label; a pallet has no single WO.
public class LabelPrintLog
{
    public const int ReasonMaxLength = 200, TargetKeyMaxLength = Serial.SerialNoMaxLength;

    public long Id { get; private set; }
    public LabelType LabelType { get; private set; }
    public string TargetKey { get; private set; }
    public long? SerialId { get; private set; }
    public string? SerialNo { get; private set; }
    public int? WorkOrderId { get; private set; }
    public int PrintNo { get; private set; }            // 1 = first print; unique per target, so two racing reprints cannot share a number
    public DateTime PrintedAt { get; private set; }     // UTC
    public string PrintedBy { get; private set; }
    public string? Reason { get; private set; }

    private LabelPrintLog() { TargetKey = PrintedBy = string.Empty; }

    private LabelPrintLog(LabelType type, string targetKey, int? workOrderId, int printNo, string printedBy, string? reason, DateTime utcNow)
    {
        if (printNo < 1) throw new DomainException("PrintNo starts at 1.");
        if (printNo > 1 && string.IsNullOrWhiteSpace(reason))
            throw new DomainException($"{type} {targetKey} was already printed; a reprint needs a reason.");
        if (reason is { Length: > ReasonMaxLength }) throw new DomainException($"Reason must be at most {ReasonMaxLength} characters.");
        ArgumentException.ThrowIfNullOrWhiteSpace(printedBy);

        LabelType = type;
        TargetKey = targetKey;
        WorkOrderId = workOrderId;
        PrintNo = printNo;
        PrintedBy = printedBy;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        PrintedAt = utcNow;
    }

    public LabelPrintLog(Serial serial, int printNo, string printedBy, string? reason, DateTime utcNow)
        : this(LabelType.Serial, serial.SerialNo, serial.WorkOrderId, printNo, printedBy, reason, utcNow)
    {
        if (serial.Status == SerialStatus.Scrapped)
            throw new DomainException($"Serial {serial.SerialNo} is scrapped; its label must not be printed.");
        SerialId = serial.Id;
        SerialNo = serial.SerialNo;
    }

    // The label tells the warehouse the quantity inside: an open carton has no final quantity yet.
    public static LabelPrintLog ForCarton(Carton carton, int printNo, string printedBy, string? reason, DateTime utcNow)
    {
        if (carton.Status == CartonStatus.Open) throw new DomainException($"Carton {carton.CartonNo} is still open: it has no label yet.");
        return new LabelPrintLog(LabelType.Carton, carton.CartonNo, carton.WorkOrderId, printNo, printedBy, reason, utcNow);
    }

    public static LabelPrintLog ForPallet(Pallet pallet, int printNo, string printedBy, string? reason, DateTime utcNow)
    {
        if (pallet.Status == PalletStatus.Open) throw new DomainException($"Pallet {pallet.PalletNo} is still open: it has no label yet.");
        return new LabelPrintLog(LabelType.Pallet, pallet.PalletNo, null, printNo, printedBy, reason, utcNow);
    }
}
