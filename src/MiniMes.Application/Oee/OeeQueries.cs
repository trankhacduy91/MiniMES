using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Oee;
using MiniMes.Domain.Oee;

namespace MiniMes.Application.Oee;

internal static class OeeMapping
{
    // Every figure the API sends goes through here with its own scan total, so the cross-check of a line or a shift
    // is decided on the sums, not carried over from one machine or one hour (rule 10).
    public static OeeFiguresDto ToDto(this OeeResult result, long scanCount)
    {
        var r = CounterCrossCheck.Apply(result, scanCount);
        return new((int)r.PlannedTime.TotalSeconds, (int)r.RunTime.TotalSeconds, r.TotalCount, r.GoodCount, scanCount,
            r.IdealSeconds, r.Availability, r.Performance, r.Quality, r.Oee, Warnings(r.Warnings));
    }

    public static IReadOnlyList<string> Warnings(OeeWarning w)
        => Enum.GetValues<OeeWarning>().Where(f => f != OeeWarning.None && w.HasFlag(f)).Select(f => f.ToString()).ToList();
}

// ---------- GET /api/oee/current: the running shift, computed now ----------

// Exactly one of the two: one machine, or every machine of a line (Line TV, dashboard).
public sealed record GetCurrentOeeQuery(string? Machine, string? Line) : IRequest<CurrentOeeDto>;

public sealed class GetCurrentOeeValidator : AbstractValidator<GetCurrentOeeQuery>
{
    public GetCurrentOeeValidator()
        => RuleFor(x => x).Must(x => (x.Machine is null) != (x.Line is null))
            .WithName("machine").WithMessage("Give either machine or line.");
}

// Spec 2.3 / 2.4 "realtime": the same reader and the same pure calculator as the snapshot job, over the shift cut at
// now (otherwise at 07:00 the planned time would be the whole shift and A = 11 %).
public sealed class GetCurrentOeeHandler(IAppDbContext db, IOeeInputReader reader, PlantTimeZone plant, TimeProvider time)
    : IRequestHandler<GetCurrentOeeQuery, CurrentOeeDto>
{
    public async Task<CurrentOeeDto> Handle(GetCurrentOeeQuery q, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var machines = await OeeScope.MachinesAsync(db, q.Machine, q.Line, ct);
        var (calendar, _) = await OeeScope.CalendarAsync(db, plant, [plant.DateOf(now)], ct);
        if (calendar.Resolve(now) is not { } shift) return new CurrentOeeDto(null, null, null, null, null, []);

        var window = shift.Until(now);
        var results = new List<(OeeMachine Machine, OeeResult Result, long Scans)>();
        foreach (var m in machines)
        {
            var reading = await reader.ReadAsync(m.Id, shift, window, ct);
            results.Add((m, OeeCalculator.Calculate(reading.Input), reading.ScanCount));
        }
        var total = OeeCalculator.Combine(results.Select(r => r.Result));   // rule 12: add up, then divide
        return new CurrentOeeDto(shift.ShiftDate, shift.ShiftCode, window.StartUtc, window.EndUtc,
            total.ToDto(results.Sum(r => r.Scans)),
            results.Select(r => new MachineOeeDto(r.Machine.Id, r.Machine.Code, r.Result.ToDto(r.Scans))).ToList());
    }
}

// ---------- GET /api/oee/shift: one shift for the dashboard ----------

public sealed record GetShiftOeeQuery(DateOnly? Date, string? Shift, string? Machine, string? Line) : IRequest<ShiftOeeDto>;

public sealed class GetShiftOeeValidator : AbstractValidator<GetShiftOeeQuery>
{
    public GetShiftOeeValidator()
    {
        RuleFor(x => x).Must(x => (x.Machine is null) != (x.Line is null))
            .WithName("machine").WithMessage("Give either machine or line.");
        RuleFor(x => x).Must(x => (x.Date is null) == (x.Shift is null))
            .WithName("date").WithMessage("Give both date and shift, or neither (the running shift).");
    }
}

