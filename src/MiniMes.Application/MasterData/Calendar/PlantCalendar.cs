using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.MasterData.Calendar;

namespace MiniMes.Application.MasterData.Calendar;

// [v2] Exceptions to the weekly shift pattern (spec M1 1.2.9). One row per date, so "set" is an upsert.
public sealed record GetPlantCalendarQuery(DateOnly From, DateOnly To) : IRequest<IReadOnlyList<PlantCalendarDayDto>>;
public sealed record SetPlantCalendarDayCommand(DateOnly Date, bool IsWorkingDay, string? ShiftCodes, string? Note)
    : IRequest<PlantCalendarDayDto>;
public sealed record RemovePlantCalendarDayCommand(DateOnly Date) : IRequest;
public sealed record GetScheduledShiftsQuery(DateOnly Date) : IRequest<ScheduledShiftsDto>;

public sealed class GetPlantCalendarValidator : AbstractValidator<GetPlantCalendarQuery>
{
    public GetPlantCalendarValidator()
        => RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From)
            .Must((q, to) => to.DayNumber - q.From.DayNumber <= 366).WithMessage("Ask for at most one year at a time.");
}

public sealed class SetPlantCalendarDayValidator : AbstractValidator<SetPlantCalendarDayCommand>
{
    public SetPlantCalendarDayValidator()
    {
        RuleFor(x => x.ShiftCodes).Empty().When(x => !x.IsWorkingDay)
            .WithMessage("A day off cannot list shifts; leave ShiftCodes empty.");
        RuleFor(x => x.ShiftCodes).MaximumLength(PlantCalendarDay.ShiftCodesMaxLength);
        RuleFor(x => x.Note).MaximumLength(PlantCalendarDay.NoteMaxLength);
    }
}

internal static class PlantCalendarMapping
{
    public static PlantCalendarDayDto ToDto(this PlantCalendarDay d) => new(d.Date, d.IsWorkingDay, d.ShiftCodes, d.Note);
}

public sealed class GetPlantCalendarHandler(IAppDbContext db) : IRequestHandler<GetPlantCalendarQuery, IReadOnlyList<PlantCalendarDayDto>>
{
    public async Task<IReadOnlyList<PlantCalendarDayDto>> Handle(GetPlantCalendarQuery q, CancellationToken ct)
        => (await db.PlantCalendar.AsNoTracking().Where(d => d.Date >= q.From && d.Date <= q.To)
                .OrderBy(d => d.Date).ToListAsync(ct))
            .Select(d => d.ToDto()).ToList();
}

public sealed class SetPlantCalendarDayHandler(IAppDbContext db) : IRequestHandler<SetPlantCalendarDayCommand, PlantCalendarDayDto>
{
    public async Task<PlantCalendarDayDto> Handle(SetPlantCalendarDayCommand cmd, CancellationToken ct)
    {
        var day = new PlantCalendarDay(cmd.Date, cmd.IsWorkingDay, cmd.ShiftCodes, cmd.Note); // normalises the codes
        var known = await db.Shifts.Where(s => day.ShiftCodeList.Contains(s.Code)).Select(s => s.Code).ToListAsync(ct);
        var unknown = day.ShiftCodeList.Except(known).ToList();
        if (unknown.Count > 0)
            throw new ValidationException([new ValidationFailure("ShiftCodes", $"Unknown shift(s): {string.Join(", ", unknown)}.")]);

        var existing = await db.PlantCalendar.SingleOrDefaultAsync(d => d.Date == cmd.Date, ct);
        if (existing is null) db.PlantCalendar.Add(day);
        else existing.Change(cmd.IsWorkingDay, cmd.ShiftCodes, cmd.Note);

        await db.SaveChangesAsync(ct);
        return (existing ?? day).ToDto();
    }
}

// Removing an exception just restores the weekly pattern for that date; the audit log keeps what it was.
public sealed class RemovePlantCalendarDayHandler(IAppDbContext db) : IRequestHandler<RemovePlantCalendarDayCommand>
{
    public async Task Handle(RemovePlantCalendarDayCommand cmd, CancellationToken ct)
    {
        var day = await db.PlantCalendar.SingleOrDefaultAsync(d => d.Date == cmd.Date, ct)
                  ?? throw new KeyNotFoundException($"No calendar exception on {cmd.Date:yyyy-MM-dd}.");
        db.PlantCalendar.Remove(day);
        await db.SaveChangesAsync(ct);
    }
}

// Which shifts run on a production date: the Domain rule, applied to every shift.
public sealed class GetScheduledShiftsHandler(IAppDbContext db) : IRequestHandler<GetScheduledShiftsQuery, ScheduledShiftsDto>
{
    public async Task<ScheduledShiftsDto> Handle(GetScheduledShiftsQuery q, CancellationToken ct)
    {
        var entry = await db.PlantCalendar.AsNoTracking().SingleOrDefaultAsync(d => d.Date == q.Date, ct);
        var shifts = await db.Shifts.AsNoTracking().OrderBy(s => s.StartTime).ToListAsync(ct);
        var codes = shifts.Where(s => ProductionCalendar.IsShiftScheduled(s, q.Date, entry)).Select(s => s.Code).ToList();
        return new ScheduledShiftsDto(q.Date, codes);
    }
}
