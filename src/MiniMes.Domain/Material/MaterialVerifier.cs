namespace MiniMes.Domain.Material;

// What the verifier needs to know about a lot: a plain snapshot, so the rule is a pure function and the tests can
// describe any lot (Hold, empty, rejected) without driving an entity through its life. The handler builds it.
public sealed record LotFacts(int Id, string LotNo, int ItemId, decimal RemainingQty, DateTime ReceivedAt, DateTime? ExpiryAt,
    MaterialLotStatus Status)
{
    public static LotFacts Of(MaterialLot l) => new(l.Id, l.LotNo, l.ItemId, l.RemainingQty, l.ReceivedAt, l.ExpiryAt, l.Status);
}

// One material of the WO's BOM snapshot (WorkOrderMaterial, M2): which item, consumed at which routing step.
public sealed record WoMaterialRequirement(int ItemId, int OperationSeq, decimal QtyPerUnit);

public sealed record VerifyResult(VerifyOutcome Outcome, VerifyCode Code, string? SuggestedLotNo = null)
{
    public static readonly VerifyResult Ok = new(VerifyOutcome.Accepted, VerifyCode.Ok);
    public static VerifyResult Reject(VerifyCode code) => new(VerifyOutcome.Rejected, code);
}

// Spec M6 2.4: may this reel go on this station for this WO now? Quality problems are a hard stop (R2-R4, R15, no
// override); FIFO is a warning a supervisor may override (R5). The ORDER of the checks is part of the contract and is
// tested: what the reel IS (not in the BOM, wrong station) before what STATE it is in, state before date, FIFO last.
public static class MaterialVerifier
{
    /// <param name="requirements">From the WO snapshot, not the live BOM: an engineer editing the BOM today must not
    /// change what a released order may load.</param>
    /// <param name="stationSeqs">Routing steps of the WO done at this station (its work center); usually one.</param>
    /// <param name="candidates">Other lots of the same item the handler found (not loaded elsewhere).</param>
    public static VerifyResult Verify(LotFacts? lot, IReadOnlyCollection<WoMaterialRequirement> requirements,
        IReadOnlyCollection<int> stationSeqs, IReadOnlyCollection<LotFacts> candidates, DateTime utcNow)
    {
        if (lot is null) return VerifyResult.Reject(VerifyCode.LotNotFound);

        // R2: in the BOM at all, then at a step this station does. An item used at two steps passes if one is here.
        var forItem = requirements.Where(r => r.ItemId == lot.ItemId).ToList();
        if (forItem.Count == 0) return VerifyResult.Reject(VerifyCode.ItemNotInBom);
        if (!forItem.Any(r => stationSeqs.Contains(r.OperationSeq))) return VerifyResult.Reject(VerifyCode.ItemNotForStation);

        switch (lot.Status)
        {
            case MaterialLotStatus.Hold: return VerifyResult.Reject(VerifyCode.LotOnHold);                       // R4
            case MaterialLotStatus.PendingIqc or MaterialLotStatus.IqcRejected:
                return VerifyResult.Reject(VerifyCode.LotNotIqcPassed);                                          // [v2] R15
            case MaterialLotStatus.Consumed: return VerifyResult.Reject(VerifyCode.LotEmpty);
        }
        if (lot.RemainingQty <= 0) return VerifyResult.Reject(VerifyCode.LotEmpty);
        if (lot.ExpiryAt is { } expiry && expiry <= utcNow) return VerifyResult.Reject(VerifyCode.LotExpired);   // R3: at the minute

        return FifoAdvisor.FindOlder(lot, candidates, utcNow) is { } older                                      // R5
            ? new VerifyResult(VerifyOutcome.Warning, VerifyCode.FifoOlderLotAvailable, older.LotNo)
            : VerifyResult.Ok;
    }

    // M6 step 10b: the lot of a part a technician solders on at Repair. The quality checks of a load, minus what does
    // not apply to one part taken by hand: no station (Repair is off the routing), no FIFO, and an empty/Consumed
    // lot passes like at the scan gate (RemainingQty is theoretical; the part is in the technician's hand).
    public static VerifyResult VerifyReplacement(LotFacts? lot, IReadOnlyCollection<WoMaterialRequirement> requirements, DateTime utcNow)
    {
        if (lot is null) return VerifyResult.Reject(VerifyCode.LotNotFound);
        if (requirements.All(r => r.ItemId != lot.ItemId)) return VerifyResult.Reject(VerifyCode.ItemNotInBom);
        return lot.Status switch
        {
            MaterialLotStatus.Hold => VerifyResult.Reject(VerifyCode.LotOnHold),
            MaterialLotStatus.PendingIqc or MaterialLotStatus.IqcRejected => VerifyResult.Reject(VerifyCode.LotNotIqcPassed),
            _ when lot.ExpiryAt is { } expiry && expiry <= utcNow => VerifyResult.Reject(VerifyCode.LotExpired),
            _ => VerifyResult.Ok,
        };
    }
}

// FIFO by ReceivedAt (for FEFO, order by ExpiryAt instead). Only lots someone could actually load instead: Available,
// not empty, not expired. Strictly older: reels of the same delivery (same ReceivedAt) are equal, not "older".
public static class FifoAdvisor
{
    public static LotFacts? FindOlder(LotFacts chosen, IEnumerable<LotFacts> candidates, DateTime utcNow)
        => candidates
            .Where(l => l.ItemId == chosen.ItemId && l.Id != chosen.Id
                        && l.Status == MaterialLotStatus.Available && l.RemainingQty > 0     // [v2] PendingIqc, Hold out
                        && !(l.ExpiryAt <= utcNow)                                           // null expiry: never expired
                        && l.ReceivedAt < chosen.ReceivedAt)
            .OrderBy(l => l.ReceivedAt).ThenBy(l => l.LotNo)
            .FirstOrDefault();
}
