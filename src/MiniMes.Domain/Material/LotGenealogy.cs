using MiniMes.Domain.Common;

namespace MiniMes.Domain.Material;

// Spec M6 2.1: "material of Parent was used to make Child" (a reel split into bags, a batch of sub-assemblies).
// Forward trace walks Parent -> Child, backward walks Child -> Parent. Insert-only. No cycles (R12): a lot cannot
// be its own parent here; longer cycles are refused by the use case that writes the edge, and the trace queries
// stop at a depth limit anyway.
public class LotGenealogy
{
    public long Id { get; private set; }
    public int ParentLotId { get; private set; }
    public int ChildLotId { get; private set; }
    public decimal Qty { get; private set; }              // how much of the parent went into the child
    public DateTime CreatedAt { get; private set; }

    private LotGenealogy() { }

    public LotGenealogy(int parentLotId, int childLotId, decimal qty, DateTime createdAt)
    {
        if (parentLotId == childLotId) throw new DomainException("A lot cannot be made from itself.");
        if (qty <= 0) throw new DomainException("Genealogy quantity must be greater than 0.");
        ParentLotId = parentLotId;
        ChildLotId = childLotId;
        Qty = qty;
        CreatedAt = createdAt;
    }
}
