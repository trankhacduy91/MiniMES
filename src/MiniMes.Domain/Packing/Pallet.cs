using MiniMes.Domain.Common;

namespace MiniMes.Domain.Packing;

public enum PalletStatus
{
    Open,
    Closed,
    Shipped,
}

// Spec M3b 2.1: level 2, closed cartons of one item (P7). Filled at a Pack station in Palletize mode: one Open pallet
// per station and item (added to the spec's columns, same idea as P4). Shipped = left the plant, frozen (P8).
public class Pallet
{
    public const int NumberMaxLength = 20, ShipmentRefMaxLength = 50;

    public int Id { get; private set; }
    public string PalletNo { get; private set; }
    public int ItemId { get; private set; }
    public int StationId { get; private set; }
    public int CartonCount { get; private set; }
    public PalletStatus Status { get; private set; }
    public bool IsPartial { get; private set; }          // closed short by a supervisor (user decision, M3b step 2)
    public DateTime OpenedAt { get; private set; }
    public DateTime? ClosedAt { get; private set; }
    public string? ClosedBy { get; private set; }        // null when it closed itself, full
    public string? CloseReason { get; private set; }
    public DateTime? ShippedAt { get; private set; }
    public string? ShippedBy { get; private set; }
    public string? ShipmentRef { get; private set; }     // delivery order number (DO-2610-015)
    public byte[] RowVersion { get; private set; } = [];

    private Pallet() { PalletNo = string.Empty; }

    public Pallet(string palletNo, int itemId, int stationId, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(palletNo);
        if (palletNo.Length > NumberMaxLength) throw new DomainException($"Pallet number must be at most {NumberMaxLength} characters.");
        PalletNo = palletNo;
        ItemId = itemId;
        StationId = stationId;
        Status = PalletStatus.Open;
        OpenedAt = utcNow;
    }

    public bool IsFull(int cartonsPerPallet) => CartonCount >= cartonsPerPallet;

    // P7, before the conditional UPDATE of CartonCount: the pallet's state, what the carton is, its state, Hold
    // (derived from its serials, P10), room. Null = may be added.
    public PalletizeBlock? CheckAdd(Carton carton, bool cartonOnHold, int cartonsPerPallet)
    {
        if (Status != PalletStatus.Open) return PalletizeBlock.PalletNotOpen;
        if (carton.ItemId != ItemId) return PalletizeBlock.WrongItem;
        if (carton.Status != CartonStatus.Closed) return PalletizeBlock.CartonNotClosed;
        if (carton.PalletId is not null) return PalletizeBlock.CartonOnAPallet;
        if (cartonOnHold) return PalletizeBlock.CartonOnHold;
        if (IsFull(cartonsPerPallet)) return PalletizeBlock.PalletFull;
        return null;
    }

    // Like Carton.ClosePartial (P6): the last pallet of a run, short, so it can be shipped.
    public void ClosePartial(string reason, string by, int cartonsPerPallet, DateTime utcNow)
    {
        if (Status != PalletStatus.Open) throw new DomainException($"Pallet {PalletNo} is not open.");
        if (CartonCount == 0) throw new DomainException($"Pallet {PalletNo} is empty: nothing to close.");
        if (IsFull(cartonsPerPallet)) throw new DomainException($"Pallet {PalletNo} is full: it closes by itself.");
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        var checkedReason = Carton.RequireReason(reason);   // checks first, then changes
        Status = PalletStatus.Closed;
        IsPartial = true;
        ClosedAt = utcNow;
        ClosedBy = by;
        CloseReason = checkedReason;
    }

    // P9 prerequisite: a carton must come off its pallet before a serial can be unpacked from it. A closed pallet opens
    // again (same rule as a carton after unpack); a shipped one is frozen (P8).
    public void RemoveCarton(Carton carton)
    {
        if (Status == PalletStatus.Shipped) throw new DomainException($"Pallet {PalletNo} is shipped: it cannot be changed.");
        if (carton.PalletId != Id) throw new DomainException($"Carton {carton.CartonNo} is not on pallet {PalletNo}.");
        carton.TakeOffPallet();
        CartonCount--;
        if (Status == PalletStatus.Closed)
        {
            Status = PalletStatus.Open;
            IsPartial = false;
            ClosedAt = null;
            ClosedBy = CloseReason = null;
        }
    }

    // P8: only a closed pallet without a held serial leaves the plant; then it and its cartons are frozen.
    public void Ship(string shipmentRef, bool hasHeldSerial, IEnumerable<Carton> cartons, string by, DateTime utcNow)
    {
        if (Status == PalletStatus.Shipped) throw new DomainException($"Pallet {PalletNo} is already shipped.");
        if (Status != PalletStatus.Closed) throw new DomainException($"Pallet {PalletNo} is not closed: close it before shipping.");
        if (hasHeldSerial) throw new DomainException($"Pallet {PalletNo} contains held serials.");
        if (string.IsNullOrWhiteSpace(shipmentRef)) throw new DomainException("A shipment reference (delivery order) is required.");
        shipmentRef = shipmentRef.Trim();
        if (shipmentRef.Length > ShipmentRefMaxLength)
            throw new DomainException($"The shipment reference must be at most {ShipmentRefMaxLength} characters.");
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        var list = cartons.ToList();
        // The caller loads the cartons with the pallet: a different count means the pallet changed meanwhile.
        if (list.Count != CartonCount) throw new DomainException($"Pallet {PalletNo} has {CartonCount} cartons, {list.Count} given.");
        if (list.FirstOrDefault(c => c.PalletId != Id) is { } stranger)
            throw new DomainException($"Carton {stranger.CartonNo} is not on pallet {PalletNo}.");
        foreach (var carton in list) carton.MarkShipped();   // after every check: a refused ship changes no carton
        Status = PalletStatus.Shipped;
        ShipmentRef = shipmentRef;
        ShippedAt = utcNow;
        ShippedBy = by;
    }
}
