using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Calendar;

// [v2] Exceptions to the normal shift days: holidays (IsWorkingDay = false) and overtime days
// (IsWorkingDay = true, optionally only some shifts). Only exceptions are stored, one row per date.
public class PlantCalendarDay : AuditableEntity, IAudited
{
    public const int NoteMaxLength = 200;

    public DateOnly Date { get; private set; }        // production date (a night shift belongs to the day it starts)
    public bool IsWorkingDay { get; private set; }
    public string? ShiftCodes { get; private set; }   // e.g. "S1"; null = every shift that normally runs that weekday
    public string? Note { get; private set; }

    private PlantCalendarDay() { }

    public PlantCalendarDay(DateOnly date, bool isWorkingDay, string? shiftCodes, string? note)
    {
        if (note is { Length: > NoteMaxLength })
            throw new DomainException($"Note must be at most {NoteMaxLength} characters.");
        Date = date;
        IsWorkingDay = isWorkingDay;
        ShiftCodes = string.IsNullOrWhiteSpace(shiftCodes) ? null : shiftCodes;
        Note = note;
    }
}
