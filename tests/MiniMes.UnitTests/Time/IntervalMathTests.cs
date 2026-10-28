using MiniMes.Domain.Time;

namespace MiniMes.UnitTests.Time;

public class IntervalMathTests
{
    private static readonly DateTime Day = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Interval M(int fromMinute, int toMinute) => new(Day.AddMinutes(fromMinute), Day.AddMinutes(toMinute));

    [Theory]
    [InlineData(0)]                                   // nothing
    [InlineData(20, 0, 10, 15, 25)]                   // disjoint: 10 + 10
    [InlineData(25, 0, 20, 5, 25)]                    // overlapping: 0..25 once
    [InlineData(30, 0, 30, 10, 20)]                   // nested: the inner one adds nothing
    [InlineData(20, 0, 10, 10, 20)]                   // touching: 0..20
    [InlineData(10, 0, 10, 0, 10)]                    // the same range twice
    [InlineData(10, 5, 5, 0, 10)]                     // an empty range is ignored
    public void Union_counts_overlaps_once(int expectedMinutes, params int[] ranges)
    {
        var items = ranges.Chunk(2).Select(r => M(r[0], r[1]));

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), IntervalMath.UnionLength(items));
    }

    [Fact]
    public void Clip_cuts_to_the_window_and_outside_becomes_empty()
    {
        var window = M(60, 120);

        Assert.Equal(M(60, 80), M(50, 80).Clip(window));
        Assert.Equal(TimeSpan.Zero, M(0, 30).Clip(window).Length);
    }

    // Down 07:50-08:30 against a break 08:00-08:15: 25 minutes are real downtime.
    [Fact]
    public void Length_outside_removes_the_part_inside_breaks()
        => Assert.Equal(TimeSpan.FromMinutes(25), IntervalMath.LengthOutside([M(470, 510)], [M(480, 495)]));
}
