namespace MiniMes.Contracts.Oee;

// Spec M5 step 7. Ratios are 0..1 (null = undefined, e.g. a lunch hour), times in seconds, instants in UTC.
// Warnings: PerformanceOver100, GoodExceedsTotal, DowntimeExceedsPlanned (spec 1.2.7): a badge on the dashboard.
public sealed record OeeFiguresDto(
    int PlannedSeconds, int RunSeconds, long TotalCount, long GoodCount, long ScanCount, double IdealSeconds,
    double? Availability, double? Performance, double? Quality, double? Oee, IReadOnlyList<string> Warnings);

public sealed record MachineOeeDto(int MachineId, string Machine, OeeFiguresDto Figures);

// GET /api/oee/current?machine= | ?line=: the running shift up to now, computed on request (not a snapshot).
// Shift null = outside every shift (between shifts, a holiday): nothing to show. Total = the machines added up.
public sealed record CurrentOeeDto(
    DateOnly? ShiftDate, string? Shift, DateTime? FromUtc, DateTime? ToUtc,
    OeeFiguresDto? Total, IReadOnlyList<MachineOeeDto> Machines);

// GET /api/oee/history: closed snapshots. Hour = plant clock hour of the bucket start; null = the shift total.
public sealed record OeeSnapshotDto(
    int MachineId, string Machine, DateOnly ShiftDate, string Shift, int? Hour,
    DateTime WindowStartUtc, DateTime WindowEndUtc, bool IsFinal, OeeFiguresDto Figures);

// GET /api/downtime/pareto. Reason = the reason code, or "(Down)" / "(Idle)"... for stops nobody explained yet.
public sealed record ParetoRowDto(string Reason, string Name, bool CountsAgainstAvailability, double Minutes, int Count,
    double Percent, double CumulativePercent);

public sealed record ParetoDto(DateOnly From, DateOnly To, string By, double TotalMinutes, int TotalCount,
    IReadOnlyList<ParetoRowDto> Rows);

public static class ParetoBy
{
    public const string Minutes = "minutes";
    public const string Count = "count";
}

// SignalR (OeeSnapshotClosed): the job closed hours or a shift of this machine; dashboards reload the history.
public sealed record OeeSnapshotClosedDto(int MachineId, string Machine, string Line, DateOnly ShiftDate, string Shift,
    IReadOnlyList<int?> Hours);
