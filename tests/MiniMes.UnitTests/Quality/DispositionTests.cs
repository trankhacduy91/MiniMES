using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Material;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;

namespace MiniMes.UnitTests.Quality;

// Spec M7 2.5 effects table, step 9: what an approved NCR does to its serials and lots. The use case releases the
// NCR's holds first; a target still held by another hold (Q8) gets the effect at that release (StatusBeforeHold).
public class DispositionTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);

    private static Serial Board(SerialStatus status = SerialStatus.InProcess)
    {
        var s = new Serial("PCBA-1", 1, 1);
        if (status is SerialStatus.InProcess or SerialStatus.Completed) s.ApplyScan(status == SerialStatus.Completed ? 40 : 10, status == SerialStatus.Completed, ScanResult.Ok);
        if (status == SerialStatus.Ng) { s.ApplyScan(10, false, ScanResult.Ok); s.ApplyScan(20, false, ScanResult.Ng); }
        return s;
    }

    [Theory]
    [InlineData(NcrDisposition.UseAsIs, SerialStatus.InProcess)]
    [InlineData(NcrDisposition.Rework, SerialStatus.Repair)]
    [InlineData(NcrDisposition.Scrap, SerialStatus.Scrapped)]
    public void A_free_board_gets_the_effect_at_once(NcrDisposition d, SerialStatus expected)
    {
        var s = Board();
        s.ApplyDisposition(d, "NCR-000007");
        Assert.Equal(expected, s.Status);
        if (d == NcrDisposition.Scrap) Assert.Equal("NCR-000007: scrap", s.ScrapReason);
    }

    [Fact]
    public void Scrap_by_decision_also_ends_a_completed_board()
    {
        var s = Board(SerialStatus.Completed);
        s.ApplyDisposition(NcrDisposition.Scrap, "NCR-000007");
        Assert.Equal(SerialStatus.Scrapped, s.Status);   // Scrap() of M3 refuses Completed; an MRB decision may
    }

    [Fact]
    public void A_board_scrapped_or_sent_to_vendor_is_refused()
    {
        var scrapped = Board();
        scrapped.Scrap("burnt");
        Assert.Throws<DomainException>(() => scrapped.ApplyDisposition(NcrDisposition.UseAsIs, "NCR-1"));   // Q10
        Assert.Throws<DomainException>(() => Board().ApplyDisposition(NcrDisposition.ReturnToVendor, "NCR-1"));   // Q13
    }

    [Theory]
    [InlineData(MaterialLotStatus.Available, NcrDisposition.UseAsIs, MaterialLotStatus.Available)]
    [InlineData(MaterialLotStatus.Available, NcrDisposition.Scrap, MaterialLotStatus.Scrapped)]
    [InlineData(MaterialLotStatus.Available, NcrDisposition.ReturnToVendor, MaterialLotStatus.Returned)]
    public void Lot_effects(MaterialLotStatus from, NcrDisposition d, MaterialLotStatus expected)
    {
        // IqcRejected -> Available is set by IQC in SQL (M6): covered by HoldApiTests.Return_to_vendor_returns_...
        var lot = new MaterialLot("L-1", 1, 500m, null, T0, null, false);
        Assert.Equal(from, lot.Status);
        lot.ApplyDisposition(d);
        Assert.Equal(expected, lot.Status);
        if (expected is MaterialLotStatus.Scrapped or MaterialLotStatus.Returned) Assert.Equal(0m, lot.RemainingQty);
    }

    [Fact]
    public void A_lot_whose_life_is_over_is_refused()
    {
        var lot = new MaterialLot("L-1", 1, 500m, null, T0, null, false);
        lot.ApplyDisposition(NcrDisposition.ReturnToVendor);
        Assert.Throws<DomainException>(() => lot.ApplyDisposition(NcrDisposition.UseAsIs));
    }

    // Q8: still held by another NCR: the effect waits in StatusBeforeHold, the gates still see Hold.
    [Fact]
    public void A_target_still_held_keeps_hold_and_the_effect_waits()
    {
        var lot = new MaterialLot("L-1", 1, 500m, null, T0, null, false);
        lot.PutOnHold();
        lot.ApplyDisposition(NcrDisposition.Scrap);
        Assert.Equal((MaterialLotStatus.Hold, MaterialLotStatus.Scrapped), (lot.Status, lot.StatusBeforeHold));
    }
}
