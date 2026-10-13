using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Boms;

// Bill of materials: how much of each component one unit of ItemId needs.
// Versioned; validity is the half-open range [EffectiveFrom, EffectiveTo), EffectiveTo null = open.
public class Bom : AuditableEntity, IAudited
{
    public int Id { get; private set; }
    public int ItemId { get; private set; }
    public int Version { get; private set; }
    public VersionStatus Status { get; private set; } = VersionStatus.Draft;
    public DateTime? EffectiveFrom { get; private set; }
    public DateTime? EffectiveTo { get; private set; }

    public List<BomLine> Lines { get; private set; } = [];

    private Bom() { }

    public static Bom CreateDraft(int itemId, int version)
    {
        if (version < 1) throw new DomainException("BOM version starts at 1.");
        return new Bom { ItemId = itemId, Version = version };
    }

    public BomLine AddLine(int componentItemId, decimal qtyPer, decimal scrapRate, int operationSeq)
    {
        EnsureDraft();
        if (componentItemId == ItemId)
            throw new DomainException("A BOM line cannot use the parent item itself.");
        if (Lines.Any(l => l.ComponentItemId == componentItemId))
            throw new DomainException("The component is already in this BOM; change its quantity instead.");

        var line = new BomLine(componentItemId, qtyPer, scrapRate, operationSeq);
        Lines.Add(line);
        return line;
    }

    // Checks that only need this BOM. Cross-BOM checks (no cycle, active components, closing the
    // previous version's validity) belong to the Approve use case (M1 step 5).
    public void Approve(DateTime effectiveFromUtc)
    {
        EnsureDraft();
        if (Lines.Count == 0) throw new DomainException("An empty BOM cannot be approved.");
        Status = VersionStatus.Approved;
        EffectiveFrom = effectiveFromUtc;
    }

    private void EnsureDraft()
    {
        if (Status != VersionStatus.Draft)
            throw new DomainException($"BOM version {Version} is {Status} and locked; create a new version to change it.");
    }
}
