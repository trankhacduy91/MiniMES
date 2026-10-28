using System.Globalization;
using MiniMes.Domain.MasterData.Calendar;
using MiniMes.Domain.Time;

namespace MiniMes.UnitTests.Time;

// Spec M5 2.2 / step 3: shift and production date of a UTC instant; plant time UTC+7; the demo shifts of the seeder.
public class ShiftCalendarResolveTests
{
    private static readonly TimeZoneInfo Plant = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private static TimeOnly T(int h, int m = 0) => new(h, m);

    private static readonly Shift[] Shifts =
    [
        new("S1", "Morning", T(6), T(14), WorkDays.MonToSat, [(T(10), T(10, 15)), (T(11, 30), T(12, 15))]),
        new("S2", "Afternoon", T(14), T(22), WorkDays.MonToSat, [(T(18), T(18, 45)), (T(20), T(20, 15))]),
        new("S3", "Night", T(22), T(6), WorkDays.MonToFri, [(T(1), T(1, 45)), (T(3, 30), T(3, 45))]),
    ];

    private static ShiftCalendar Calendar(params PlantCalendarDay[] days) => new(Plant, Shifts, days);

    private static DateTime Utc(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // Plant time -> UTC (Vietnam: +7, no daylight saving).
    private static DateTime Local(int year, int month, int day, int hour, int minute = 0)
        => new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-7);

    // The table of spec 1.2.8.
    [Theory]
    [InlineData("2026-10-01T15:00:00Z", "2026-10-01", "S3")]   // 22:00 plant time: night shift starts
    [InlineData("2026-10-01T18:30:00Z", "2026-10-01", "S3")]   // 01:30 on 02/10: still production date 01/10
    [InlineData("2026-10-01T22:59:59Z", "2026-10-01", "S3")]   // 05:59:59: last second
    [InlineData("2026-10-01T23:00:00Z", "2026-10-02", "S1")]   // 06:00: morning shift of 02/10
    public void Resolve_gives_the_production_date_of_the_shift_start(string utc, string date, string shift)
    {
        var w = Calendar().Resolve(Utc(utc));

        Assert.Equal(DateOnly.Parse(date, CultureInfo.InvariantCulture), w!.ShiftDate);
        Assert.Equal(shift, w.ShiftCode);
    }

    [Fact]
    public void Night_shift_window_and_breaks_are_in_utc_across_midnight()
    {
        var w = Calendar().Resolve(Utc("2026-10-01T18:30:00Z"))!;

        Assert.Equal(Utc("2026-10-01T15:00:00Z"), w.StartUtc);    // 22:00 on 01/10
        Assert.Equal(Utc("2026-10-01T23:00:00Z"), w.EndUtc);      // 06:00 on 02/10
        Assert.Equal(DateTimeKind.Utc, w.StartUtc.Kind);
        Assert.Equal(new Interval(Utc("2026-10-01T18:00:00Z"), Utc("2026-10-01T18:45:00Z")), w.Breaks[0]);   // 01:00-01:45
        Assert.Equal(TimeSpan.FromMinutes(60), IntervalMath.UnionLength(w.Breaks));
    }

    [Fact]
    public void Value_read_from_the_database_without_kind_is_taken_as_utc()
    {
        var unspecified = DateTime.SpecifyKind(Utc("2026-10-01T18:30:00Z"), DateTimeKind.Unspecified);

        Assert.Equal("S3", Calendar().Resolve(unspecified)!.ShiftCode);
    }

    [Fact]
    public void Local_time_is_refused()
        => Assert.Throws<ArgumentException>(() => Calendar().Resolve(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Local)));

    // [v2] 30/04 is a holiday. No shift has production date 30/04; the first hours of 30/04 still belong to the
    // night shift of 29/04 (a Wednesday), which was scheduled. The night shift of 30/04 does not run either.
    [Fact]
    public void Holiday_has_no_shift_window()
    {
        var calendar = Calendar(new PlantCalendarDay(new DateOnly(2026, 4, 30), isWorkingDay: false, null, "Reunification Day"));

        Assert.Empty(calendar.WindowsOn(new DateOnly(2026, 4, 30)));
        for (var hour = 6; hour < 30; hour++)                 // 30/04 06:00 -> 01/05 05:59 plant time
            Assert.Null(calendar.Resolve(Local(2026, 4, 30, 0).AddHours(hour).AddMinutes(30)));

        var early = calendar.Resolve(Local(2026, 4, 30, 1, 30))!;
        Assert.Equal((new DateOnly(2026, 4, 29), "S3"), (early.ShiftDate, early.ShiftCode));
    }

    // [v2] Sunday 01/11 overtime with only S1: 08:00 is S1, 15:00 (normally S2) is outside every shift.
    [Fact]
    public void Overtime_sunday_runs_only_the_listed_shift()
    {
        var calendar = Calendar(new PlantCalendarDay(new DateOnly(2026, 11, 1), isWorkingDay: true, "S1", "Overtime"));

        Assert.Equal("S1", calendar.Resolve(Local(2026, 11, 1, 8))!.ShiftCode);
        Assert.Null(calendar.Resolve(Local(2026, 11, 1, 15)));
        Assert.Null(Calendar().Resolve(Local(2026, 11, 1, 8)));   // without the overtime row Sunday is off
    }
}
