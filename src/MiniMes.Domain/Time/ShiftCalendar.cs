using MiniMes.Domain.MasterData.Calendar;

namespace MiniMes.Domain.Time;

// One occurrence of a shift: S3 of production date 01/10 runs 01/10 22:00 -> 02/10 06:00 plant time, stored as UTC.
// ShiftDate is the plant-time date the shift STARTS (spec M5 1.2.8), so a night shift is never split in two.
public sealed record ShiftWindow(DateOnly ShiftDate, string ShiftCode, DateTime StartUtc, DateTime EndUtc,
    IReadOnlyList<Interval> Breaks)
{
    public Interval Span => new(StartUtc, EndUtc);

    // The part of the shift that has happened: a running shift is judged up to "now", or at 07:00 the planned time
    // would be the whole 405 minutes and A = 11 % (spec M5 2.3).
    public Interval Until(DateTime nowUtc) => new(StartUtc, nowUtc < EndUtc ? nowUtc : EndUtc);
}

// Spec M5 2.2: which shift (and production date) a UTC instant belongs to. Shifts are defined in plant time
// because people think "06:00", not "23:00 UTC". Whether a shift runs on a date is decided by M1's
// ProductionCalendar.IsShiftScheduled, so holidays and overtime Sundays follow one rule in M1 and M5.
public sealed class ShiftCalendar
{
    private readonly TimeZoneInfo _zone;
    private readonly IReadOnlyList<Shift> _shifts;
    private readonly Dictionary<DateOnly, PlantCalendarDay> _calendar;

    public ShiftCalendar(TimeZoneInfo zone, IEnumerable<Shift> shifts, IEnumerable<PlantCalendarDay> calendar)
    {
        _zone = zone;
        _shifts = shifts.ToList();
        _calendar = calendar.ToDictionary(d => d.Date);
    }

    public bool IsScheduled(Shift shift, DateOnly date)
        => ProductionCalendar.IsShiftScheduled(shift, date, _calendar.GetValueOrDefault(date));

    // null = outside every shift (between shifts, a holiday): that time is not planned and has no OEE.
    public ShiftWindow? Resolve(DateTime utc)
    {
        utc = AsUtc(utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, _zone);
        var today = DateOnly.FromDateTime(local);
        // A night shift that started yesterday may still be running: try both start dates.
        foreach (var date in new[] { today, today.AddDays(-1) })
            foreach (var window in WindowsOn(date))
                if (utc >= window.StartUtc && utc < window.EndUtc)
                    return window;
        return null;
    }

    // The shifts that run on a production date (none on a holiday), in start order.
    public IReadOnlyList<ShiftWindow> WindowsOn(DateOnly date)
        => _shifts.Where(s => IsScheduled(s, date))
            .OrderBy(s => s.StartTime)
            .Select(s => WindowOf(s, date))
            .ToList();

    private ShiftWindow WindowOf(Shift shift, DateOnly date)
    {
        var startLocal = date.ToDateTime(shift.StartTime);   // Kind = Unspecified: plant time
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, _zone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal.AddMinutes(shift.DurationMinutes), _zone);
        // Breaks as offsets from the shift start (TimeOnly subtraction wraps midnight): a 01:00 break of a 22:00
        // shift is minute 180, no special case after midnight.
        var breaks = shift.Breaks
            .Select(b => new Interval(startUtc + (b.StartTime - shift.StartTime), startUtc + (b.EndTime - shift.StartTime)))
            .OrderBy(b => b.StartUtc)
            .ToList();
        return new ShiftWindow(date, shift.Code, startUtc, endUtc, breaks);
    }

    // EF Core reads datetime2 as Kind = Unspecified; in this system every stored DateTime is UTC. A Local value
    // means someone called DateTime.Now: refuse it rather than silently shift by 7 hours.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => throw new ArgumentException("Expected a UTC time, got a local one.", nameof(value)),
    };
}
