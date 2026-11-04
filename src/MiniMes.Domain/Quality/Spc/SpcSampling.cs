namespace MiniMes.Domain.Quality.Spc;

public enum SampleDecision
{
    NotDue,   // the period since the last sample has not passed yet
    Take,     // due, and a new Good value exists: it becomes the point
    Wait,     // due, no new Good value yet, not long enough to call it a gap
    Gap,      // [v2] 2.3b: GapPeriods periods without a sample: say so on the chart, never invent a point
}

// [v2] Spec M7 2.3b: when the machine sampler takes a point from a tag. Every value sent by deadband would give points
// that follow each other (autocorrelated, the run rule fires on noise); one value per period gives near-independent
// points, which is what a control chart assumes. Times are the machine's (SourceTs of the last sample).
public static class SpcSampling
{
    public const int DefaultGapPeriods = 3;

    public static SampleDecision Decide(DateTime now, DateTime? lastSampleTs, int everySec, bool newGoodValue,
        int gapPeriods = DefaultGapPeriods)
    {
        if (everySec <= 0) throw new ArgumentOutOfRangeException(nameof(everySec), "The sampling period must be positive.");
        if (gapPeriods < 1) throw new ArgumentOutOfRangeException(nameof(gapPeriods), "A gap is at least one period.");
        var period = TimeSpan.FromSeconds(everySec);
        if (lastSampleTs is { } last && now - last < period) return SampleDecision.NotDue;
        if (newGoodValue) return SampleDecision.Take;
        // Never sampled and nothing arrived: the tag has not started, there is no series to have a gap in.
        return lastSampleTs is { } since && now - since >= gapPeriods * period ? SampleDecision.Gap : SampleDecision.Wait;
    }
}