public sealed class GetShiftOeeHandler(IAppDbContext db, IOeeInputReader reader, PlantTimeZone plant, TimeProvider time)
    : IRequestHandler<GetShiftOeeQuery, ShiftOeeDto>
{
    public async Task<ShiftOeeDto> Handle(GetShiftOeeQuery q, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var machines = await OeeScope.MachinesAsync(db, q.Machine, q.Line, ct);
        var date = q.Date ?? plant.DateOf(now);
        var (calendar, shifts) = await OeeScope.CalendarAsync(db, plant, [date], ct);
        var shift = q.Date is null ? calendar.Resolve(now) : calendar.WindowsOn(date).FirstOrDefault(w => w.ShiftCode == q.Shift);
        if (shift is null || shift.StartUtc >= now)   // between shifts, a holiday, or not started yet
            return new ShiftOeeDto(q.Date, q.Shift, null, null, false, null, [], []);

        var window = shift.Until(now);
        var results = new List<(OeeMachine Machine, OeeResult Result, long Scans)>();
        foreach (var m in machines)
        {
            var reading = await reader.ReadAsync(m.Id, shift, window, ct);
            results.Add((m, OeeCalculator.Calculate(reading.Input), reading.ScanCount));
        }
        var total = OeeCalculator.Combine(results.Select(r => r.Result));

        // The closed hours of every machine in scope, added up hour by hour (rule 12).
        var ids = machines.Select(m => m.Id).ToList();
        var shiftId = shifts.Single(s => s.Code == shift.ShiftCode).Id;
        var hourly = await db.OeeSnapshots.AsNoTracking()
            .Where(s => ids.Contains(s.MachineId) && s.ShiftDate == shift.ShiftDate && s.ShiftId == shiftId && s.Hour != null)
            .ToListAsync(ct);
        var hours = hourly.GroupBy(s => s.Hour!.Value)
            .OrderBy(g => g.Min(s => s.WindowStartUtc))
            .Select(g => new HourOeeDto(g.Key, g.Min(s => s.WindowStartUtc), g.Max(s => s.WindowEndUtc),
                OeeCalculator.Combine(g.Select(s => s.ToResult())).ToDto(g.Sum(s => s.ScanCount))))
            .ToList();

        return new ShiftOeeDto(shift.ShiftDate, shift.ShiftCode, window.StartUtc, window.EndUtc, now < shift.EndUtc,
            total.ToDto(results.Sum(r => r.Scans)),
            results.Select(r => new MachineOeeDto(r.Machine.Id, r.Machine.Code, r.Result.ToDto(r.Scans))).ToList(), hours);
    }
}

// ---------- GET /api/oee/history: closed snapshots ----------

public sealed record GetOeeHistoryQuery(DateOnly From, DateOnly To, string? Machine, string? Line, bool Hourly)
    : IRequest<IReadOnlyList<OeeSnapshotDto>>;

public sealed class GetOeeHistoryValidator : AbstractValidator<GetOeeHistoryQuery>
{
    public const int MaxDays = 93;

    public GetOeeHistoryValidator()
        => RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From)
            .Must((x, to) => to.DayNumber - x.From.DayNumber < MaxDays).WithMessage($"At most {MaxDays} days.");
}

public sealed class GetOeeHistoryHandler(IAppDbContext db) : IRequestHandler<GetOeeHistoryQuery, IReadOnlyList<OeeSnapshotDto>>
{
    public async Task<IReadOnlyList<OeeSnapshotDto>> Handle(GetOeeHistoryQuery q, CancellationToken ct)
    {
        var machines = await OeeScope.MachinesAsync(db, q.Machine, q.Line, ct);
        var ids = machines.Select(m => m.Id).ToList();
        var rows = await (
                from s in db.OeeSnapshots
                join sh in db.Shifts on s.ShiftId equals sh.Id
                where ids.Contains(s.MachineId) && s.ShiftDate >= q.From && s.ShiftDate <= q.To && (q.Hourly || s.Hour == null)
                orderby s.ShiftDate, s.WindowStartUtc, s.MachineId, s.Hour
                select new { Snapshot = s, Shift = sh.Code })
            .AsNoTracking().ToListAsync(ct);
        var codes = machines.ToDictionary(m => m.Id, m => m.Code);
        return rows.Select(r => new OeeSnapshotDto(r.Snapshot.MachineId, codes[r.Snapshot.MachineId], r.Snapshot.ShiftDate, r.Shift,
                r.Snapshot.Hour, r.Snapshot.WindowStartUtc, r.Snapshot.WindowEndUtc, r.Snapshot.IsFinal,
                r.Snapshot.ToResult().ToDto(r.Snapshot.ScanCount)))
            .ToList();
    }
}
