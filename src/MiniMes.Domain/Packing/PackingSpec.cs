using MiniMes.Domain.Common;

namespace MiniMes.Domain.Packing;

// Spec M3b 1.2: how a finished item is packed. Small master data, one row per item; changes are audited (M0).
public class PackingSpec : IAudited
{
    public const int MaxQtyPerCarton = 1_000, MaxCartonsPerPallet = 200;

    public int ItemId { get; private set; }
    public int QtyPerCarton { get; private set; }
    public int CartonsPerPallet { get; private set; }

    private PackingSpec() { }

    public PackingSpec(int itemId, int qtyPerCarton, int cartonsPerPallet)
    {
        ItemId = itemId;
        Change(qtyPerCarton, cartonsPerPallet);
    }

    public void Change(int qtyPerCarton, int cartonsPerPallet)
    {
        if (qtyPerCarton is < 1 or > MaxQtyPerCarton)
            throw new DomainException($"Quantity per carton must be between 1 and {MaxQtyPerCarton}.");
        if (cartonsPerPallet is < 1 or > MaxCartonsPerPallet)
            throw new DomainException($"Cartons per pallet must be between 1 and {MaxCartonsPerPallet}.");
        QtyPerCarton = qtyPerCarton;
        CartonsPerPallet = cartonsPerPallet;
    }
}
