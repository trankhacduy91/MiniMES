using MiniMes.Domain.Common;
using MiniMes.Domain.Material;

namespace MiniMes.UnitTests.Material;

// Spec M6 step 1: what the entities refuse on their own (the database checks the rest).
public class MaterialEntityTests
{
    private static readonly DateTime T0 = new(2026, 9, 15, 0, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true, MaterialLotStatus.PendingIqc)]    // [v2] R15: received lots wait for IQC
    [InlineData(false, MaterialLotStatus.Available)]    // SkipIqc items (packing bags)
    public void New_lot_is_full_and_waits_for_iqc_unless_exempt(bool needsIqc, MaterialLotStatus status)
    {
        var lot = new MaterialLot("L-CAP-260915-01", 7, 10_000, " MUR-2609-A17 ", T0, T0.AddYears(2), needsIqc);

        Assert.Equal((10_000m, 10_000m, "MUR-2609-A17", status), (lot.Qty, lot.RemainingQty, lot.SupplierLot, lot.Status));
    }

    [Fact]
    public void Lot_refuses_bad_quantity_and_expiry()
    {
        Assert.Throws<DomainException>(() => new MaterialLot("L-1", 7, 0, "S1", T0, null, true));
        Assert.Throws<DomainException>(() => new MaterialLot("L-1", 7, 10, "S1", T0, T0, true));          // expires on receipt
        Assert.Throws<DomainException>(() => new MaterialLot(new string('L', 31), 7, 10, "S1", T0, null, true));
        Assert.Throws<DomainException>(() => new MaterialLot("L-1", 7, 10, " ", T0, null, true));         // [v2] IQC needs a supplier lot
        Assert.Equal(MaterialLotStatus.Available, new MaterialLot("L-1", 7, 10, null, T0, null, false).Status);   // exempt: none needed
    }

    [Fact]
    public void Fifo_override_needs_both_supervisor_and_reason()
    {
        Assert.Throws<DomainException>(() => new MaterialLoad(1, 7, 3, 9, null, "EMP00123", T0, "SUP01", null));
        Assert.Throws<DomainException>(() => new MaterialLoad(1, 7, 3, 9, null, "EMP00123", T0, null, "older lot is suspect"));

        var load = new MaterialLoad(1, 7, 3, 9, " 12 ", "EMP00123", T0, "SUP01", " older lot is suspect ");
        Assert.Equal(("12", "SUP01", "older lot is suspect"), (load.SlotNo, load.FifoOverrideBy, load.FifoOverrideReason));
    }

    [Fact]
    public void Load_is_unloaded_once()
    {
        var load = new MaterialLoad(1, 7, 3, 9, null, "EMP00123", T0);
        Assert.Throws<DomainException>(() => load.Unload("EMP00123", T0.AddMinutes(-1)));

        load.Unload("EMP00123", T0.AddHours(8));

        Assert.False(load.IsActive);
        Assert.Throws<DomainException>(() => load.Unload("EMP00123", T0.AddHours(9)));
    }

    [Fact]
    public void Genealogy_refuses_a_lot_made_from_itself()
        => Assert.Throws<DomainException>(() => new LotGenealogy(5, 5, 100, T0));

    [Theory]
    [InlineData(0, 0, IqcResult.Pass, null)]          // no samples
    [InlineData(5, 6, IqcResult.Pass, null)]          // more defects than samples
    [InlineData(5, 2, IqcResult.Fail, null)]          // a failed IQC needs a defect code
    public void Iqc_record_refuses_inconsistent_results(int samples, int defects, IqcResult result, string? defectCode)
        => Assert.Throws<DomainException>(() => new IqcRecord(7, "MUR-2609-A17", 5, samples, defects, result, defectCode, null, "QC01", T0));

    [Fact]
    public void Verification_log_keeps_an_unreadable_label_cut_to_the_column()
    {
        var log = new MaterialVerificationLog(1, new string('X', 50), null, null, null, VerifyOutcome.Rejected,
            VerifyCode.LotNotFound, null, "EMP00123", T0);

        Assert.Equal(MaterialLot.LotNoMaxLength, log.ScannedLotNo.Length);
    }

    // R10: at unload the real count replaces the theoretical one; nothing left = Consumed.
    [Theory]
    [InlineData(1840, MaterialLotStatus.Available)]
    [InlineData(0, MaterialLotStatus.Consumed)]
    public void Correcting_the_remaining_quantity(int actual, MaterialLotStatus status)
    {
        var lot = new MaterialLot("L-1", 7, 10_000, "S1", T0, null, needsIqc: false);

        lot.CorrectRemaining(actual);

        Assert.Equal((actual, status), ((int)lot.RemainingQty, lot.Status));
    }

    [Fact]
    public void Correction_cannot_go_below_zero_or_above_the_received_quantity()
    {
        var lot = new MaterialLot("L-1", 7, 100, "S1", T0, null, needsIqc: false);

        Assert.Throws<DomainException>(() => lot.CorrectRemaining(-1));
        Assert.Throws<DomainException>(() => lot.CorrectRemaining(101));
        Assert.Equal(100m, lot.RemainingQty);
    }
}
