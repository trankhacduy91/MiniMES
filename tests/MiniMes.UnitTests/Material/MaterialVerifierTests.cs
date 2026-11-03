using MiniMes.Domain.Material;

namespace MiniMes.UnitTests.Material;

// Spec M6 step 3 / 2.4 test table (>= 12 cases, 3 of them IQC). Scenario of spec 1.3 A: WO of PCB-A needs
// CAP-100N (item 1) and RES-100K (item 2) at SMT (seq 10), and IC-MCU (item 3) at ASSY (seq 20).
// The kiosk is on the SMT station; now = 2026-10-01 01:00 UTC.
public class MaterialVerifierTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);
    private const int Cap = 1, Res = 2, Mcu = 3, Res10K = 9;

    private static readonly WoMaterialRequirement[] Bom = [new(Cap, 10, 4), new(Res, 10, 1), new(Mcu, 20, 1)];
    private static readonly int[] Smt = [10];

    // The reel scanned: a new reel of capacitors received 15/09, 10,000 left, expires in 2 years.
    private static readonly LotFacts NewCap = new(100, "L-260915-000001", Cap, 10_000, new(2026, 9, 15, 0, 30, 0, DateTimeKind.Utc),
        new(2028, 9, 15, 0, 0, 0, DateTimeKind.Utc), MaterialLotStatus.Available);

    // An older reel of the same item, received 01/09, 3,200 left.
    private static readonly LotFacts OldCap = NewCap with { Id = 50, LotNo = "L-260901-000002", RemainingQty = 3_200,
        ReceivedAt = new(2026, 9, 1, 0, 30, 0, DateTimeKind.Utc) };

    public static TheoryData<string, LotFacts?, LotFacts[], VerifyOutcome, VerifyCode, string?> Cases() => new()
    {
        { "right item, right station, no older lot", NewCap, [], VerifyOutcome.Accepted, VerifyCode.Ok, null },
        { "label of no lot", null, [], VerifyOutcome.Rejected, VerifyCode.LotNotFound, null },
        // 1.3 A #4: RES-10K instead of RES-100K. They look the same; only the label tells them apart.
        { "item not in the BOM", NewCap with { ItemId = Res10K }, [], VerifyOutcome.Rejected, VerifyCode.ItemNotInBom, null },
        { "item of the BOM, consumed at another step", NewCap with { ItemId = Mcu }, [], VerifyOutcome.Rejected, VerifyCode.ItemNotForStation, null },
        { "lot on hold", NewCap with { Status = MaterialLotStatus.Hold }, [], VerifyOutcome.Rejected, VerifyCode.LotOnHold, null },
        { "[v2] lot pending IQC", NewCap with { Status = MaterialLotStatus.PendingIqc }, [], VerifyOutcome.Rejected, VerifyCode.LotNotIqcPassed, null },
        { "[v2] lot rejected by IQC", NewCap with { Status = MaterialLotStatus.IqcRejected }, [], VerifyOutcome.Rejected, VerifyCode.LotNotIqcPassed, null },
        { "lot consumed", NewCap with { Status = MaterialLotStatus.Consumed, RemainingQty = 0 }, [], VerifyOutcome.Rejected, VerifyCode.LotEmpty, null },
        { "available but nothing left", NewCap with { RemainingQty = 0 }, [], VerifyOutcome.Rejected, VerifyCode.LotEmpty, null },
        { "expires exactly now (boundary)", NewCap with { ExpiryAt = Now }, [], VerifyOutcome.Rejected, VerifyCode.LotExpired, null },
        { "expires in one second", NewCap with { ExpiryAt = Now.AddSeconds(1) }, [], VerifyOutcome.Accepted, VerifyCode.Ok, null },
        { "no expiry date", NewCap with { ExpiryAt = null }, [], VerifyOutcome.Accepted, VerifyCode.Ok, null },
        // 1.3 A #3: an older reel with stock -> yellow, naming the reel to use.
        { "older lot available", NewCap, [OldCap], VerifyOutcome.Warning, VerifyCode.FifoOlderLotAvailable, OldCap.LotNo },
        { "older lot on hold: no warning", NewCap, [OldCap with { Status = MaterialLotStatus.Hold }], VerifyOutcome.Accepted, VerifyCode.Ok, null },
        { "[v2] older lot pending IQC: not suggested", NewCap, [OldCap with { Status = MaterialLotStatus.PendingIqc }], VerifyOutcome.Accepted, VerifyCode.Ok, null },
        { "older lot expired: no warning", NewCap, [OldCap with { ExpiryAt = Now.AddDays(-1) }], VerifyOutcome.Accepted, VerifyCode.Ok, null },
        { "older lot empty: no warning", NewCap, [OldCap with { RemainingQty = 0 }], VerifyOutcome.Accepted, VerifyCode.Ok, null },
        // Same delivery (same ReceivedAt) is not "older": '<', not '<='.
        { "same ReceivedAt: no warning", NewCap, [OldCap with { ReceivedAt = NewCap.ReceivedAt }], VerifyOutcome.Accepted, VerifyCode.Ok, null },
        { "older lot of another item: no warning", NewCap, [OldCap with { ItemId = Res }], VerifyOutcome.Accepted, VerifyCode.Ok, null },
        { "newer lot available: no warning", NewCap, [OldCap with { ReceivedAt = NewCap.ReceivedAt.AddDays(3) }], VerifyOutcome.Accepted, VerifyCode.Ok, null },
        // Quality before FIFO: an expired reel is red even when an older one exists (FIFO is not the problem).
        { "expired and older lot exists", NewCap with { ExpiryAt = Now }, [OldCap], VerifyOutcome.Rejected, VerifyCode.LotExpired, null },
        // What the reel is before its state: a wrong item on hold says "not in the BOM".
        { "wrong item and on hold", NewCap with { ItemId = Res10K, Status = MaterialLotStatus.Hold }, [], VerifyOutcome.Rejected, VerifyCode.ItemNotInBom, null },
        // [v2] state before date: a reel waiting for IQC that is also expired says IQC (no point rechecking dates).
        { "pending IQC and expired", NewCap with { Status = MaterialLotStatus.PendingIqc, ExpiryAt = Now }, [], VerifyOutcome.Rejected, VerifyCode.LotNotIqcPassed, null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Verify_table(string scenario, LotFacts? lot, LotFacts[] candidates, VerifyOutcome outcome, VerifyCode code, string? suggested)
    {
        var result = MaterialVerifier.Verify(lot, Bom, Smt, candidates, Now);

        Assert.True(new VerifyResult(outcome, code, suggested) == result, $"{scenario}: got {result}");
    }

    // Step 10b: the lot of a part soldered on at Repair. Quality as at loading; no station, no FIFO, empty passes.
    public static TheoryData<string, LotFacts?, VerifyCode> ReplacementCases() => new()
    {
        { "lot of the WO's BOM", NewCap, VerifyCode.Ok },
        { "item consumed at another step: Repair is off the routing", NewCap with { ItemId = Mcu }, VerifyCode.Ok },
        { "label of no lot", null, VerifyCode.LotNotFound },
        { "item not in the BOM (RES-10K for RES-100K)", NewCap with { ItemId = Res10K }, VerifyCode.ItemNotInBom },
        { "lot on hold", NewCap with { Status = MaterialLotStatus.Hold }, VerifyCode.LotOnHold },
        { "lot pending IQC", NewCap with { Status = MaterialLotStatus.PendingIqc }, VerifyCode.LotNotIqcPassed },
        { "lot rejected by IQC", NewCap with { Status = MaterialLotStatus.IqcRejected }, VerifyCode.LotNotIqcPassed },
        { "expires exactly now (boundary)", NewCap with { ExpiryAt = Now }, VerifyCode.LotExpired },
        { "expires in one second", NewCap with { ExpiryAt = Now.AddSeconds(1) }, VerifyCode.Ok },
        // RemainingQty is theoretical: the part is in the technician's hand (same reasoning as the scan gate).
        { "lot consumed on paper", NewCap with { Status = MaterialLotStatus.Consumed, RemainingQty = 0 }, VerifyCode.Ok },
        { "wrong item and on hold: what it is first", NewCap with { ItemId = Res10K, Status = MaterialLotStatus.Hold }, VerifyCode.ItemNotInBom },
    };

    [Theory]
    [MemberData(nameof(ReplacementCases))]
    public void Verify_replacement_table(string scenario, LotFacts? lot, VerifyCode code)
    {
        var result = MaterialVerifier.VerifyReplacement(lot, Bom, Now);

        Assert.True(result == (code == VerifyCode.Ok ? VerifyResult.Ok : VerifyResult.Reject(code)), $"{scenario}: got {result}");
    }

    // An older reel never matters for one part taken by hand.
    [Fact]
    public void Replacement_ignores_fifo()
        => Assert.Equal(VerifyResult.Ok, MaterialVerifier.VerifyReplacement(NewCap, Bom, Now));

    // The scanned reel itself is in the candidate list (the handler loads all reels of the item): never "older than itself".
    [Fact]
    public void Chosen_lot_among_the_candidates_is_ignored()
        => Assert.Equal(VerifyResult.Ok, MaterialVerifier.Verify(NewCap, Bom, Smt, [NewCap], Now));

    // An item used at two steps (glue at SMT and at ASSY): loading it on either station is fine.
    [Fact]
    public void Item_needed_at_two_steps_is_accepted_at_either()
    {
        WoMaterialRequirement[] bom = [new(Cap, 10, 4), new(Cap, 20, 1)];

        Assert.Equal(VerifyResult.Ok, MaterialVerifier.Verify(NewCap, bom, [20], [], Now));
        Assert.Equal(VerifyCode.ItemNotForStation, MaterialVerifier.Verify(NewCap, bom, [30], [], Now).Code);
    }

    // Several older reels: the OLDEST is suggested (and the same-day tie by lot number), so the advice is stable.
    [Fact]
    public void Oldest_of_several_older_lots_is_suggested()
    {
        var older = OldCap with { Id = 51, LotNo = "L-260905-000001", ReceivedAt = OldCap.ReceivedAt.AddDays(4) };
        var oldestB = OldCap with { Id = 52, LotNo = "L-260901-000003" };

        var result = FifoAdvisor.FindOlder(NewCap, [older, oldestB, OldCap], Now);

        Assert.Equal(OldCap.LotNo, result!.LotNo);   // 01/09 beats 05/09; "-000002" before "-000003" on the same day
    }
}
