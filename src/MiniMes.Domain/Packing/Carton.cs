using MiniMes.Domain.Common;

namespace MiniMes.Domain.Packing;

public enum CartonStatus
{
    Open,
    Closed,
    Shipped,
}

// Spec M3b 2.1: level 1 of the packing hierarchy. One WO per carton (1.2): one BOM version per carton. Qty counts the
// serials in it now (unpacked ones excluded) and only ever moves through a conditional UPDATE (2.3). Hold is not
// stored here: a carton is on hold when a serial inside is (P10, derived).
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
}
