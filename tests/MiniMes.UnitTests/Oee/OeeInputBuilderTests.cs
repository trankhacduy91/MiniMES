using Microsoft.Extensions.Time.Testing;
using MiniMes.Domain.Machines;
using MiniMes.Domain.MasterData.Calendar;
using MiniMes.Domain.Oee;
using MiniMes.Domain.Time;

namespace MiniMes.UnitTests.Oee;

// Spec M5 step 3: from time ranges to OeeInput. The shifts are the hand calculations of docs/oee-hand-calcs.md.
public class OeeInputBuilderTests
{
    private static readonly TimeSpan MicroStop = TimeSpan.FromSeconds(60);

    // Plant time on Thursday 01/10/2026 -> UTC (+7).
    private static DateTime L(int hour, int minute = 0, int second = 0)
        => new DateTime(2026, 10, 1, hour, minute, second, DateTimeKind.Utc).AddHours(-7);

    private static Interval R(int h1, int m1, int h2, int m2) => new(L(h1, m1), L(h2, m2));

    private static readonly Interval S1 = R(6, 0, 14, 0);
    private static readonly Interval[] Breaks = [R(8, 0, 8, 15), R(10, 0, 10, 45)];

    // Six 20-second jams through the shift: below the threshold.
    private static IEnumerable<Interval> MicroStops()
        => Enumerable.Range(0, 6).Select(i => new Interval(L(7, 50 + i), L(7, 50 + i, 20)));

    [Fact]
    public void Sample_shift_from_time_ranges_gives_79_84_percent()
    {
        Interval[] down = [R(7, 10, 7, 35), R(9, 20, 9, 32), R(12, 40, 12, 48), .. MicroStops()];

        var input = OeeInputBuilder.Build(S1, Breaks, [R(6, 0, 6, 15)], down, MicroStop, [new(20, 1000)], 970);
        var r = OeeCalculator.Calculate(input);

        Assert.Equal((480, 60, 15, 45), (input.ShiftTime.TotalMinutes, input.BreakTime.TotalMinutes,
            input.PlannedStop.TotalMinutes, input.UnplannedDowntime.TotalMinutes));
        Assert.Equal(0.7984, r.Oee!.Value, 4);
    }

    // Hand calc (b): meeting 07:55-08:20 over the break 08:00-08:15; the machine is Down 10:10-10:30 during lunch.
    [Fact]
    public void Meeting_over_a_break_is_not_subtracted_twice_and_down_in_a_break_is_not_downtime()
    {
        var input = OeeInputBuilder.Build(S1, Breaks, [R(7, 55, 8, 20)], [R(7, 10, 7, 40), R(10, 10, 10, 30)],
            MicroStop, [new(20, 1080)], 1065);
        var r = OeeCalculator.Calculate(input);

        Assert.Equal(410, r.PlannedTime.TotalMinutes);      // not 395
        Assert.Equal(10, input.PlannedStop.TotalMinutes);    // only 07:55-08:00 and 08:15-08:20 are outside the break
        Assert.Equal(30, input.UnplannedDowntime.TotalMinutes);
        Assert.Equal(0.8659, r.Oee!.Value, 4);
    }

    // Spec 1.5 #1: Idle/Down through the whole lunch break must not lower Availability.
    [Fact]
    public void Machine_stopped_through_every_break_keeps_availability()
    {
        var without = OeeCalculator.Calculate(OeeInputBuilder.Build(S1, Breaks, [], [], MicroStop, [new(20, 1000)], 970));
        var with = OeeCalculator.Calculate(OeeInputBuilder.Build(S1, Breaks, [], Breaks, MicroStop, [new(20, 1000)], 970));

        Assert.Equal(1.0, with.Availability);
        Assert.Equal(without.Availability, with.Availability);
    }

    // Spec 1.2.5: threshold 0 counts every stop, the 2 minutes of micro stops come out of Run Time.
    [Fact]
    public void Threshold_zero_counts_micro_stops_as_downtime()
    {
        Interval[] down = [R(7, 10, 7, 35), R(9, 20, 9, 32), R(12, 40, 12, 48), .. MicroStops()];

        var input = OeeInputBuilder.Build(S1, Breaks, [R(6, 0, 6, 15)], down, TimeSpan.Zero, [new(20, 1000)], 970);

        Assert.Equal(47, input.UnplannedDowntime.TotalMinutes);
    }

