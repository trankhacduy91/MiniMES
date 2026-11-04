using MiniMes.Domain.Common;
using MiniMes.Domain.Machines;
using MiniMes.Domain.Material;
using MiniMes.Domain.Quality;
using MiniMes.Domain.Quality.Fai;

namespace MiniMes.UnitTests.Quality;

// Spec M7 step 2: what the quality entities refuse or decide on their own (the database checks the rest,
// QualitySchemaTests). The statistics and the rule engine come in steps 3 and 4.
public class QualityEntityTests
{
    private static readonly DateTime T0 = new(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc);

    private static InspectionPlan Rail() => InspectionPlan.ForStation("3V3_RAIL", "3.3 V rail", itemId: 1, "TEST", "V",
        lsl: 3.135m, usl: 3.465m, target: 3.300m, defectCodeId: 9);

    // Q1/Q2: decimal comparison; exactly on a limit is still OK. 3.440 is OK against USL even though it is
    // beyond the control limit 3.422: the two pairs of limits are independent (spec 1.2).
    [Theory]
    [InlineData("3.300", MeasureResult.Ok)]
    [InlineData("3.440", MeasureResult.Ok)]
    [InlineData("3.465", MeasureResult.Ok)]
    [InlineData("3.135", MeasureResult.Ok)]
    [InlineData("3.470", MeasureResult.Ng)]
    [InlineData("3.134999", MeasureResult.Ng)]
    public void Plan_judges_a_value_against_the_specification(string value, MeasureResult expected)
        => Assert.Equal(expected, Rail().Judge(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    // Flatness has only an upper limit: nothing is too low.
    [Fact]
    public void One_sided_plan_judges_one_side_only()
    {
        var flatness = InspectionPlan.ForMachine("FLATNESS", "Board flatness", "mm", lsl: null, usl: 0.75m, target: null);
        Assert.Equal((MeasureResult.Ok, MeasureResult.Ng), (flatness.Judge(-5m), flatness.Judge(0.76m)));
    }

    [Fact]
    public void Plan_refuses_impossible_limits()
    {
        Assert.Throws<DomainException>(() => InspectionPlan.ForMachine("X", "X", "V", null, null, null));       // no limit at all
        Assert.Throws<DomainException>(() => InspectionPlan.ForMachine("X", "X", "V", 3.4m, 3.4m, null));      // LSL = USL
        Assert.Throws<DomainException>(() => InspectionPlan.ForMachine("X", "X", "V", 3.1m, 3.4m, 3.5m));      // target outside
        Assert.Throws<DomainException>(() => InspectionPlan.ForMachine("x y", "X", "V", 3.1m, 3.4m, null));    // code rules (M1)
    }

    // Step 1 decision 2: the measurement copies the limits it was judged against; a later spec change leaves it.
    [Fact]
    public void Measurement_keeps_the_limits_of_its_time()
    {
        var plan = Rail();
        var before = Measurement.OfSerial(plan, MeasurementSource.Station, 42, 7, 3.470m, "EMP00123", T0);
        plan.ChangeSpec(3.135m, 3.500m, 3.300m);
        var after = Measurement.OfSerial(plan, MeasurementSource.Station, 42, 7, 3.470m, "EMP00123", T0.AddMinutes(1));

        Assert.Equal((MeasureResult.Ng, 3.465m), (before.Result, before.Usl));
        Assert.Equal((MeasureResult.Ok, 3.500m), (after.Result, after.Usl));
    }

    [Fact]
    public void Machine_sample_has_no_serial_and_sits_at_the_machine_time()
    {
        var oven = InspectionPlan.ForMachine("REFLOW_PEAK_TEMP", "Peak", "°C", 235m, 255m, 245m);
        var m = Measurement.FromMachine(oven, machineTagId: 3, 256.2m, T0);

        Assert.Equal((MeasurementSource.Machine, (long?)null, T0, T0, MeasureResult.Ng),
            (m.Source, m.SerialId, m.MeasuredAt, m.SourceTs, m.Result));
        Assert.Throws<DomainException>(() => Measurement.OfSerial(oven, MeasurementSource.Machine, 42, 7, 245m, "EMP00123", T0));
    }

    [Fact]
    public void Inactive_plan_takes_no_measurement()
    {
        var plan = Rail();
        plan.SetActive(false);
        Assert.Throws<DomainException>(() => Measurement.OfSerial(plan, MeasurementSource.Station, 42, 7, 3.3m, "EMP00123", T0));
    }

    // Spec 1.2: baseline 3.302 / 0.040 gives UCL 3.422 and LCL 3.182; UCL_MR = 3.267 × MR̄ (0.0544 -> 0.1777).
    [Fact]
    public void Baseline_stores_the_limits_it_implies()
    {
        var b = new ControlLimitSet(1, 3.302m, 0.040m, 0.0544m, 25, T0.AddHours(-8), T0, "Phase I", "qc01", T0);
        Assert.Equal((3.422m, 3.182m, 0.1777248m), (b.Ucl, b.Lcl, b.UclMr));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    public void Baseline_refuses_a_sigma_that_is_not_positive(string sigma)
        => Assert.Throws<DomainException>(() =>
            new ControlLimitSet(1, 3.3m, decimal.Parse(sigma, System.Globalization.CultureInfo.InvariantCulture), null, 0, null, null, "Test", "qc01", T0));

    [Fact]
    public void Only_a_data_gap_alert_has_no_point()
    {
        Assert.Throws<DomainException>(() => new SpcAlert(1, SpcRuleCode.WE1, measurementId: null, T0));
        var gap = new SpcAlert(1, SpcRuleCode.DataGap, measurementId: null, T0);

        gap.Close("qc01", "Oven was in PM", T0.AddMinutes(10));
        Assert.Equal(SpcAlertStatus.Closed, gap.Status);
        Assert.Throws<DomainException>(() => gap.Close("qc01", "again", T0.AddMinutes(11)));
    }

    [Fact]
    public void Ncr_needs_targets_and_numbers_from_the_sequence()
    {
        Assert.Throws<DomainException>(() => new Ncr("NCR-000001", NcrSource.Manual, null, null, "Bridge on U3", 1, []));
        Assert.Throws<DomainException>(() => new Ncr("NCR-000001", NcrSource.Manual, null, null, "Bridge on U3", 1,
            [(HoldTargetType.Serial, 5), (HoldTargetType.Serial, 5)]));
        var ncr = new Ncr(Ncr.FormatNo(12), NcrSource.SpcAlert, "17", null, "Run of 7 on 3V3_RAIL", 87,
            [(HoldTargetType.Lot, 3), (HoldTargetType.Serial, 5)]);

        Assert.Equal(("NCR-000012", NcrStatus.Open, 2), (ncr.NcrNo, ncr.Status, ncr.Items.Count));
        Assert.Throws<DomainException>(() => Ncr.FormatNo(1_000_000));
    }

    [Fact]
    public void Hold_is_released_once_with_a_note()
    {
        var hold = new HoldRecord(HoldTargetType.Lot, 3, Guid.NewGuid(), HoldScope.Lot, "L-260101-0001", "Suspect reel", null, "qc01", T0);
        Assert.Throws<DomainException>(() => hold.Release("sup01", " ", T0.AddHours(1)));
        Assert.True(hold.IsActive);                                   // a refused release changes nothing

        hold.Release("sup01", "NCR-000012 use as is", T0.AddHours(1));
        Assert.False(hold.IsActive);
        Assert.Throws<DomainException>(() => hold.Release("sup01", "again", T0.AddHours(2)));
    }

    // Q17: a person ticking Pass cannot overrule a red automatic check.
    [Fact]
    public void Fai_cannot_pass_with_a_red_automatic_check()
    {
        Assert.Throws<DomainException>(() => new FaiRecord(1, 42, 1, "[]", autoChecksPassed: false, FaiResult.Pass, null, "qc01", T0));
        var fail = new FaiRecord(1, 42, 1, "[]", autoChecksPassed: false, FaiResult.Fail, "D3 reversed", "qc01", T0, " fai-fail ");
        Assert.Equal((FaiResult.Fail, "FAI-FAIL"), (fail.Result, fail.DefectCode));
    }

    // [step 8b] a Fail says why the sample went to Repair; a Pass has no defect.
    [Fact]
    public void Fai_fail_needs_a_defect_code_and_pass_has_none()
    {
        Assert.Throws<DomainException>(() => new FaiRecord(1, 42, 1, "[]", autoChecksPassed: true, FaiResult.Fail, null, "qc01", T0));
        Assert.Throws<DomainException>(() => new FaiRecord(1, 42, 1, "[]", autoChecksPassed: true, FaiResult.Pass, null, "qc01", T0, "FAI-FAIL"));
    }

    [Fact]
    public void Fai_template_knows_only_the_automatic_checks_it_can_compute()
    {
        Assert.Throws<DomainException>(() => new FaiTemplate(1, 1, [new("XRAY", "X-ray of BGA", FaiCheckKind.Auto)]));
        Assert.Throws<DomainException>(() => new FaiTemplate(1, 1, []));
        var t = new FaiTemplate(1, 1,
        [
            new(nameof(FaiAutoCheck.BomMatch), "BOM matches", FaiCheckKind.Auto),
            new("VISUAL", "Looks right", FaiCheckKind.Manual),
        ]);

        Assert.Contains("\"kind\":\"Auto\"", t.ItemsJson);           // readable in SSMS
        Assert.Equal(t.Items(), [new FaiCheckItem("BomMatch", "BOM matches", FaiCheckKind.Auto), new FaiCheckItem("VISUAL", "Looks right", FaiCheckKind.Manual)]);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(3_601)]
    public void Machine_tag_refuses_a_sampling_period_out_of_range(int seconds)
        => Assert.Throws<DomainException>(() => new MachineTag(1, "peakTemp", "°C", 0, 400).LinkSpc(1, seconds));

    // Spec 2.6 point 2: the M6 trace-screen hold remembers what to go back to.
    [Fact]
    public void Lot_hold_remembers_the_status_before()
    {
        var lot = new MaterialLot("L-1", 7, 10, null, T0, null, needsIqc: false);
        lot.PutOnHold();
        Assert.Equal((MaterialLotStatus.Hold, MaterialLotStatus.Available), (lot.Status, lot.StatusBeforeHold));
    }
}
