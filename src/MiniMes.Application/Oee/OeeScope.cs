using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Domain.MasterData.Calendar;
using MiniMes.Domain.Time;

namespace MiniMes.Application.Oee;

public sealed record OeeMachine(int Id, string Code, string Line);

// Shared by the snapshot job, the current-shift query and the Pareto: the shift calendar of some production dates
// and the machines of a machine code / line filter.
public static class OeeScope
{
    public static async Task<(ShiftCalendar Calendar, IReadOnlyList<Shift> Shifts)> CalendarAsync(
        IAppDbContext db, PlantTimeZone plant, IReadOnlyCollection<DateOnly> dates, CancellationToken ct)
    {
        var shifts = await db.Shifts.Where(s => s.IsActive).ToListAsync(ct);   // breaks are owned: loaded with them
        // The night shift of the day before the first date may run into it.
        var days = dates.Concat(dates.Select(d => d.AddDays(-1))).Distinct().ToList();
        var calendarDays = await db.PlantCalendar.Where(d => days.Contains(d.Date)).ToListAsync(ct);
        return (new ShiftCalendar(plant.Zone, shifts, calendarDays), shifts);
    }

    /// <summary>Active machines; both filters null = every machine.</summary>
    /// <exception cref="KeyNotFoundException">The machine or line code does not exist (404).</exception>
    public static async Task<IReadOnlyList<OeeMachine>> MachinesAsync(IAppDbContext db, string? machine, string? line, CancellationToken ct)
    {
        var rows = await (
                from m in db.Machines
                join w in db.WorkCenters on m.WorkCenterId equals w.Id
                join l in db.Lines on w.LineId equals l.Id
                where m.IsActive && (machine == null || m.Code == machine) && (line == null || l.Code == line)
                orderby m.Code
                select new OeeMachine(m.Id, m.Code, l.Code))
            .ToListAsync(ct);
        if (rows.Count == 0 && machine is not null) throw new KeyNotFoundException($"Machine '{machine}' not found.");
        if (rows.Count == 0 && line is not null && !await db.Lines.AnyAsync(l => l.Code == line, ct))
            throw new KeyNotFoundException($"Line '{line}' not found.");
        return rows;
    }

    public static IEnumerable<DateOnly> Dates(DateOnly from, DateOnly to)
        => Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays);
}
