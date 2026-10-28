using MiniMes.Domain.Oee;

namespace MiniMes.UnitTests.Oee;

// Spec M5 2.1 test table + the hand calculations in docs/oee-hand-calcs.md (step 1).
public class OeeCalculatorTests
{
    private static OeeInput Input(double shift, double breaks, double plannedStop, double down,
        ProductionSegment[] segments, long good)
        => new(TimeSpan.FromMinutes(shift), TimeSpan.FromMinutes(breaks), TimeSpan.FromMinutes(plannedStop),
            TimeSpan.FromMinutes(down), segments, good);

    // L1-SMT-01, S1 01/10/2026: 480 − 60 − 15 = 405 planned, 45 unplanned, 1,000 boards at 20 s, 970 good.
    [Fact]
    public void Sample_shift_matches_the_hand_calculation()
    {
        var r = OeeCalculator.Calculate(Input(480, 60, 15, 45, [new(20, 1000)], 970));

        Assert.Equal(405, r.PlannedTime.TotalMinutes);
        Assert.Equal(360, r.RunTime.TotalMinutes);
        Assert.Equal(1000, r.TotalCount);
        Assert.Equal(970, r.GoodCount);
        Assert.Equal(20_000, r.IdealSeconds);
        Assert.Equal(0.8889, r.Availability!.Value, 4);   // 360 / 405
        Assert.Equal(0.9259, r.Performance!.Value, 4);    // 20,000 / 21,600
        Assert.Equal(0.97, r.Quality!.Value, 4);
        Assert.Equal(19_400.0 / 24_300.0, r.Oee!.Value, 6);   // shortcut: cycle × good / planned
        Assert.Equal(OeeWarning.None, r.Warnings);
    }

    [Fact]
    public void No_stop_at_the_ideal_rate_is_100_percent()
    {
        var r = OeeCalculator.Calculate(Input(480, 60, 15, 0, [new(20, 1215)], 1215));   // 405 × 60 / 20 = 1,215

        Assert.Equal(1.0, r.Availability!.Value, 9);
        Assert.Equal(1.0, r.Performance!.Value, 9);
        Assert.Equal(1.0, r.Quality!.Value, 9);
        Assert.Equal(1.0, r.Oee!.Value, 9);
        Assert.Equal(OeeWarning.None, r.Warnings);
    }

    // Hand calc (a): the master says 20 s but the machine really runs 15 s.
    [Fact]
    public void Performance_over_100_is_kept_raw_and_flagged()
    {
        var r = OeeCalculator.Calculate(Input(480, 60, 15, 45, [new(20, 1440)], 1420));

        Assert.Equal(1.3333, r.Performance!.Value, 4);    // not clipped to 1
        Assert.Equal(1.1687, r.Oee!.Value, 4);
        Assert.Equal(OeeWarning.PerformanceOver100, r.Warnings);
    }

    [Fact]
    public void Lunch_break_hour_has_no_ratios_and_does_not_throw()
    {
        var r = OeeCalculator.Calculate(Input(60, 60, 0, 0, [new(20, 0)], 0));

        Assert.Equal(TimeSpan.Zero, r.PlannedTime);
        Assert.Null(r.Availability);
        Assert.Null(r.Performance);
        Assert.Null(r.Quality);
        Assert.Null(r.Oee);
        Assert.Equal(OeeWarning.None, r.Warnings);
    }

    [Fact]
    public void Machine_counted_nothing_gives_P_0_Q_undefined_OEE_0()
    {
        var r = OeeCalculator.Calculate(Input(60, 0, 0, 10, [new(20, 0)], 0));

        Assert.Equal(50.0 / 60.0, r.Availability!.Value, 9);
        Assert.Equal(0.0, r.Performance);
        Assert.Null(r.Quality);
        Assert.Equal(0.0, r.Oee);
    }

    [Fact]
    public void Downtime_longer_than_planned_gives_run_0_and_a_flag()
    {
        var r = OeeCalculator.Calculate(Input(60, 0, 0, 90, [new(20, 10)], 10));

        Assert.Equal(TimeSpan.Zero, r.RunTime);
        Assert.Equal(0.0, r.Availability);
        Assert.Null(r.Performance);
        Assert.Equal(0.0, r.Oee);
        Assert.Equal(OeeWarning.DowntimeExceedsPlanned, r.Warnings);
    }

    // Hand calc (c): S2 with PCB-A 20 s × 600 and PCB-B 30 s × 200.
    [Fact]
    public void Two_work_orders_add_cycle_time_times_count_per_order()
    {
        var r = OeeCalculator.Calculate(Input(480, 60, 0, 30, [new(20, 600), new(30, 200)], 785));

        Assert.Equal(18_000, r.IdealSeconds);             // 12,000 + 6,000
        Assert.Equal(800, r.TotalCount);
        Assert.Equal(0.9286, r.Availability!.Value, 4);
        Assert.Equal(0.7692, r.Performance!.Value, 4);
        Assert.Equal(0.98125, r.Quality!.Value, 9);
        Assert.Equal(0.7009, r.Oee!.Value, 4);
    }

