using MiniMes.Domain.Common;

namespace MiniMes.Domain.Material;

// Stored as numbers: the atomic stock UPDATE of step 5 compares them in SQL (spec M6 2.1).
public enum MaterialLotStatus : byte
{
    Available = 0,
    Hold = 1,          // blocked by quality (M7, or "Hold lot" on the trace screen)
    Consumed = 2,      // empty: the end of its life, never deleted (spec 1.5)
    PendingIqc = 3,    // [v2] received, incoming inspection not done yet (R15)
    IqcRejected = 4,   // [v2] failed IQC; only an NCR decision brings it back (R17)
}

// Spec M6 1.2: one reel = one lot. LotNo is the code printed on the label; SupplierLot groups the reels of one
// delivery (a supplier recall speaks in supplier lots). Never deleted: a used lot is evidence for traceability.
// Audited: status changes (Hold, IQC) and the stock correction at unload are decisions someone made.
public class MaterialLot : IAudited
{
    public const int LotNoMaxLength = 30;
    public const int SupplierLotMaxLength = 50;

    public int Id { get; private set; }
    public string LotNo { get; private set; }
    public int ItemId { get; private set; }
    public decimal Qty { get; private set; }
    public decimal RemainingQty { get; private set; }   // theoretical: scans subtract QtyPer, real loss is not seen (R10)
    public string? SupplierLot { get; private set; }
    public DateTime ReceivedAt { get; private set; }    // UTC; FIFO order
    public DateTime? ExpiryAt { get; private set; }     // UTC; expired = blocked, no override (R3)
    public MaterialLotStatus Status { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private MaterialLot() { LotNo = string.Empty; }

    public MaterialLot(string lotNo, int itemId, decimal qty, string? supplierLot, DateTime receivedAt, DateTime? expiryAt,
        bool needsIqc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lotNo);
        if (lotNo.Length > LotNoMaxLength) throw new DomainException($"Lot number must be at most {LotNoMaxLength} characters.");
        if (qty <= 0) throw new DomainException("Lot quantity must be greater than 0.");
        if (supplierLot?.Trim().Length > SupplierLotMaxLength)
            throw new DomainException($"Supplier lot must be at most {SupplierLotMaxLength} characters.");
        if (expiryAt <= receivedAt) throw new DomainException("Expiry must be after the receiving date.");

        LotNo = lotNo;
        ItemId = itemId;
        Qty = RemainingQty = qty;
        SupplierLot = string.IsNullOrWhiteSpace(supplierLot) ? null : supplierLot.Trim();
        ReceivedAt = receivedAt;
        ExpiryAt = expiryAt;
        Status = needsIqc ? MaterialLotStatus.PendingIqc : MaterialLotStatus.Available;   // [v2] R15
    }
}
