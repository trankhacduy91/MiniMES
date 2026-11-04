using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality.Fai;

namespace MiniMes.UnitTests.Quality;

// [v2] Spec M7 2.5b / Q17: the three automatic checks of the FAI worksheet and when QC may record Pass.
public class FaiChecksTests
{
    // ---------- BomMatch ----------

    [Fact]
    public void Bom_match_is_green_when_the_traced_items_are_exactly_the_required_ones()
    {
        var r = FaiChecks.BomMatch(["RES-10K", "CAP-100N"], ["cap-100n", "RES-10K"]);
        Assert.Equal(FaiAutoResult.Green, r);
    }

    [Fact]
    public void Bom_match_lists_what_is_missing_and_what_does_not_belong()
    {
        // The setup error FAI exists for: a wrong reel loaded (RES-1K instead of RES-10K).
        var r = FaiChecks.BomMatch(["RES-10K", "CAP-100N"], ["CAP-100N", "RES-1K"]);
        Assert.False(r.Passed);
        Assert.Equal("not traced on the sample: RES-10K; not in the work order BOM: RES-1K.", r.Detail);
    }

    [Fact]
    public void Bom_match_with_nothing_traced_is_red()
        => Assert.Equal("not traced on the sample: CAP-100N.", FaiChecks.BomMatch(["CAP-100N"], []).Detail);

    // ---------- TestPassed ----------

    [Theory]
    [InlineData(new[] { "TEST" }, new[] { "TEST" }, true, null)]
    [InlineData(new[] { "TEST" }, new string[0], false, "No OK scan at TEST.")]
    [InlineData(new[] { "AOI", "TEST" }, new[] { "AOI" }, false, "No OK scan at TEST.")]
    [InlineData(new string[0], new string[0], false, "The routing has no inspection step.")]
    public void Test_passed_needs_an_ok_scan_at_every_inspection_step(string[] steps, string[] ok, bool passed, string? detail)
        => Assert.Equal(new FaiAutoResult(passed, detail), FaiChecks.TestPassed(steps, ok));

    // ---------- MeasurementsInSpec ----------

    [Fact]
    public void Measurements_in_spec_is_green_when_every_planned_value_is_ok()
        => Assert.True(FaiChecks.MeasurementsInSpec(["3V3_RAIL"], [("3V3_RAIL", 3.301m, true)]).Passed);

    [Fact]
    public void An_out_of_spec_value_is_red_with_the_value()
    {
        var r = FaiChecks.MeasurementsInSpec(["3V3_RAIL"], [("3V3_RAIL", 3.480m, false)]);
        Assert.Equal(new FaiAutoResult(false, "out of spec: 3V3_RAIL = 3.480."), r);
    }

    // "In spec" cannot be claimed without a value: a planned characteristic never measured is red.
    [Fact]
    public void A_planned_characteristic_not_measured_is_red()
        => Assert.Equal("not measured: 3V3_RAIL.", FaiChecks.MeasurementsInSpec(["3V3_RAIL"], []).Detail);

    [Fact]
    public void No_plan_and_no_value_is_green()
        => Assert.True(FaiChecks.MeasurementsInSpec([], []).Passed);

    // ---------- the worksheet and Q17 ----------

    private static readonly FaiCheckItem[] Template =
    [
        new(nameof(FaiAutoCheck.BomMatch), "BOM", FaiCheckKind.Auto),
        new(nameof(FaiAutoCheck.TestPassed), "Test", FaiCheckKind.Auto),
        new(nameof(FaiAutoCheck.MeasurementsInSpec), "Values", FaiCheckKind.Auto),
        new("VISUAL", "Visual", FaiCheckKind.Manual),
        new("LABEL", "Label", FaiCheckKind.Manual),
    ];

    private static Dictionary<FaiAutoCheck, FaiAutoResult> Auto(FaiAutoResult? bom = null) => new()
    {
        [FaiAutoCheck.BomMatch] = bom ?? FaiAutoResult.Green,
        [FaiAutoCheck.TestPassed] = FaiAutoResult.Green,
        [FaiAutoCheck.MeasurementsInSpec] = FaiAutoResult.Green,
    };

    [Fact]
    public void The_sheet_keeps_the_template_order_and_leaves_manual_items_blank_until_qc_ticks()
    {
        var sheet = FaiChecks.Fill(Template, Auto(), ticked: null);

        Assert.Equal(["BomMatch", "TestPassed", "MeasurementsInSpec", "VISUAL", "LABEL"], sheet.Items.Select(i => i.Code));
        Assert.All(sheet.Items.Where(i => i.Kind == FaiCheckKind.Manual), i => Assert.Null(i.Passed));
        Assert.True(sheet.AutoChecksPassed);
        Assert.False(sheet.ManualComplete);
    }

    [Fact]
    public void Pass_needs_green_automatic_checks_and_every_manual_item()
    {
        var all = FaiChecks.Fill(Template, Auto(), ["visual", "LABEL"]);
        var oneMissing = FaiChecks.Fill(Template, Auto(), ["VISUAL"]);
        var red = FaiChecks.Fill(Template, Auto(new FaiAutoResult(false, "not in the work order BOM: RES-1K.")), ["VISUAL", "LABEL"]);

        all.RequireCanRecord(FaiResult.Pass);
        Assert.Contains("LABEL", Assert.Throws<DomainException>(() => oneMissing.RequireCanRecord(FaiResult.Pass)).Message);
        Assert.Contains("RES-1K", Assert.Throws<DomainException>(() => red.RequireCanRecord(FaiResult.Pass)).Message);
        Assert.False(red.AutoChecksPassed);
    }

    // QC may fail a sample for something the checklist does not list, even with everything green.
    [Fact]
    public void Fail_is_always_allowed()
    {
        FaiChecks.Fill(Template, Auto(new FaiAutoResult(false, "x")), []).RequireCanRecord(FaiResult.Fail);
        FaiChecks.Fill(Template, Auto(), ["VISUAL", "LABEL"]).RequireCanRecord(FaiResult.Fail);
    }

    [Fact]
    public void The_checklist_json_round_trips_with_kinds_as_text()
    {
        var sheet = FaiChecks.Fill(Template, Auto(new FaiAutoResult(false, "missing RES")), ["VISUAL"]);

        var json = sheet.ToJson();

        Assert.Contains("\"kind\":\"Manual\"", json);
        Assert.Equal(sheet.Items, FaiSheet.FromJson(json).Items);
    }

    // ---------- the sample after a Fail ----------

    [Fact]
    public void A_rejected_sample_goes_the_ng_way_and_keeps_its_step()
    {
        var s = new Serial("PCBA-1", 1, 1);
        s.ApplyScan(10, false, ScanResult.Ok);
        s.ApplyScan(30, false, ScanResult.Ok);

        s.RejectFirstArticle();

        Assert.Equal((SerialStatus.Ng, 30), (s.Status, s.CurrentSeq));
        s.CheckInRepair();   // the NG flow of M3 takes over
    }

    [Fact]
    public void A_completed_or_not_started_sample_cannot_be_rejected()
    {
        var packed = new Serial("PCBA-2", 1, 1);
        packed.ApplyScan(40, true, ScanResult.Ok);

        Assert.Contains("packing", Assert.Throws<DomainException>(packed.RejectFirstArticle).Message);
        Assert.Throws<DomainException>(new Serial("PCBA-3", 1, 1).RejectFirstArticle);
    }
}
