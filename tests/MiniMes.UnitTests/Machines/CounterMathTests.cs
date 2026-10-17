using MiniMes.Domain.Machines;

namespace MiniMes.UnitTests.Machines;

// Spec M4 2.3: the table of the cumulative counter.
public class CounterMathTests
{
    [Theory]
    [InlineData(null, 15230L, 0L, false, true)]    // first value seen: baseline only
    [InlineData(100L, 105L, 5L, false, false)]
    [InlineData(100L, 100L, 0L, false, false)]     // no change
    [InlineData(15230L, 15290L, 60L, false, false)] // after an Offline gap the total is still right
    [InlineData(15290L, 3L, 3L, true, false)]      // reset to 0, then 3 made
    [InlineData(65535L, 2L, 2L, true, false)]      // 16-bit PLC register wrapped: treated as a reset
    public void Delta_from_the_last_counter(long? last, long current, long delta, bool reset, bool baseline)
        => Assert.Equal(new CounterDelta(delta, reset, baseline), CounterMath.Compute(last, current));

    // Lost messages cost nothing: summing the deltas of the messages that did arrive gives the true production.
    [Fact]
    public void Missing_messages_do_not_lose_production()
    {
        long? last = null;
        long total = 0;
        foreach (var counter in new long[] { 1000, 1012, /* 1024, 1036 lost */ 1048, 1060 })
        {
            total += CounterMath.Compute(last, counter).Delta;
            last = counter;
        }

        Assert.Equal(60, total);
    }
}