    // A 30-minute stop 06:50-07:20 seen from the hour 07:00-08:00 is 20 minutes of downtime: the threshold is
    // judged on the whole stop, before cutting it to the hour.
    [Fact]
    public void Stop_crossing_the_hour_is_cut_to_the_window_after_the_threshold()
    {
        var hour = R(7, 0, 8, 0);

        var input = OeeInputBuilder.Build(hour, Breaks, [], [R(6, 50, 7, 20)], TimeSpan.FromMinutes(15), [new(20, 120)], 120);

        Assert.Equal(60, input.ShiftTime.TotalMinutes);
        Assert.Equal(0, input.BreakTime.TotalMinutes);         // the 08:00 break starts exactly at the end: [ , ) excludes it
        Assert.Equal(20, input.UnplannedDowntime.TotalMinutes);
    }

    // Spec 2.3: at 07:00 the running shift is judged on 60 minutes, not 480; the 08:00 break is still in the future.
    [Fact]
    public void Running_shift_is_cut_at_now()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(L(7, 0)));
        var shift = new ShiftCalendar(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"),
            [new Shift("S1", "Morning", new(6, 0), new(14, 0), WorkDays.MonToSat, [(new(8, 0), new(8, 15)), (new(10, 0), new(10, 45))])], []);

        var window = shift.Resolve(clock.GetUtcNow().UtcDateTime)!;
        var input = OeeInputBuilder.Build(window.Until(clock.GetUtcNow().UtcDateTime), window.Breaks, [R(6, 0, 6, 15)],
            [], MicroStop, [new(20, 135)], 135);
        var r = OeeCalculator.Calculate(input);

        Assert.Equal(45, r.PlannedTime.TotalMinutes);
        Assert.Equal(1.0, r.Availability);

        clock.Advance(TimeSpan.FromHours(2));                // 09:00: the 08:00-08:15 break is now in the past
        var later = OeeInputBuilder.Build(window.Until(clock.GetUtcNow().UtcDateTime), window.Breaks, [R(6, 0, 6, 15)],
            [], MicroStop, [], 0);
        Assert.Equal(TimeSpan.FromMinutes(15), later.BreakTime);
    }
}

// Step 7: each stop's share, for the Pareto. The losses must add up to exactly the Availability loss.
public class OeeStopLossTests
{
    private static DateTime L(int hour, int minute = 0, int second = 0)
        => new DateTime(2026, 10, 1, hour, minute, second, DateTimeKind.Utc).AddHours(-7);

    private static Interval R(int h1, int m1, int h2, int m2) => new(L(h1, m1), L(h2, m2));

    [Fact]
    public void Stop_losses_add_up_to_the_unplanned_downtime()
    {
        StopPiece[] down =
        [
            new(R(7, 10, 7, 35), 1, MachineState.Down),        // 25
            new(R(7, 50, 8, 10), null, MachineState.Down),     // 20, but 08:00-08:15 is a break -> 10
            new(new Interval(L(9, 0), L(9, 0, 20)), 2, MachineState.Down),   // micro stop: not a loss
        ];
        StopPiece[] planned = [new(R(6, 0, 6, 15), 3, MachineState.PlannedStop)];

        var (input, stops) = OeeInputBuilder.BuildDetailed(R(6, 0, 14, 0), [R(8, 0, 8, 15)], planned, down,
            TimeSpan.FromSeconds(60), [new(20, 100)], 100);

        Assert.Equal(TimeSpan.FromMinutes(35), input.UnplannedDowntime);
        Assert.Equal(input.UnplannedDowntime, stops.Where(s => s.CountsAgainstAvailability).Aggregate(TimeSpan.Zero, (t, s) => t + s.Length));
        Assert.Equal([(1, 25.0), ((int?)null, 10.0)], stops.Where(s => s.CountsAgainstAvailability).Select(s => (s.ReasonId, s.Length.TotalMinutes)));
        Assert.Equal((3, false, 15.0), stops.Where(s => !s.CountsAgainstAvailability).Select(s => (s.ReasonId, s.CountsAgainstAvailability, s.Length.TotalMinutes)).Single());
    }
}
