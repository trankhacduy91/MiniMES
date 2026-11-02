namespace MiniMes.Domain.Material;

// The lot a station holds for one item, as the scan gate sees it.
public sealed record LoadedLot(int ItemId, string LotNo, MaterialLotStatus Status, DateTime? ExpiryAt);

public enum GateProblem
{
    NotLoaded,      // R8: the step consumes the item but no reel of it is on the station
    LotOnHold,      // R4: put on Hold after it was loaded (M7 containment)
    LotNotUsable,   // PendingIqc / IqcRejected: cannot be loaded normally, checked anyway
    LotExpired,     // R3: expired during the shift
}

public sealed record GateFinding(int ItemId, string? LotNo, GateProblem Problem);

// Spec M6 2.5, the gate before a scan OK: every lot-controlled item the step consumes must have a usable lot on the
// station. A pure function like MaterialVerifier, so every case is a unit test.
public static class MaterialGateRule
{
    // Empty = the scan may go on. Consumed is fine on purpose: RemainingQty is theoretical and floors at 0 while
    // the reel still has parts (spec 1.5, R10); the operator corrects it when taking the reel off.
    public static IReadOnlyList<GateFinding> Check(IEnumerable<int> itemsAtStep, IReadOnlyCollection<LoadedLot> loaded, DateTime utcNow)
    {
        var findings = new List<GateFinding>();
        foreach (var itemId in itemsAtStep.Distinct().Order())
        {
            var lot = loaded.FirstOrDefault(l => l.ItemId == itemId);   // R6: at most one per item
            var problem = lot switch
            {
                null => GateProblem.NotLoaded,
                { Status: MaterialLotStatus.Hold } => GateProblem.LotOnHold,
                { Status: MaterialLotStatus.PendingIqc or MaterialLotStatus.IqcRejected } => GateProblem.LotNotUsable,
                { ExpiryAt: { } expiry } when expiry <= utcNow => GateProblem.LotExpired,   // same boundary as MaterialVerifier
                _ => (GateProblem?)null,
            };
            if (problem is { } p) findings.Add(new GateFinding(itemId, lot?.LotNo, p));
        }
        return findings;
    }
}
