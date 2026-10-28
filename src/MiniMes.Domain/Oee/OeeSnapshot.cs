namespace MiniMes.Domain.Oee;

// Spec M5 step 5: OEE of one machine for one hour of a shift, or the whole shift (Hour = null). Keeps the totals,
// not only the ratios: a day or a week is computed by adding them up and dividing again (rule 12), never by
// averaging percentages. Recomputed in place (idempotent upsert on the unique key), never duplicated (rule 13).
public class OeeSnapshot
{
    public long Id { get; private set; }
    public int MachineId { get; private set; }
    public DateOnly ShiftDate { get; private set; }      // production date: the day the shift starts (plant time)
    public int ShiftId { get; private set; }
    public byte? Hour { get; private set; }              // plant clock hour the bucket starts at; null = shift total
    public DateTime WindowStartUtc { get; private set; }
    public DateTime WindowEndUtc { get; private set; }

    public int PlannedSeconds { get; private set; }
    public int RunSeconds { get; private set; }
    public long TotalCount { get; private set; }         // machine counter
    public long GoodCount { get; private set; }          // OK scans
    public double IdealSeconds { get; private set; }     // Σ count × ideal cycle time: lets P be recomputed exactly
    public long ScanCount { get; private set; }          // scan rows, for the counter cross-check (step 9)
    public double? Availability { get; private set; }
    public double? Performance { get; private set; }
    public double? Quality { get; private set; }
    public double? Oee { get; private set; }
    public OeeWarning Warnings { get; private set; }
    public bool IsFinal { get; private set; }            // false = to (re)compute: a reason changed (rule 8)
    public DateTime CalculatedAt { get; private set; }

    private OeeSnapshot() { }

    public OeeSnapshot(int machineId, DateOnly shiftDate, int shiftId, byte? hour, DateTime windowStartUtc, DateTime windowEndUtc)
    {
        if (hour > 23) throw new ArgumentOutOfRangeException(nameof(hour), "An hour is 0-23.");
        if (windowEndUtc <= windowStartUtc) throw new ArgumentException("The window must end after it starts.", nameof(windowEndUtc));
        MachineId = machineId;
        ShiftDate = shiftDate;
        ShiftId = shiftId;
        Hour = hour;
        WindowStartUtc = windowStartUtc;
        WindowEndUtc = windowEndUtc;
    }

    public bool IsShiftTotal => Hour is null;

    // Overwrites everything: computing the same window twice gives the same row.
    public void Apply(OeeResult r, long scanCount, DateTime calculatedAt, bool isFinal)
    {
        PlannedSeconds = (int)r.PlannedTime.TotalSeconds;
        RunSeconds = (int)r.RunTime.TotalSeconds;
        TotalCount = r.TotalCount;
        GoodCount = r.GoodCount;
        IdealSeconds = r.IdealSeconds;
        ScanCount = scanCount;
        Availability = r.Availability;
        Performance = r.Performance;
        Quality = r.Quality;
        Oee = r.Oee;
        Warnings = r.Warnings;
        IsFinal = isFinal;
        CalculatedAt = calculatedAt;
    }

    // A downtime reason of this shift changed: the next job run computes it again.
    public void MarkForRecalculation() => IsFinal = false;

    public OeeResult ToResult() => new(TimeSpan.FromSeconds(PlannedSeconds), TimeSpan.FromSeconds(RunSeconds), TotalCount,
        GoodCount, IdealSeconds, Availability, Performance, Quality, Oee, Warnings);
}
