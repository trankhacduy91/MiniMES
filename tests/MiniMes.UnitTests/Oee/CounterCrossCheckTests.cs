using MiniMes.Domain.Oee;

namespace MiniMes.UnitTests.Oee;

// Spec M5 1.2.10 / rule 10: machine counter vs scan rows, flagged above 2 % of the machine count.
public class CounterCrossCheckTests
{
    [Theory]
    [InlineData(1000, 1000, false)]   // the sample shift
    [InlineData(1000, 993, false)]    // 0.7 %: a few boards still at the station when the shift ends (spec 1.3 #10)
    [InlineData(1000, 985, false)]    // 1.5 % (spec 1.2.10)
    [InlineData(1000, 980, false)]    // exactly 2 %: not above
    [InlineData(1000, 940, true)]     // 6 %: scans skipped, or the machine counts dry cycles (spec 1.2.10)
    [InlineData(1000, 1021, true)]    // more scans than counts is just as wrong
    [InlineData(0, 0, false)]         // nothing made, nothing scanned: they agree
    [InlineData(0, 5, true)]          // scans while the counter says nothing: the counter is not heard
    [InlineData(50, 0, true)]         // counted, never scanned
    public void Flags_a_gap_above_two_percent(long machine, long scans, bool mismatch)
        => Assert.Equal(mismatch, CounterCrossCheck.IsMismatch(machine, scans));

    [Fact]
    public void Gap_is_relative_to_the_machine_count()
    {
        Assert.Equal(0.06, CounterCrossCheck.Gap(1000, 940)!.Value, 9);
        Assert.Null(CounterCrossCheck.Gap(0, 5));
    }

    // Spec 1.2.10: 1,000 counted, 940 scanned of which 910 OK -> Q = 91 % although only 3 % were NG. The numbers stay
    // as they are; only the flag says they cannot be trusted.
    [Fact]
    public void Apply_flags_without_changing_the_numbers()
    {
        var r = OeeCalculator.Calculate(new OeeInput(TimeSpan.FromMinutes(480), TimeSpan.FromMinutes(60),
            TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(45), [new(20, 1000)], 910));

        var flagged = CounterCrossCheck.Apply(r, 940);

        Assert.Equal(OeeWarning.CounterMismatch, flagged.Warnings);
        Assert.Equal(r with { Warnings = OeeWarning.CounterMismatch }, flagged);
        Assert.Equal(0.91, flagged.Quality!.Value, 9);
    }

    // A line or a shift is judged on its own sums: one hour off by 3 boards does not flag the whole shift, and a
    // flag carried by a part is cleared when the totals agree.
    [Fact]
    public void Apply_decides_again_from_the_totals_it_is_given()
    {
        var hour = CounterCrossCheck.Apply(OeeCalculator.Calculate(new OeeInput(TimeSpan.FromHours(1), TimeSpan.Zero,
            TimeSpan.Zero, TimeSpan.Zero, [new(20, 100)], 97)), 97);
        Assert.True(hour.Warnings.HasFlag(OeeWarning.CounterMismatch));

        var withPerformance = hour with { Warnings = hour.Warnings | OeeWarning.PerformanceOver100 };
        Assert.Equal(OeeWarning.PerformanceOver100, CounterCrossCheck.Apply(withPerformance, 100).Warnings);
    }
}
