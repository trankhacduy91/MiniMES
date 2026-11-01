using MiniMes.Domain.Common;

namespace MiniMes.Domain.Material;

// Spec M6 1.2 / R6: a lot put on a station (the reel on a feeder of the machine). One open load per (station, item):
// every OK scan must know exactly which lot it used. The database enforces it (filtered unique index).
// Not IAudited: the row itself says who loaded, who approved a FIFO exception and why (spec 2.4).
public class MaterialLoad
{
    public const int SlotNoMaxLength = 20;
    public const int ReasonMaxLength = 200;

    public int Id { get; private set; }
    public int StationId { get; private set; }
    public int ItemId { get; private set; }
    public int LotId { get; private set; }
    public int WorkOrderId { get; private set; }         // the WO the load was verified against (its BOM snapshot)
    public string? SlotNo { get; private set; }          // optional feeder slot (no slot table in the MVP, spec 1.5)
    public string LoadedBy { get; private set; }
    public DateTime LoadedAt { get; private set; }
    public string? FifoOverrideBy { get; private set; }  // supervisor who let a newer lot go first (R5)
    public string? FifoOverrideReason { get; private set; }
    public DateTime? UnloadedAt { get; private set; }    // null = on the station now
    public string? UnloadedBy { get; private set; }

    private MaterialLoad() { LoadedBy = string.Empty; }

    public MaterialLoad(int stationId, int itemId, int lotId, int workOrderId, string? slotNo, string loadedBy, DateTime loadedAt,
        string? fifoOverrideBy = null, string? fifoOverrideReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loadedBy);
        if (slotNo?.Trim().Length > SlotNoMaxLength) throw new DomainException($"Slot must be at most {SlotNoMaxLength} characters.");
        if (string.IsNullOrWhiteSpace(fifoOverrideBy) != string.IsNullOrWhiteSpace(fifoOverrideReason)
            || fifoOverrideReason?.Trim().Length > ReasonMaxLength)
            throw new DomainException($"A FIFO override needs both the supervisor and a reason (at most {ReasonMaxLength} characters).");

        StationId = stationId;
        ItemId = itemId;
        LotId = lotId;
        WorkOrderId = workOrderId;
        SlotNo = string.IsNullOrWhiteSpace(slotNo) ? null : slotNo.Trim();
        LoadedBy = loadedBy;
        LoadedAt = loadedAt;
        FifoOverrideBy = string.IsNullOrWhiteSpace(fifoOverrideBy) ? null : fifoOverrideBy;
        FifoOverrideReason = string.IsNullOrWhiteSpace(fifoOverrideReason) ? null : fifoOverrideReason.Trim();
    }

    public bool IsActive => UnloadedAt is null;

    // A newer lot of the same item, the reel ran out, or taken off: closes the load (R6).
    public void Unload(string by, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        if (!IsActive) throw new DomainException("This lot is already unloaded.");
        if (utcNow < LoadedAt) throw new DomainException("Unload time is before the load time.");
        UnloadedAt = utcNow;
        UnloadedBy = by;
    }
}
