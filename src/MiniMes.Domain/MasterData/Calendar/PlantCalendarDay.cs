using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Calendar;

// [v2] Exceptions to the normal shift days: holidays (IsWorkingDay = false) and overtime days
// (IsWorkingDay = true, optionally only some shifts). Only exceptions are stored, one row per date.
public class PlantCalendarDay : AuditableEntity, IAudited
{
    public const int NoteMaxLength = 200;
    public const int ShiftCodesMaxLength = 50;

    public DateOnly Date { get; private set; }        // production date (a night shift belongs to the day it starts)
    public bool IsWorkingDay { get; private set; }
    public string? ShiftCodes { get; private set; }   // comma-separated, e.g. "S1" or "S1,S2"; null = every shift
    public string? Note { get; private set; }

    private PlantCalendarDay() { }

    public PlantCalendarDay(DateOnly date, bool isWorkingDay, string? shiftCodes, string? note)
    {
        Date = date;
        Change(isWorkingDay, shiftCodes, note);
    }

    public IReadOnlyList<string> ShiftCodeList => ParseCodes(ShiftCodes);

    public void Change(bool isWorkingDay, string? shiftCodes, string? note)
    {
        if (note is { Length: > NoteMaxLength })
            throw new DomainException($"Note must be at most {NoteMaxLength} characters.");
        var codes = ParseCodes(shiftCodes);
        if (!isWorkingDay && codes.Count > 0)
            throw new DomainException("A day off cannot list shifts; leave ShiftCodes empty.");
        foreach (var code in codes) MasterDataRules.RequireCode(code, "Shift");

        IsWorkingDay = isWorkingDay;
        ShiftCodes = codes.Count == 0 ? null : string.Join(",", codes); // normalised: "S1, S2 " -> "S1,S2"
        if (ShiftCodes is { Length: > ShiftCodesMaxLength })
            throw new DomainException($"ShiftCodes must be at most {ShiftCodesMaxLength} characters.");
        Note = string.IsNullOrWhiteSpace(note) ? null : note;
    }

    private static IReadOnlyList<string> ParseCodes(string? codes)
        => string.IsNullOrWhiteSpace(codes)
            ? []
            : codes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
}

// [v2] "Does shift X run on production date D?" (spec M1 1.2.9). Pure, so M5 can reuse it for the
// planned time of Availability without a database.
public static class ProductionCalendar
{
    /// <param name="calendarEntry">The PlantCalendar row for that date, or null when the date has none.</param>
    public static bool IsShiftScheduled(Shift shift, DateOnly productionDate, PlantCalendarDay? calendarEntry)
    {
        if (calendarEntry is not null && calendarEntry.Date != productionDate)
            throw new ArgumentException("The calendar entry is for another date.", nameof(calendarEntry));
        if (!shift.IsActive) return false;

        // A calendar row overrides the weekly pattern for that date.
        if (calendarEntry is not null)
            return calendarEntry.IsWorkingDay
                   && (calendarEntry.ShiftCodeList.Count == 0 || calendarEntry.ShiftCodeList.Contains(shift.Code));

        return shift.Days.HasFlag(ToWorkDay(productionDate.DayOfWeek));
    }

    public static WorkDays ToWorkDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => WorkDays.Mon,
        DayOfWeek.Tuesday => WorkDays.Tue,
        DayOfWeek.Wednesday => WorkDays.Wed,
        DayOfWeek.Thursday => WorkDays.Thu,
        DayOfWeek.Friday => WorkDays.Fri,
        DayOfWeek.Saturday => WorkDays.Sat,
        _ => WorkDays.Sun,
    };
}
