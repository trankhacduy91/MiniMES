using MiniMes.Domain.Common;

namespace MiniMes.Domain.Material;

// Spec M6 1.2 / R7: "serial X used lot Y", written when the serial is scanned OK at a station that consumes the
// item. The most important row of the module: every trace question is a query on these. Insert-only (R11, the
// database refuses UPDATE/DELETE); one row per (serial, station, item, lot), so a repeated scan adds nothing (R9).
// Ids, not codes (user decision, noted in the spec): the table grows to ~100 M rows, narrow keys keep it small.
public class SerialComponent
{
    public long Id { get; private set; }
    public long SerialId { get; private set; }
    public int LotId { get; private set; }
    public int ItemId { get; private set; }
    public decimal Qty { get; private set; }             // QtyPerUnit of the WO material snapshot; 1 for a repaired part
    public int StationId { get; private set; }
    public long? ScanLogId { get; private set; }         // the scan that produced it: operator, time, result
    public long? RepairLogId { get; private set; }       // or the repair that put a new part on the board (exactly one is set)
    public DateTime ConsumedAt { get; private set; }

    private SerialComponent() { }

    public SerialComponent(long serialId, int lotId, int itemId, decimal qty, int stationId, long scanLogId, DateTime consumedAt)
        : this(serialId, lotId, itemId, qty, stationId, consumedAt)
        => ScanLogId = scanLogId;

    // M6 step 10b: a part replaced at Repair is consumption too. Without it the backward trace of a repaired board
    // misses the new part's lot, and a recall of that lot misses the board.
    public static SerialComponent FromRepair(long serialId, int lotId, int itemId, decimal qty, int stationId, long repairLogId, DateTime repairedAt)
        => new(serialId, lotId, itemId, qty, stationId, repairedAt) { RepairLogId = repairLogId };

    private SerialComponent(long serialId, int lotId, int itemId, decimal qty, int stationId, DateTime consumedAt)
    {
        if (qty <= 0) throw new DomainException("Consumed quantity must be greater than 0.");
        SerialId = serialId;
        LotId = lotId;
        ItemId = itemId;
        Qty = qty;
        StationId = stationId;
        ConsumedAt = consumedAt;
    }
}
