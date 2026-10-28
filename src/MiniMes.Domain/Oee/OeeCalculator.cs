namespace MiniMes.Domain.Oee;

// One work order's share of the window: its ideal cycle time (WorkOrderOperation snapshot) and what the machine
// counted while it ran. A shift with two products has two segments (spec M5 rule 3).
public readonly record struct ProductionSegment(double IdealCycleSec, long TotalCount);

// Totals for one window (an hour or a shift), already cut to the window by the caller. BreakTime and PlannedStop
// must not overlap each other (the builder takes their union first, rule 4).
public sealed record OeeInput(TimeSpan ShiftTime, TimeSpan BreakTime, TimeSpan PlannedStop,
    TimeSpan UnplannedDowntime, IReadOnlyList<ProductionSegment> Segments, long GoodCount);

[Flags]
public enum OeeWarning
{
    None = 0,
    PerformanceOver100 = 1,       // ideal cycle time too slow, wrong unit, or a bad counter (spec 1.2.7)
    GoodExceedsTotal = 2,         // more OK scans than machine counts
    DowntimeExceedsPlanned = 4,
}

// Ratios are null when undefined (denominator 0), which is not the same as 0 % (rule 14).
public sealed record OeeResult(TimeSpan PlannedTime, TimeSpan RunTime, long TotalCount, long GoodCount,
    double IdealSeconds, double? Availability, double? Performance, double? Quality, double? Oee, OeeWarning Warnings);

// Spec M5 2.1: the only place in the system where A/P/Q/OEE are computed. Pure: no clock, no DB, no logging.
public static class OeeCalculator
{
    public static OeeResult Calculate(OeeInput x)
    {
        var planned = x.ShiftTime - x.BreakTime - x.PlannedStop;
        long total = x.Segments.Sum(s => s.TotalCount);
        double ideal = x.Segments.Sum(s => s.IdealCycleSec * s.TotalCount);
        if (planned <= TimeSpan.Zero)   // a lunch-break hour: nothing was planned, so nothing to judge
            return new(TimeSpan.Zero, TimeSpan.Zero, total, x.GoodCount, ideal, null, null, null, null, OeeWarning.None);

        var warnings = OeeWarning.None;
        var run = planned - x.UnplannedDowntime;
        if (run < TimeSpan.Zero) { run = TimeSpan.Zero; warnings |= OeeWarning.DowntimeExceedsPlanned; }
        return FromTotals(planned, run, total, x.GoodCount, ideal, warnings);
    }

    // Rule 12: several hours or machines together = add the times and counts, then divide with the same formula.
    // Averaging the hourly OEEs is wrong as soon as the hours have different planned times (spec 1.5 #6).
    public static OeeResult Combine(IEnumerable<OeeResult> parts)
    {
        var list = parts.ToList();
        var planned = list.Aggregate(TimeSpan.Zero, (t, r) => t + r.PlannedTime);
        long total = list.Sum(r => r.TotalCount), good = list.Sum(r => r.GoodCount);
        double ideal = list.Sum(r => r.IdealSeconds);
        if (planned <= TimeSpan.Zero)
            return new(TimeSpan.Zero, TimeSpan.Zero, total, good, ideal, null, null, null, null, OeeWarning.None);
        var run = list.Aggregate(TimeSpan.Zero, (t, r) => t + r.RunTime);
        var carried = list.Aggregate(OeeWarning.None, (w, r) => w | r.Warnings) & OeeWarning.DowntimeExceedsPlanned;
        return FromTotals(planned, run, total, good, ideal, carried);
    }

    private static OeeResult FromTotals(TimeSpan planned, TimeSpan run, long total, long good, double ideal, OeeWarning warnings)
    {
        double a = run / planned;
        double? p = run > TimeSpan.Zero ? ideal / run.TotalSeconds : null;
        double? q = total > 0 ? (double)good / total : null;
        // Raw values are kept: clipping P to 100 % would hide a wrong cycle time (rule 9).
        if (p > 1.0) warnings |= OeeWarning.PerformanceOver100;
        if (good > total) warnings |= OeeWarning.GoodExceedsTotal;
        double oee = p is { } pp && q is { } qq ? a * pp * qq : 0.0;   // planned time but nothing made: 0 %
        return new(planned, run, total, good, ideal, a, p, q, oee, warnings);
    }
}
