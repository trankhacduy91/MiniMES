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

    // Saving a draft replaces all its lines (the editor sends the full list).
    public void ClearLines()
    {
        EnsureDraft();
        Lines.Clear();
    }

    // previous = the item's currently open Approved version (EffectiveTo null), or null for the first one.
    // Its validity is closed exactly where this one starts: [From, To) ranges never overlap (spec M1 rule 5).
    // Cross-BOM checks (active components, no cycle) need the database and live in the Approve use case.
    public void Approve(DateTime effectiveFromUtc, Bom? previous)
    {
        EnsureDraft();
        if (Lines.Count == 0) throw new DomainException("An empty BOM cannot be approved.");
        if (previous is not null)
        {
            if (previous.ItemId != ItemId || previous.Status != VersionStatus.Approved || previous.EffectiveTo is not null)
                throw new InvalidOperationException("previous must be the open Approved BOM of the same item.");
            if (effectiveFromUtc <= previous.EffectiveFrom)
                throw new DomainException(
                    $"EffectiveFrom must be after the start of version {previous.Version} ({previous.EffectiveFrom:yyyy-MM-dd HH:mm} UTC).");
            previous.EffectiveTo = effectiveFromUtc;
        }

        Status = VersionStatus.Approved;
        EffectiveFrom = effectiveFromUtc;
    }

    private void EnsureDraft()
    {
        if (Status != VersionStatus.Draft)
            throw new DomainException($"BOM version {Version} is {Status} and locked; create a new version to change it.");
    }
}