    // Hand calc (b): the caller passes the union of break and planned stop (70 min), not 60 + 25.
    [Fact]
    public void Meeting_inside_a_break_uses_the_union_from_the_caller()
    {
        var r = OeeCalculator.Calculate(Input(480, 70, 0, 30, [new(20, 1080)], 1065));

        Assert.Equal(410, r.PlannedTime.TotalMinutes);
        Assert.Equal(0.9268, r.Availability!.Value, 4);
        Assert.Equal(0.9474, r.Performance!.Value, 4);
        Assert.Equal(0.8659, r.Oee!.Value, 4);
    }

    [Fact]
    public void Good_above_total_is_kept_raw_and_flagged()
    {
        var r = OeeCalculator.Calculate(Input(480, 60, 15, 45, [new(20, 1000)], 1010));

        Assert.Equal(1.01, r.Quality!.Value, 9);
        Assert.Equal(OeeWarning.GoodExceedsTotal, r.Warnings);
    }

    // Invariant: Run cancels out, so A × P × Q = Ideal × (Good / Total) / Planned for any valid input. With one
    // cycle time this is the familiar cycle × good / planned. Catches a broken formula no example thought of.
    [Fact]
    public void A_times_P_times_Q_equals_ideal_times_quality_over_planned()
    {
        var random = new Random(20261028);
        for (var i = 0; i < 1000; i++)
        {
            var shift = random.Next(30, 721);
            var breaks = random.Next(0, shift / 4);
            var plannedStop = random.Next(0, shift / 4);
            var planned = shift - breaks - plannedStop;
            var down = random.Next(0, planned);
            var segments = Enumerable.Range(0, random.Next(1, 4))
                .Select(_ => new ProductionSegment(random.Next(5, 61), random.Next(1, 2000)))
                .ToArray();
            var total = segments.Sum(s => s.TotalCount);
            var good = (long)random.Next(0, (int)total + 1);

            var r = OeeCalculator.Calculate(Input(shift, breaks, plannedStop, down, segments, good));

            var expected = r.IdealSeconds * ((double)good / total) / TimeSpan.FromMinutes(planned).TotalSeconds;
            Assert.Equal(expected, r.Availability!.Value * r.Performance!.Value * r.Quality!.Value, 9);
            Assert.Equal(expected, r.Oee!.Value, 9);
        }
    }
}

// Rule 12 / spec 1.5 #6: OEE of several hours = add the totals and divide, never average the percentages.
public class OeeCombineTests
{
    private static OeeResult Hour(double plannedMinutes, double idealCycleSec, long count)
        => OeeCalculator.Calculate(new OeeInput(TimeSpan.FromMinutes(plannedMinutes), TimeSpan.Zero, TimeSpan.Zero,
            TimeSpan.Zero, [new ProductionSegment(idealCycleSec, count)], count));

    // Hour 1: 60 planned minutes at 90 %; hour 2: 6 planned minutes at 40 %. Average = 65 %, real = 85.45 %.
    [Fact]
    public void Two_hours_are_added_not_averaged()
    {
        var h1 = Hour(60, 20, 162);     // 3,240 s ideal / 3,600 s = 0.90
        var h2 = Hour(6, 1, 144);       //   144 s ideal /   360 s = 0.40

        var both = OeeCalculator.Combine([h1, h2]);

        Assert.Equal(0.9, h1.Oee!.Value, 9);
        Assert.Equal(0.4, h2.Oee!.Value, 9);
        Assert.Equal(3384.0 / 3960.0, both.Oee!.Value, 9);   // 0.8545, not (0.9 + 0.4) / 2
        Assert.Equal((66.0, 306L), (both.PlannedTime.TotalMinutes, both.TotalCount));
    }

    // A lunch hour (planned 0) adds nothing and does not turn the shift into "undefined".
    [Fact]
    public void Lunch_hour_does_not_change_the_shift()
    {
        var h1 = Hour(60, 20, 162);
        var lunch = OeeCalculator.Calculate(new OeeInput(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60), TimeSpan.Zero,
            TimeSpan.Zero, [new ProductionSegment(20, 0)], 0));

        Assert.Equal(h1.Oee, OeeCalculator.Combine([h1, lunch]).Oee);
        Assert.Null(OeeCalculator.Combine([lunch]).Oee);
    }

    // An hour above 100 % keeps its flag; the shift is judged on its own totals (6,660 s ideal / 7,200 s run = 0.925).
    [Fact]
    public void Flags_are_recomputed_on_the_totals()
    {
        var fast = Hour(60, 30, 162);   // P = 4,860 / 3,600 = 1.35

        Assert.Equal(OeeWarning.PerformanceOver100, fast.Warnings);
        Assert.Equal(OeeWarning.None, OeeCalculator.Combine([fast, Hour(60, 20, 90)]).Warnings);
        Assert.Equal(OeeWarning.PerformanceOver100, OeeCalculator.Combine([fast, Hour(60, 20, 180)]).Warnings);   // 8,460 / 7,200
    }
}
