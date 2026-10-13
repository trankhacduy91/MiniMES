using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Calendar;

// A working time window in factory local time ("the morning shift starts at 06:00"), not an absolute
// instant: transactions are stored in UTC and M5 converts between the two.
public class Shift : AuditableEntity, IAudited
{
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

    public Shift(string code, string name, TimeOnly startTime, TimeOnly endTime, WorkDays days)
    {
        MasterDataRules.RequireCode(code, "Shift");
        MasterDataRules.RequireName(name);
        if (startTime == endTime) throw new DomainException("A shift cannot start and end at the same time.");
        if (days == WorkDays.None) throw new DomainException("A shift must run on at least one day.");

        Code = code;
        Name = name;
        StartTime = startTime;
        EndTime = endTime;
        Days = days;
        CrossesMidnight = endTime <= startTime;
    }

    // Break rules (inside the shift, no overlap, total < shift length) and planned minutes: M1 step 7.
    public void AddBreak(TimeOnly startTime, TimeOnly endTime) => Breaks.Add(new ShiftBreak(startTime, endTime));

    public void SetActive(bool isActive) => IsActive = isActive;
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
