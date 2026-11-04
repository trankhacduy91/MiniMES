using MiniMes.Domain.Quality.Spc;

namespace MiniMes.UnitTests.Quality;

// [v2] Spec M7 2.3b: the rhythm of the machine sampler (30 s period, a gap after 3 periods without a sample).
public class SpcSamplingTests
{
    private static readonly DateTime Last = new(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    // seconds since the last sample, new Good value?, expected
    [InlineData(0, true, SampleDecision.NotDue)]
    [InlineData(29, true, SampleDecision.NotDue)]     // not due yet, even with fresh values (no autocorrelated points)
    [InlineData(30, true, SampleDecision.Take)]       // exactly one period
    [InlineData(31, true, SampleDecision.Take)]
    [InlineData(30, false, SampleDecision.Wait)]      // due but nothing new: wait, do not repeat the old value
    [InlineData(89, false, SampleDecision.Wait)]
    [InlineData(90, false, SampleDecision.Gap)]       // 3 periods without a sample
    [InlineData(600, false, SampleDecision.Gap)]
    [InlineData(600, true, SampleDecision.Take)]      // values are back: the next point is taken, the gap ends
    public void Decides_from_the_time_since_the_last_sample(int seconds, bool newValue, SampleDecision expected)
        => Assert.Equal(expected, SpcSampling.Decide(Last.AddSeconds(seconds), Last, everySec: 30, newValue));

    // A tag that never produced a value has no series yet: no gap to report.
    [Fact]
    public void A_tag_never_sampled_takes_its_first_value_or_waits()
    {
        Assert.Equal(SampleDecision.Take, SpcSampling.Decide(Last, null, 30, newGoodValue: true));
        Assert.Equal(SampleDecision.Wait, SpcSampling.Decide(Last.AddDays(1), null, 30, newGoodValue: false));
    }

    [Fact]
    public void The_gap_length_is_configurable()
    {
        Assert.Equal(SampleDecision.Wait, SpcSampling.Decide(Last.AddSeconds(100), Last, 30, false, gapPeriods: 4));
        Assert.Equal(SampleDecision.Gap, SpcSampling.Decide(Last.AddSeconds(120), Last, 30, false, gapPeriods: 4));
    }

    [Fact]
    public void Bad_settings_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpcSampling.Decide(Last, Last, 0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpcSampling.Decide(Last, Last, 30, true, gapPeriods: 0));
    }
}
