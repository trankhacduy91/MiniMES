using MiniMes.Domain.Time;

namespace MiniMes.Domain.Oee;

// Spec M5 step 3: turns time ranges into the totals OeeCalculator takes. Pure; the reader (step 4) decides which
// machine state goes to which list (spec 1.2.4): PlannedStop or a stop whose reason does not count against
// Availability -> plannedStops; Down, Setup, Idle with a WO, Offline -> downtime.
public static class OeeInputBuilder
{
    /// <param name="window">The hour or shift being judged (a running shift: cut at "now").</param>
    /// <param name="downtime">Whole stop segments, not yet cut to the window: the micro-stop threshold applies to
    /// the full length, so a 30-minute stop crossing the hour is not a 20-second micro stop in the next hour.</param>
    public static OeeInput Build(Interval window, IEnumerable<Interval> breaks, IEnumerable<Interval> plannedStops,
        IEnumerable<Interval> downtime, TimeSpan microStopThreshold, IReadOnlyList<ProductionSegment> segments, long goodCount)
    {
        var breakList = Clip(breaks, window);
        // E = breaks ∪ planned stops: the machine may stand still then. Counted once where they overlap (rule 4).
        var notPlanned = breakList.Concat(Clip(plannedStops, window)).ToList();
        var breakTime = IntervalMath.UnionLength(breakList);
        var plannedStop = IntervalMath.UnionLength(notPlanned) - breakTime;   // only the part outside breaks

        // Stops shorter than the threshold are micro stops: they stay in Run Time and show up in Performance (rule 6).
        var counted = Clip(downtime.Where(d => d.Length >= microStopThreshold), window);
        // A stop inside a break or a planned stop is not unplanned downtime (rule 5).
        var unplanned = IntervalMath.LengthOutside(counted, notPlanned);

        return new OeeInput(window.Length, breakTime, plannedStop, unplanned, segments, goodCount);
    }

    private static List<Interval> Clip(IEnumerable<Interval> items, Interval window)
        => items.Select(i => i.Clip(window)).Where(i => i.Length > TimeSpan.Zero).ToList();
}
