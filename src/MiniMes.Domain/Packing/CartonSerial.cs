namespace MiniMes.Domain.Packing;

// Spec M3b 2.1: "serial X is (or was) in carton Y". Unpack fills the last three columns and keeps the row (P9): the
// trace still knows every carton a serial was in. A filtered unique index allows one live row per serial (P3).
public class CartonSerial
{
    public long Id { get; private set; }
    public int CartonId { get; private set; }
    public long SerialId { get; private set; }
    public DateTime PackedAt { get; private set; }
    public string PackedBy { get; private set; }
    public DateTime? UnpackedAt { get; private set; }
    public string? UnpackedBy { get; private set; }
    public string? UnpackReason { get; private set; }

    private CartonSerial() { PackedBy = string.Empty; }

    public CartonSerial(int cartonId, long serialId, string packedBy, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packedBy);
        CartonId = cartonId;
        SerialId = serialId;
        PackedBy = packedBy;
        PackedAt = utcNow;
    }

    // Only through Carton.Unpack, which checks the carton (P8, P9) and keeps its Qty right.
    internal void MarkUnpacked(string by, string reason, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        UnpackedAt = utcNow;
        UnpackedBy = by;
        UnpackReason = reason;
    }
}
