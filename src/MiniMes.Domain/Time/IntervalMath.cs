namespace MiniMes.Domain.Time;

// A half-open UTC time range [StartUtc, EndUtc): the boundary second belongs to the next range only, so two
// adjacent hours never count the same event (spec M5 2.2).
public readonly record struct Interval(DateTime StartUtc, DateTime EndUtc)
{
    public TimeSpan Length => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;

    // Cut to the window; a range outside it ends up with Length 0.
    public Interval Clip(Interval window) => new(
        StartUtc > window.StartUtc ? StartUtc : window.StartUtc,
        EndUtc < window.EndUtc ? EndUtc : window.EndUtc);
}

public static class IntervalMath
{
    // Total time covered, overlaps counted once: a meeting 07:55-08:20 over a break 08:00-08:15 is 25 minutes,
    // not 40 (rule 4).
    public static TimeSpan UnionLength(IEnumerable<Interval> items)
    {
        var total = TimeSpan.Zero;
        DateTime? start = null, end = null;
        foreach (var i in items.Where(x => x.Length > TimeSpan.Zero).OrderBy(x => x.StartUtc))
        {
            if (end is null || i.StartUtc > end)          // a gap: close the current run, start a new one
            {
                if (end is not null) total += end.Value - start!.Value;
                start = i.StartUtc;
                end = i.EndUtc;
            }
            else if (i.EndUtc > end) end = i.EndUtc;      // overlapping or touching: extend
        }
        return end is null ? total : total + (end.Value - start!.Value);
    }

    // |D \ E| = |D ∪ E| − |E|: the part of D outside E. Used for "downtime that is not inside a break".
    public static TimeSpan LengthOutside(IEnumerable<Interval> d, IReadOnlyCollection<Interval> e)
        => UnionLength(d.Concat(e)) - UnionLength(e);
}
