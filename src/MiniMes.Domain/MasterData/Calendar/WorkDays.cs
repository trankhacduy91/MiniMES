namespace MiniMes.Domain.MasterData.Calendar;

// Days of the week a shift normally runs, stored as one int column. Named WorkDays to avoid
// clashing with System.DayOfWeek (which is not a [Flags] enum).
[Flags]
public enum WorkDays
{
    None = 0,
    Mon = 1,
    Tue = 2,
    Wed = 4,
    Thu = 8,
    Fri = 16,
    Sat = 32,
    Sun = 64,
    MonToFri = Mon | Tue | Wed | Thu | Fri,
    MonToSat = MonToFri | Sat,
}
