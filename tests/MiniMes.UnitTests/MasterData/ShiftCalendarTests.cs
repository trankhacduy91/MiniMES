using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Calendar;

namespace MiniMes.UnitTests.MasterData;

// Spec M1 step 7: break rules, planned production minutes, and IsShiftScheduled.
public class ShiftCalendarTests
{
    private static TimeOnly T(int h, int m = 0) => new(h, m);

    private static Shift S1(params (TimeOnly, TimeOnly)[] breaks)
        => new("S1", "Morning", T(6), T(14), WorkDays.MonToSat,
            breaks.Length > 0 ? breaks : [(T(10), T(10, 15)), (T(11, 30), T(12, 15))]);

    private static Shift S2() => new("S2", "Afternoon", T(14), T(22), WorkDays.MonToSat,
        [(T(18), T(18, 45)), (T(20), T(20, 15))]);

    private static Shift S3(params (TimeOnly, TimeOnly)[] breaks)
        => new("S3", "Night", T(22), T(6), WorkDays.MonToFri,
            breaks.Length > 0 ? breaks : [(T(1), T(1, 45)), (T(3, 30), T(3, 45))]);

    [Fact]
    public void S1_has_420_planned_minutes()
    {
        var s1 = S1();

        Assert.Equal((480, 420, false), (s1.DurationMinutes, s1.PlannedProductionMinutes, s1.CrossesMidnight));
    }

    // 22:00-06:00: a naive End - Start would be negative; breaks at 01:00 belong to the part after midnight.
    [Fact]
    public void S3_night_shift_has_420_planned_minutes()
    {
        var s3 = S3();

        Assert.Equal((480, 420, true), (s3.DurationMinutes, s3.PlannedProductionMinutes, s3.CrossesMidnight));
    }

    [Fact]
    public void Break_across_midnight_inside_a_night_shift_is_fine()
        => Assert.Equal(460, S3((T(23, 50), T(0, 10))).PlannedProductionMinutes);

    [Theory]
    [InlineData(15, 0, 15, 15)]   // after the shift
    [InlineData(5, 30, 5, 45)]    // before the shift
    [InlineData(13, 50, 14, 10)]  // runs past the end
    public void Break_outside_the_day_shift_is_rejected(int h1, int m1, int h2, int m2)
        => Assert.Throws<DomainException>(() => S1((T(h1, m1), T(h2, m2))));

    [Theory]
    [InlineData(12, 0, 12, 30)]   // 12:00 is "today" for a 22:00 shift, so it is outside
    [InlineData(5, 50, 6, 10)]    // runs past 06:00
    public void Break_outside_the_night_shift_is_rejected(int h1, int m1, int h2, int m2)
        => Assert.Throws<DomainException>(() => S3((T(h1, m1), T(h2, m2))));

    [Fact]
    public void Overlapping_breaks_are_rejected()
        => Assert.Throws<DomainException>(() => S1((T(10), T(10, 30)), (T(10, 15), T(10, 45))));

    [Fact]
    public void Breaks_as_long_as_the_shift_are_rejected()
        => Assert.Throws<DomainException>(() => new Shift("SX", "x", T(6), T(7), WorkDays.Mon, [(T(6), T(7))]));

    [Fact]
    public void Reschedule_replaces_the_breaks_as_one_set()
    {
        var s1 = S1();

        s1.Reschedule(T(6), T(14), WorkDays.MonToFri, [(T(11, 30), T(12, 30))]);

        Assert.Equal(420, s1.PlannedProductionMinutes);
        Assert.Single(s1.Breaks);
    }

    [Fact]
    public void Failed_reschedule_keeps_the_old_breaks()
    {
        var s1 = S1();

        Assert.Throws<DomainException>(() => s1.Reschedule(T(6), T(14), WorkDays.MonToSat, [(T(15), T(15, 30))]));
        Assert.Equal(2, s1.Breaks.Count);
    }

    // ---------- IsShiftScheduled (spec M1 1.2.9) ----------

    private static readonly DateOnly Monday = new(2026, 10, 12);
    private static readonly DateOnly Sunday = new(2026, 10, 18);
    private static readonly DateOnly ReunificationDay = new(2026, 4, 30); // a Thursday

    [Fact]
    public void Normal_monday_follows_the_weekly_pattern()
        => Assert.True(ProductionCalendar.IsShiftScheduled(S1(), Monday, null));

    [Fact]
    public void Holiday_row_stops_every_shift()
    {
        var holiday = new PlantCalendarDay(ReunificationDay, isWorkingDay: false, null, "Reunification Day");

        Assert.False(ProductionCalendar.IsShiftScheduled(S1(), ReunificationDay, holiday));
        Assert.False(ProductionCalendar.IsShiftScheduled(S2(), ReunificationDay, holiday));
    }

    [Fact]
    public void Sunday_overtime_runs_only_the_listed_shift()
    {
        var overtime = new PlantCalendarDay(Sunday, isWorkingDay: true, "S1", "Overtime for order X");

        Assert.True(ProductionCalendar.IsShiftScheduled(S1(), Sunday, overtime));
        Assert.False(ProductionCalendar.IsShiftScheduled(S2(), Sunday, overtime));
    }

    [Fact]
    public void Sunday_without_a_row_is_off()
        => Assert.False(ProductionCalendar.IsShiftScheduled(S1(), Sunday, null));

    [Fact]
    public void Working_day_row_without_shift_codes_runs_every_shift()
    {
        var overtime = new PlantCalendarDay(Sunday, isWorkingDay: true, null, null);

        Assert.True(ProductionCalendar.IsShiftScheduled(S1(), Sunday, overtime));
        Assert.True(ProductionCalendar.IsShiftScheduled(S3(), Sunday, overtime));
    }

    [Fact]
    public void Inactive_shift_never_runs()
    {
        var s1 = S1();
        s1.SetActive(false);

        Assert.False(ProductionCalendar.IsShiftScheduled(s1, Monday, null));
    }

    [Fact]
    public void Shift_codes_are_normalised_and_a_day_off_cannot_list_shifts()
    {
        Assert.Equal("S1,S2", new PlantCalendarDay(Sunday, true, " S1, S2 ,S1", null).ShiftCodes);
        Assert.Throws<DomainException>(() => new PlantCalendarDay(Sunday, false, "S1", null));
    }

    [Fact]
    public void Entry_for_another_date_is_a_programming_error()
        => Assert.Throws<ArgumentException>(() =>
            ProductionCalendar.IsShiftScheduled(S1(), Monday, new PlantCalendarDay(Sunday, true, null, null)));
}
