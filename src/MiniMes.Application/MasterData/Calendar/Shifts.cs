using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Calendar;

namespace MiniMes.Application.MasterData.Calendar;

public sealed record GetShiftsQuery(bool? IsActive) : IRequest<IReadOnlyList<ShiftDto>>;
public sealed record GetShiftByIdQuery(int Id) : IRequest<ShiftDto>;
public sealed record CreateShiftCommand(
    string Code, string Name, TimeOnly StartTime, TimeOnly EndTime, IReadOnlyList<string> Days,
    IReadOnlyList<ShiftBreakDto> Breaks) : IRequest<ShiftDto>;
public sealed record UpdateShiftCommand(
    int Id, string Name, TimeOnly StartTime, TimeOnly EndTime, IReadOnlyList<string> Days,
    IReadOnlyList<ShiftBreakDto> Breaks) : IRequest<ShiftDto>;
public sealed record DeactivateShiftCommand(int Id) : IRequest;

internal static class ShiftRules
{
    // ["Mon","Tue"] <-> WorkDays.Mon | WorkDays.Tue. Single days only, so the API stays readable.
    public static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    public static WorkDays ToWorkDays(IEnumerable<string> days)
        => days.Aggregate(WorkDays.None, (acc, d) => acc | Enum.Parse<WorkDays>(d));

    public static IReadOnlyList<string> ToNames(WorkDays days)
        => DayNames.Where(d => days.HasFlag(Enum.Parse<WorkDays>(d))).ToList();

    public static IEnumerable<(TimeOnly, TimeOnly)> ToTuples(IEnumerable<ShiftBreakDto> breaks)
        => breaks.Select(b => (b.StartTime, b.EndTime));

    public static ShiftDto ToDto(this Shift s)
        => new(s.Id, s.Code, s.Name, s.StartTime, s.EndTime, ToNames(s.Days), s.CrossesMidnight,
            s.DurationMinutes, s.PlannedProductionMinutes,
            s.Breaks.Select(b => new ShiftBreakDto(b.StartTime, b.EndTime)).ToList(), s.IsActive);

    // Field checks, then the Domain's time rules (break inside the shift, no overlap, total < length)
    // run on a throw-away Shift, so a bad break is a 400 on "Breaks" and the rule lives in one place.
    public static void AddScheduleRules<T>(AbstractValidator<T> v, Func<T, TimeOnly> start, Func<T, TimeOnly> end,
        Func<T, IReadOnlyList<string>> days, Func<T, IReadOnlyList<ShiftBreakDto>> breaks)
    {
        v.RuleFor(x => days(x)).NotEmpty().OverridePropertyName("Days");
        v.RuleForEach(x => days(x)).Must(d => DayNames.Contains(d)).OverridePropertyName("Days")
            .WithMessage($"Days must be among: {string.Join(", ", DayNames)}.");
        v.RuleFor(x => breaks(x)).NotNull().OverridePropertyName("Breaks");
        v.RuleFor(x => x).Custom((x, ctx) =>
        {
            if (days(x) is not { Count: > 0 } d || d.Any(n => !DayNames.Contains(n)) || breaks(x) is null) return;
            try
            {
                _ = new Shift("CHECK", "check", start(x), end(x), ToWorkDays(d), ToTuples(breaks(x)));
            }
            catch (DomainException ex)
            {
                ctx.AddFailure(start(x) == end(x) ? "EndTime" : "Breaks", ex.Message);
            }
        });
    }

    public static async Task<Shift> LoadShiftAsync(this IAppDbContext db, int id, CancellationToken ct)
        => await db.Shifts.SingleOrDefaultAsync(s => s.Id == id, ct)
           ?? throw new KeyNotFoundException($"Shift {id} not found.");
}

public sealed class CreateShiftValidator : AbstractValidator<CreateShiftCommand>
{
    public CreateShiftValidator()
    {
        RuleFor(x => x.Code).MasterCode().MaximumLength(Shift.CodeMaxLength);
        RuleFor(x => x.Name).MasterName();
        ShiftRules.AddScheduleRules(this, x => x.StartTime, x => x.EndTime, x => x.Days, x => x.Breaks);
    }
}

public sealed class UpdateShiftValidator : AbstractValidator<UpdateShiftCommand>
{
    public UpdateShiftValidator()
    {
        RuleFor(x => x.Name).MasterName();
        ShiftRules.AddScheduleRules(this, x => x.StartTime, x => x.EndTime, x => x.Days, x => x.Breaks);
    }
}

public sealed class GetShiftsHandler(IAppDbContext db) : IRequestHandler<GetShiftsQuery, IReadOnlyList<ShiftDto>>
{
    public async Task<IReadOnlyList<ShiftDto>> Handle(GetShiftsQuery q, CancellationToken ct)
        => (await db.Shifts.AsNoTracking().Where(s => q.IsActive == null || s.IsActive == q.IsActive)
                .OrderBy(s => s.StartTime).ToListAsync(ct))
            .Select(s => s.ToDto()).ToList();
}

public sealed class GetShiftByIdHandler(IAppDbContext db) : IRequestHandler<GetShiftByIdQuery, ShiftDto>
{
    public async Task<ShiftDto> Handle(GetShiftByIdQuery q, CancellationToken ct) => (await db.LoadShiftAsync(q.Id, ct)).ToDto();
}

public sealed class CreateShiftHandler(IAppDbContext db) : IRequestHandler<CreateShiftCommand, ShiftDto>
{
    public async Task<ShiftDto> Handle(CreateShiftCommand cmd, CancellationToken ct)
    {
        if (await db.Shifts.AnyAsync(s => s.Code == cmd.Code, ct))
            throw new ConflictException($"Shift '{cmd.Code}' already exists.");

        var shift = new Shift(cmd.Code, cmd.Name, cmd.StartTime, cmd.EndTime,
            ShiftRules.ToWorkDays(cmd.Days), ShiftRules.ToTuples(cmd.Breaks));
        db.Shifts.Add(shift);
        await db.SaveChangesAsync(ct);
        return shift.ToDto();
    }
}

public sealed class UpdateShiftHandler(IAppDbContext db) : IRequestHandler<UpdateShiftCommand, ShiftDto>
{
    public async Task<ShiftDto> Handle(UpdateShiftCommand cmd, CancellationToken ct)
    {
        var shift = await db.LoadShiftAsync(cmd.Id, ct);
        shift.Rename(cmd.Name);
        shift.Reschedule(cmd.StartTime, cmd.EndTime, ShiftRules.ToWorkDays(cmd.Days), ShiftRules.ToTuples(cmd.Breaks));
        await db.SaveChangesAsync(ct);
        return shift.ToDto();
    }
}

public sealed class DeactivateShiftHandler(IAppDbContext db) : IRequestHandler<DeactivateShiftCommand>
{
    public async Task Handle(DeactivateShiftCommand cmd, CancellationToken ct)
    {
        var shift = await db.LoadShiftAsync(cmd.Id, ct);
        shift.SetActive(false);
        await db.SaveChangesAsync(ct);
    }
}
