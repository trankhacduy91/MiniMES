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

        double a = run / planned;
        double? p = run > TimeSpan.Zero ? ideal / run.TotalSeconds : null;
        double? q = total > 0 ? (double)x.GoodCount / total : null;
        // Raw values are kept: clipping P to 100 % would hide a wrong cycle time (rule 9).
        if (p > 1.0) warnings |= OeeWarning.PerformanceOver100;
        if (x.GoodCount > total) warnings |= OeeWarning.GoodExceedsTotal;
        double oee = p is { } pp && q is { } qq ? a * pp * qq : 0.0;   // planned time but nothing made: 0 %
        return new(planned, run, total, x.GoodCount, ideal, a, p, q, oee, warnings);
    }
}
