using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Downtime;

namespace MiniMes.Application.MasterData.Catalogs;

public sealed record GetDowntimeReasonsQuery(bool? IsActive) : IRequest<IReadOnlyList<DowntimeReasonDto>>;
public sealed record GetDowntimeReasonByIdQuery(int Id) : IRequest<DowntimeReasonDto>;
public sealed record CreateDowntimeReasonCommand(string Code, string Name, string Category, bool CountsAgainstAvailability)
    : IRequest<DowntimeReasonDto>;
public sealed record UpdateDowntimeReasonCommand(int Id, string Name, string Category, bool CountsAgainstAvailability)
    : IRequest<DowntimeReasonDto>;
public sealed record DeactivateDowntimeReasonCommand(int Id) : IRequest;

internal static class DowntimeReasonRules
{
    public static void AddCategoryRules<T>(AbstractValidator<T> v, Func<T, string> category, Func<T, bool> counts)
    {
        v.RuleFor(x => category(x)).IsEnumName(typeof(DowntimeCategory)).OverridePropertyName("Category")
            .WithMessage("Category must be Planned or Unplanned.");
        // Spec M1 1.4 rule 12: a breakdown can never be excused from Availability.
        v.RuleFor(x => counts(x)).Equal(true).OverridePropertyName("CountsAgainstAvailability")
            .When(x => category(x) == nameof(DowntimeCategory.Unplanned))
            .WithMessage("Unplanned downtime always counts against availability.");
    }

    public static DowntimeReasonDto ToDto(this DowntimeReason r)
        => new(r.Id, r.Code, r.Name, r.Category.ToString(), r.CountsAgainstAvailability, r.IsActive);

    public static async Task<DowntimeReason> LoadDowntimeReasonAsync(this IAppDbContext db, int id, CancellationToken ct)
        => await db.DowntimeReasons.SingleOrDefaultAsync(r => r.Id == id, ct)
           ?? throw new KeyNotFoundException($"Downtime reason {id} not found.");
}

public sealed class CreateDowntimeReasonValidator : AbstractValidator<CreateDowntimeReasonCommand>
{
    public CreateDowntimeReasonValidator()
    {
        RuleFor(x => x.Code).MasterCode();
        RuleFor(x => x.Name).MasterName();
        DowntimeReasonRules.AddCategoryRules(this, x => x.Category, x => x.CountsAgainstAvailability);
    }
}

public sealed class UpdateDowntimeReasonValidator : AbstractValidator<UpdateDowntimeReasonCommand>
{
    public UpdateDowntimeReasonValidator()
    {
        RuleFor(x => x.Name).MasterName();
        DowntimeReasonRules.AddCategoryRules(this, x => x.Category, x => x.CountsAgainstAvailability);
    }
}

public sealed class GetDowntimeReasonsHandler(IAppDbContext db) : IRequestHandler<GetDowntimeReasonsQuery, IReadOnlyList<DowntimeReasonDto>>
{
    public async Task<IReadOnlyList<DowntimeReasonDto>> Handle(GetDowntimeReasonsQuery q, CancellationToken ct)
        => (await db.DowntimeReasons.AsNoTracking().Where(r => q.IsActive == null || r.IsActive == q.IsActive)
                .OrderBy(r => r.Category).ThenBy(r => r.Code).ToListAsync(ct))
            .Select(r => r.ToDto()).ToList();
}

public sealed class GetDowntimeReasonByIdHandler(IAppDbContext db) : IRequestHandler<GetDowntimeReasonByIdQuery, DowntimeReasonDto>
{
    public async Task<DowntimeReasonDto> Handle(GetDowntimeReasonByIdQuery q, CancellationToken ct)
        => (await db.LoadDowntimeReasonAsync(q.Id, ct)).ToDto();
}

public sealed class CreateDowntimeReasonHandler(IAppDbContext db) : IRequestHandler<CreateDowntimeReasonCommand, DowntimeReasonDto>
{
    public async Task<DowntimeReasonDto> Handle(CreateDowntimeReasonCommand cmd, CancellationToken ct)
    {
        if (await db.DowntimeReasons.AnyAsync(r => r.Code == cmd.Code, ct))
            throw new ConflictException($"Downtime reason '{cmd.Code}' already exists.");

        var reason = new DowntimeReason(cmd.Code, cmd.Name, Enum.Parse<DowntimeCategory>(cmd.Category), cmd.CountsAgainstAvailability);
        db.DowntimeReasons.Add(reason);
        await db.SaveChangesAsync(ct);
        return reason.ToDto();
    }
}

public sealed class UpdateDowntimeReasonHandler(IAppDbContext db) : IRequestHandler<UpdateDowntimeReasonCommand, DowntimeReasonDto>
{
    public async Task<DowntimeReasonDto> Handle(UpdateDowntimeReasonCommand cmd, CancellationToken ct)
    {
        var reason = await db.LoadDowntimeReasonAsync(cmd.Id, ct);
        reason.Update(cmd.Name, Enum.Parse<DowntimeCategory>(cmd.Category), cmd.CountsAgainstAvailability);
        await db.SaveChangesAsync(ct);
        return reason.ToDto();
    }
}

public sealed class DeactivateDowntimeReasonHandler(IAppDbContext db) : IRequestHandler<DeactivateDowntimeReasonCommand>
{
    public async Task Handle(DeactivateDowntimeReasonCommand cmd, CancellationToken ct)
    {
        var reason = await db.LoadDowntimeReasonAsync(cmd.Id, ct);
        reason.SetActive(false);
        await db.SaveChangesAsync(ct);
    }
}
