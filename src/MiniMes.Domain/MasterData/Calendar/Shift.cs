using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Calendar;

// A working time window in factory local time ("the morning shift starts at 06:00"), not an absolute
// instant: transactions are stored in UTC and M5 converts between the two.
public class Shift : AuditableEntity, IAudited
{
    public const int CodeMaxLength = 10;

    public int Id { get; private set; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public TimeOnly StartTime { get; private set; }
    public TimeOnly EndTime { get; private set; }
    public WorkDays Days { get; private set; }
    public bool CrossesMidnight { get; private set; }   // 22:00-06:00; derived from the times, stored for queries
    public bool IsActive { get; private set; } = true;

    public List<ShiftBreak> Breaks { get; private set; } = [];

    private Shift() { Code = Name = string.Empty; }

    public Shift(string code, string name, TimeOnly startTime, TimeOnly endTime, WorkDays days,
        IEnumerable<(TimeOnly Start, TimeOnly End)>? breaks = null)
    {
        MasterDataRules.RequireCode(code, "Shift");
        if (code.Length > CodeMaxLength) throw new DomainException($"Shift code must be at most {CodeMaxLength} characters.");
        Code = code;
        Name = string.Empty;
        Rename(name);
        Reschedule(startTime, endTime, days, breaks ?? []);
    }

    // Length in minutes; a night shift (22:00-06:00) wraps past midnight: 06:00 - 22:00 + 24h = 480.
    public int DurationMinutes => Offset(EndTime);

    // Planned production time before other planned stops: the denominator of Availability (M5).
    public int PlannedProductionMinutes => DurationMinutes - Breaks.Sum(b => BreakMinutes(b));

    public void Rename(string name)
    {
        MasterDataRules.RequireName(name);
        Name = name;
    }

    // Times, days and breaks change together and are validated as one set (spec M1 1.4 rule 11):
    // every break inside the shift (night shifts too), no two breaks overlapping, total breaks < shift length.
    public void Reschedule(TimeOnly startTime, TimeOnly endTime, WorkDays days, IEnumerable<(TimeOnly Start, TimeOnly End)> breaks)
    {
        if (startTime == endTime) throw new DomainException("A shift cannot start and end at the same time.");
        if (days == WorkDays.None) throw new DomainException("A shift must run on at least one day.");

        StartTime = startTime;
        EndTime = endTime;
        Days = days;
        CrossesMidnight = endTime <= startTime;

        var newBreaks = breaks.Select(b => new ShiftBreak(b.Start, b.End)).OrderBy(BreakStart).ToList();
        foreach (var b in newBreaks)
            if (BreakStart(b) + BreakMinutes(b) > DurationMinutes)
                throw new DomainException($"Break {b.StartTime:HH\\:mm}-{b.EndTime:HH\\:mm} is outside the shift {StartTime:HH\\:mm}-{EndTime:HH\\:mm}.");
        for (var i = 1; i < newBreaks.Count; i++)
            if (BreakStart(newBreaks[i]) < BreakStart(newBreaks[i - 1]) + BreakMinutes(newBreaks[i - 1]))
                throw new DomainException($"Breaks {newBreaks[i - 1].StartTime:HH\\:mm} and {newBreaks[i].StartTime:HH\\:mm} overlap.");
        if (newBreaks.Sum(BreakMinutes) >= DurationMinutes)
            throw new DomainException("Total break time must be shorter than the shift.");

        Breaks.Clear();
        Breaks.AddRange(newBreaks);
    }

    public void SetActive(bool isActive) => IsActive = isActive;

    // Everything is measured in minutes from the shift start, so a break at 01:00 in a shift starting
    // at 22:00 is simply minute 180: no special case for "after midnight".
    // TimeOnly subtraction wraps past midnight: 06:00 - 22:00 = 8 h.
    private int Offset(TimeOnly t) => (int)(t - StartTime).TotalMinutes;
    private int BreakStart(ShiftBreak b) => Offset(b.StartTime);
    private static int BreakMinutes(ShiftBreak b) => (int)(b.EndTime - b.StartTime).TotalMinutes;
}

// Owned by Shift: a break only means something inside its shift.
public class ShiftBreak : IAudited
{
    public TimeOnly StartTime { get; private set; }
    public TimeOnly EndTime { get; private set; }

    private ShiftBreak() { }

    internal ShiftBreak(TimeOnly startTime, TimeOnly endTime)
    {
        if (startTime == endTime) throw new DomainException("A break cannot start and end at the same time.");
        StartTime = startTime;
        EndTime = endTime;
    }
}
