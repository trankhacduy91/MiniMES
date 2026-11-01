using MiniMes.Domain.Machines;
using MiniMes.Domain.Time;

namespace MiniMes.Domain.Oee;

// One machine state segment that is not production, with what explains it (null = no reason yet).
public readonly record struct StopPiece(Interval Span, int? ReasonId, MachineState State);

// The time a stop really cost inside the window: outside breaks (and, for a loss, outside planned stops), after the
// micro-stop threshold. The Pareto adds these up, so its minutes are exactly the Availability loss (spec 2.5).
public readonly record struct StopLoss(int? ReasonId, MachineState State, bool CountsAgainstAvailability, TimeSpan Length);

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
        => BuildDetailed(window, breaks, plannedStops.Select(Piece).ToList(), downtime.Select(Piece).ToList(),
            microStopThreshold, segments, goodCount).Input;

    // Same totals, plus each stop's share (step 7, Pareto). One machine's segments never overlap each other, so the
    // losses add up to UnplannedDowntime exactly.
    public static (OeeInput Input, IReadOnlyList<StopLoss> Stops) BuildDetailed(Interval window, IEnumerable<Interval> breaks,
        IReadOnlyList<StopPiece> plannedStops, IReadOnlyList<StopPiece> downtime, TimeSpan microStopThreshold,
        IReadOnlyList<ProductionSegment> segments, long goodCount)
    {
        var breakList = Clip(breaks, window);
        // E = breaks ∪ planned stops: the machine may stand still then. Counted once where they overlap (rule 4).
        var notPlanned = breakList.Concat(Clip(plannedStops.Select(p => p.Span), window)).ToList();
        var breakTime = IntervalMath.UnionLength(breakList);
        var plannedStop = IntervalMath.UnionLength(notPlanned) - breakTime;   // only the part outside breaks

        // Stops shorter than the threshold are micro stops: they stay in Run Time and show up in Performance (rule 6).
        var counted = downtime.Where(d => d.Span.Length >= microStopThreshold)
            .Select(d => d with { Span = d.Span.Clip(window) })
            .Where(d => d.Span.Length > TimeSpan.Zero)
            .ToList();
        // A stop inside a break or a planned stop is not unplanned downtime (rule 5).
        var unplanned = IntervalMath.LengthOutside(counted.Select(d => d.Span), notPlanned);

        var stops = counted
            .Select(d => new StopLoss(d.ReasonId, d.State, true, IntervalMath.LengthOutside([d.Span], notPlanned)))
            .Concat(plannedStops
                .Select(p => p with { Span = p.Span.Clip(window) })
                .Select(p => new StopLoss(p.ReasonId, p.State, false, IntervalMath.LengthOutside([p.Span], breakList))))
            .Where(s => s.Length > TimeSpan.Zero)
            .ToList();
        return (new OeeInput(window.Length, breakTime, plannedStop, unplanned, segments, goodCount), stops);
    }

    private static StopPiece Piece(Interval span) => new(span, null, MachineState.Down);

    private static List<Interval> Clip(IEnumerable<Interval> items, Interval window)
        => items.Select(i => i.Clip(window)).Where(i => i.Length > TimeSpan.Zero).ToList();
}
