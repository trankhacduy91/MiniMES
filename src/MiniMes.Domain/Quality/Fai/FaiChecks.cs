using System.Text.Json;
using MiniMes.Domain.Common;

namespace MiniMes.Domain.Quality.Fai;

// One line of a FAI worksheet. Auto: Passed computed from MES data, Detail says why it is red. Manual: Passed =
// ticked by QC (null on a blank worksheet).
public sealed record FaiCheckOutcome(string Code, string Text, FaiCheckKind Kind, bool? Passed, string? Detail);

public sealed record FaiAutoResult(bool Passed, string? Detail)
{
    public static readonly FaiAutoResult Green = new(true, null);
}

// [v2] Spec M7 2.5b: the three automatic checks and the judgement rules (Q17), pure so they are unit tested without
// a database. The worksheet query gathers the inputs (trace, scans, measurements) and calls these.
public static class FaiChecks
{
    // BomMatch: the lot-controlled items the WO consumes at the steps the sample has passed, against the items traced
    // on the sample (M6 SerialComponent). Steps not reached yet (packing bag at PACK) are not expected.
    public static FaiAutoResult BomMatch(IReadOnlyCollection<string> requiredItems, IReadOnlyCollection<string> tracedItems)
    {
        var missing = requiredItems.Except(tracedItems, StringComparer.OrdinalIgnoreCase).Order().ToList();
        var extra = tracedItems.Except(requiredItems, StringComparer.OrdinalIgnoreCase).Order().ToList();
        if (missing.Count == 0 && extra.Count == 0) return FaiAutoResult.Green;
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add("not traced on the sample: " + string.Join(", ", missing));
        if (extra.Count > 0) parts.Add("not in the work order BOM: " + string.Join(", ", extra));
        return new FaiAutoResult(false, string.Join("; ", parts) + ".");
    }

    // TestPassed: an OK scan at every inspection step of the routing (a board failed and repaired has its later OK).
    public static FaiAutoResult TestPassed(IReadOnlyCollection<string> inspectionSteps, IReadOnlyCollection<string> okSteps)
    {
        if (inspectionSteps.Count == 0) return new FaiAutoResult(false, "The routing has no inspection step.");
        var missing = inspectionSteps.Except(okSteps, StringComparer.OrdinalIgnoreCase).ToList();
        return missing.Count == 0 ? FaiAutoResult.Green : new FaiAutoResult(false, "No OK scan at " + string.Join(", ", missing) + ".");
    }

    // MeasurementsInSpec: every measurement of the sample is OK (a corrected value counts by its correction, Q16),
    // and every characteristic planned at a passed step was measured: "in spec" cannot be claimed without a value.
    public static FaiAutoResult MeasurementsInSpec(IReadOnlyCollection<string> plannedCharacteristics,
        IReadOnlyCollection<(string Plan, decimal Value, bool Ok)> measurements)
    {
        var ng = measurements.Where(m => !m.Ok).Select(m => $"{m.Plan} = {m.Value}").ToList();
        var notMeasured = plannedCharacteristics.Except(measurements.Select(m => m.Plan), StringComparer.OrdinalIgnoreCase).ToList();
        if (ng.Count == 0 && notMeasured.Count == 0) return FaiAutoResult.Green;
        var parts = new List<string>();
        if (ng.Count > 0) parts.Add("out of spec: " + string.Join(", ", ng));
        if (notMeasured.Count > 0) parts.Add("not measured: " + string.Join(", ", notMeasured));
        return new FaiAutoResult(false, string.Join("; ", parts) + ".");
    }

    // The worksheet: the template's items in order, Auto ones with their result, Manual ones ticked or not
    // (ticked = null leaves them blank, the worksheet before QC starts).
    public static FaiSheet Fill(IReadOnlyList<FaiCheckItem> items, IReadOnlyDictionary<FaiAutoCheck, FaiAutoResult> auto,
        IReadOnlyCollection<string>? ticked)
    {
        var lines = items.Select(i =>
        {
            if (i.Kind == FaiCheckKind.Manual)
                return new FaiCheckOutcome(i.Code, i.Text, i.Kind, ticked is null ? null : ticked.Contains(i.Code, StringComparer.OrdinalIgnoreCase), null);
            var r = auto[Enum.Parse<FaiAutoCheck>(i.Code)];
            return new FaiCheckOutcome(i.Code, i.Text, i.Kind, r.Passed, r.Detail);
        }).ToList();
        return new FaiSheet(lines);
    }
}

public sealed record FaiSheet(IReadOnlyList<FaiCheckOutcome> Items)
{
    // FaiRecord.ChecklistJson: what QC saw and ticked, kept as it was (insert-only), readable in SSMS.
    public string ToJson() => JsonSerializer.Serialize(Items, FaiTemplate.Json);

    public static FaiSheet FromJson(string json) => new(JsonSerializer.Deserialize<List<FaiCheckOutcome>>(json, FaiTemplate.Json) ?? []);

    public bool AutoChecksPassed => Items.Where(i => i.Kind == FaiCheckKind.Auto).All(i => i.Passed == true);

    public bool ManualComplete => Items.Where(i => i.Kind == FaiCheckKind.Manual).All(i => i.Passed == true);

    // Q17: Pass needs every automatic check green and every manual item ticked. Fail is always possible: QC may
    // see a problem the checklist does not list.
    public void RequireCanRecord(FaiResult result)
    {
        if (result != FaiResult.Pass) return;
        if (!AutoChecksPassed)
            throw new DomainException("FAI cannot pass while an automatic check is red: "
                + string.Join(" ", Items.Where(i => i.Kind == FaiCheckKind.Auto && i.Passed != true).Select(i => $"{i.Code}: {i.Detail}")));
        if (!ManualComplete)
            throw new DomainException("FAI cannot pass before every manual item is checked: "
                + string.Join(", ", Items.Where(i => i.Kind == FaiCheckKind.Manual && i.Passed != true).Select(i => i.Code)) + ".");
    }
}
