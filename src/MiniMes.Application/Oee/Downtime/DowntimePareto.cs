using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Oee;
using MiniMes.Domain.Oee;

namespace MiniMes.Application.Oee.Downtime;

// GET /api/downtime/pareto?from&to&machine|line&shift&by=minutes|count&includePlanned=false (spec 1.2.9, 2.5).
// Shift: one shift code (the Pareto of a shift handover); null = every shift.
public sealed record GetDowntimeParetoQuery(DateOnly From, DateOnly To, string? Machine, string? Line,
    string By = ParetoBy.Minutes, bool IncludePlanned = false, string? Shift = null) : IRequest<ParetoDto>;

public sealed class GetDowntimeParetoValidator : AbstractValidator<GetDowntimeParetoQuery>
{
    public const int MaxDays = 31;

    public GetDowntimeParetoValidator()
    {
        RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From)
            .Must((x, to) => to.DayNumber - x.From.DayNumber < MaxDays).WithMessage($"At most {MaxDays} days.");
        RuleFor(x => x.By).Must(b => b is ParetoBy.Minutes or ParetoBy.Count)
            .WithMessage($"By must be '{ParetoBy.Minutes}' or '{ParetoBy.Count}'.");
    }
}

// Built from the same stop list the reader uses for Availability, shift by shift (breaks, planned stops and micro
// stops already taken out), not from a separate SQL over MachineStateHistory: the Pareto minutes are then exactly
// the minutes Availability lost, and a manager comparing the two never finds a gap (spec 2.5, trap 4).
// Cost: one read per machine and shift; fine for a line over a month, a report over a year would need its own table.
public sealed class GetDowntimeParetoHandler(IAppDbContext db, IOeeInputReader reader, PlantTimeZone plant, TimeProvider time)
    : IRequestHandler<GetDowntimeParetoQuery, ParetoDto>
{
    public async Task<ParetoDto> Handle(GetDowntimeParetoQuery q, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var machines = await OeeScope.MachinesAsync(db, q.Machine, q.Line, ct);
        var dates = OeeScope.Dates(q.From, q.To).ToList();
        var (calendar, _) = await OeeScope.CalendarAsync(db, plant, dates, ct);

        var stops = new List<StopLoss>();
        foreach (var shift in dates.SelectMany(calendar.WindowsOn).Where(w => w.StartUtc < now && (q.Shift is null || w.ShiftCode == q.Shift)))
            foreach (var m in machines)
                stops.AddRange((await reader.ReadAsync(m.Id, shift, shift.Until(now), ct)).Stops
                    .Where(s => s.CountsAgainstAvailability || q.IncludePlanned));

        var reasons = await db.DowntimeReasons.AsNoTracking().ToDictionaryAsync(r => r.Id, r => (r.Code, r.Name), ct);
        var groups = stops
            .GroupBy(s => s.ReasonId is { } id ? reasons[id].Code : $"({s.State})")
            .Select(g => (Reason: g.Key,
                Name: g.First().ReasonId is { } id ? reasons[id].Name : $"{g.First().State}, not classified yet",
                Counts: g.First().CountsAgainstAvailability,
                Minutes: g.Sum(s => s.Length.TotalMinutes), Count: g.Count()));
        return Pareto.Rank(q.From, q.To, q.By, groups);
    }
}

public static class Pareto
{
    // Sorted by the chosen measure, biggest first (ties by code, stable); % of the total and running %.
    // By minutes = what hurts most (breakdowns); by count = what repeats most (small jams): spec 1.2.9.
    public static ParetoDto Rank(DateOnly from, DateOnly to, string by,
        IEnumerable<(string Reason, string Name, bool Counts, double Minutes, int Count)> groups)
    {
        double Measure((string, string, bool, double Minutes, int Count) g) => by == ParetoBy.Count ? g.Count : g.Minutes;
        var sorted = groups.OrderByDescending(Measure).ThenBy(g => g.Reason, StringComparer.Ordinal).ToList();
        var total = sorted.Sum(Measure);
        var running = 0.0;
        var rows = sorted.Select(g =>
        {
            running += Measure(g);
            return new ParetoRowDto(g.Reason, g.Name, g.Counts, Math.Round(g.Minutes, 2), g.Count,
                total == 0 ? 0 : Math.Round(100 * Measure(g) / total, 2), total == 0 ? 0 : Math.Round(100 * running / total, 2));
        }).ToList();
        return new ParetoDto(from, to, by, Math.Round(sorted.Sum(g => g.Minutes), 2), sorted.Sum(g => g.Count), rows);
    }
}
