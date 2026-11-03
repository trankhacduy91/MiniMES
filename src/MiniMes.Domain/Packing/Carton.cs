using MiniMes.Domain.Common;

namespace MiniMes.Domain.Packing;

public enum CartonStatus
{
    Open,
    Closed,
    Shipped,
}

// Spec M3b 2.1: level 1 of the packing hierarchy. One WO per carton (1.2): one BOM version per carton. Qty counts the
// serials in it now (unpacked ones excluded); adding goes through a conditional UPDATE (2.3), the rules below decide
// whether it may be tried. Hold is not stored here: a carton is on hold when a serial inside is (P10, derived).
public class Carton
{
    public const int NumberMaxLength = 20, ReasonMaxLength = 200;

    public int Id { get; private set; }
    public string CartonNo { get; private set; }
    public int WorkOrderId { get; private set; }
    public int ItemId { get; private set; }
    public int StationId { get; private set; }           // the Pack station filling it (P4: one Open carton per station and WO)
    public int Qty { get; private set; }
    public CartonStatus Status { get; private set; }
    public bool IsPartial { get; private set; }          // closed short by a supervisor (P6)
    public int? PalletId { get; private set; }
    public DateTime OpenedAt { get; private set; }
    public DateTime? ClosedAt { get; private set; }
    public string? ClosedBy { get; private set; }        // null when it closed itself, full (P5)
    public string? CloseReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private Carton() { CartonNo = string.Empty; }

    public Carton(string cartonNo, int workOrderId, int itemId, int stationId, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cartonNo);
        if (cartonNo.Length > NumberMaxLength) throw new DomainException($"Carton number must be at most {NumberMaxLength} characters.");
        CartonNo = cartonNo;
        WorkOrderId = workOrderId;
        ItemId = itemId;
        StationId = stationId;
        Status = CartonStatus.Open;
        OpenedAt = utcNow;
    }

    public bool IsFull(int qtyPerCarton) => Qty >= qtyPerCarton;

    // P2 + P5, the checks before the conditional UPDATE: state first (a closed carton refuses anything), then what the
    // serial is (another model is the serious one), then room. Null = may be added.
    public PackBlock? CheckAdd(int serialWorkOrderId, int serialItemId, int qtyPerCarton)
    {
        if (Status != CartonStatus.Open) return PackBlock.CartonNotOpen;
        if (serialItemId != ItemId) return PackBlock.WrongItem;
        if (serialWorkOrderId != WorkOrderId) return PackBlock.WrongWorkOrder;
        if (IsFull(qtyPerCarton)) return PackBlock.CartonFull;
        return null;
    }

    // P6: the last carton of a WO, short. A supervisor decides it and says why; a full carton closes itself instead,
    // and an empty one is simply not used.
    public void ClosePartial(string reason, string by, int qtyPerCarton, DateTime utcNow)
    {
        if (Status != CartonStatus.Open) throw new DomainException($"Carton {CartonNo} is not open.");
        if (Qty == 0) throw new DomainException($"Carton {CartonNo} is empty: nothing to close.");
        if (IsFull(qtyPerCarton)) throw new DomainException($"Carton {CartonNo} is full: it closes by itself.");
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        var checkedReason = RequireReason(reason);   // every check before the first change: a refusal leaves it as it was
        Status = CartonStatus.Closed;
        IsPartial = true;
        ClosedAt = utcNow;
        ClosedBy = by;
        CloseReason = checkedReason;
    }

    // P9: take one serial out, keeping its row as history. Not once the carton is on a pallet (take it off first) or
    // shipped (P8). A closed carton opens again so it can be filled up, then it closes itself and its label is printed
    // again (P11; user decision M3b step 2). Reopening fails on the database (P4) when the station already has an Open
    // carton of this WO: close that one first.
    public void Unpack(CartonSerial row, string by, string reason, DateTime utcNow)
    {
        if (Status == CartonStatus.Shipped) throw new DomainException($"Carton {CartonNo} is shipped: it cannot be changed.");
        if (PalletId is not null) throw new DomainException($"Carton {CartonNo} is on a pallet: take it off the pallet first.");
        if (row.CartonId != Id || row.UnpackedAt is not null) throw new DomainException($"The serial is not in carton {CartonNo}.");
        row.MarkUnpacked(by, RequireReason(reason), utcNow);
        Qty--;
        if (Status == CartonStatus.Closed) Reopen();
    }

    public void PutOnPallet(int palletId)
    {
        if (Status != CartonStatus.Closed) throw new DomainException($"Carton {CartonNo} is not closed.");
        if (PalletId is not null) throw new DomainException($"Carton {CartonNo} is already on a pallet.");
        PalletId = palletId;
    }

    // Called by Pallet.RemoveCarton, which checks the pallet side (not shipped).
    internal void TakeOffPallet() => PalletId = null;

    internal void MarkShipped() => Status = CartonStatus.Shipped;

    private void Reopen()
    {
        Status = CartonStatus.Open;
        IsPartial = false;
        ClosedAt = null;
        ClosedBy = CloseReason = null;
    }

    internal static string RequireReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reason is required.");
        reason = reason.Trim();
        if (reason.Length > ReasonMaxLength) throw new DomainException($"The reason must be at most {ReasonMaxLength} characters.");
        return reason;
    }
}
