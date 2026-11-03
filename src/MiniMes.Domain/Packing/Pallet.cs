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
    public DateTime OpenedAt { get; private set; }
    public DateTime? ClosedAt { get; private set; }
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
}
