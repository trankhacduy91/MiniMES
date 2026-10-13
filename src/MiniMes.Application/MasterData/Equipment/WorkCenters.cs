using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Equipment;

namespace MiniMes.Application.MasterData.Equipment;

public sealed record GetWorkCentersQuery(int? LineId, bool? IsActive) : IRequest<IReadOnlyList<WorkCenterDto>>;
public sealed record GetWorkCenterByIdQuery(int Id) : IRequest<WorkCenterDto>;
public sealed record CreateWorkCenterCommand(int LineId, string Code, string Name, string Type) : IRequest<WorkCenterDto>;
public sealed record UpdateWorkCenterCommand(int Id, string Name, string Type) : IRequest<WorkCenterDto>;
public sealed record DeactivateWorkCenterCommand(int Id) : IRequest;

public sealed class CreateWorkCenterValidator : AbstractValidator<CreateWorkCenterCommand>
{
    public CreateWorkCenterValidator()
    {
        RuleFor(x => x.LineId).GreaterThan(0);
        RuleFor(x => x.Code).MasterCode();
        RuleFor(x => x.Name).MasterName();
        RuleFor(x => x.Type).MasterCode(); // same vocabulary as RoutingStep.OperationCode: SMT, ASSY...
    }
}

public sealed class UpdateWorkCenterValidator : AbstractValidator<UpdateWorkCenterCommand>
{
    public UpdateWorkCenterValidator()
    {
        RuleFor(x => x.Name).MasterName();
        RuleFor(x => x.Type).MasterCode();
    }
}

internal static class WorkCenterMapping
{
    public static WorkCenterDto ToDto(this WorkCenter w) => new(w.Id, w.LineId, w.Code, w.Name, w.Type, w.IsActive);

    public static async Task<WorkCenter> LoadWorkCenterAsync(this IAppDbContext db, int id, CancellationToken ct)
        => await db.WorkCenters.SingleOrDefaultAsync(w => w.Id == id, ct)
           ?? throw new KeyNotFoundException($"Work center {id} not found.");
}

public sealed class GetWorkCentersHandler(IAppDbContext db) : IRequestHandler<GetWorkCentersQuery, IReadOnlyList<WorkCenterDto>>
{
    public async Task<IReadOnlyList<WorkCenterDto>> Handle(GetWorkCentersQuery q, CancellationToken ct)
        => (await db.WorkCenters.AsNoTracking()
                .Where(w => (q.LineId == null || w.LineId == q.LineId) && (q.IsActive == null || w.IsActive == q.IsActive))
                .OrderBy(w => w.Code).ToListAsync(ct))
            .Select(w => w.ToDto()).ToList();
}

public sealed class GetWorkCenterByIdHandler(IAppDbContext db) : IRequestHandler<GetWorkCenterByIdQuery, WorkCenterDto>
{
    public async Task<WorkCenterDto> Handle(GetWorkCenterByIdQuery q, CancellationToken ct)
        => (await db.LoadWorkCenterAsync(q.Id, ct)).ToDto();
}

public sealed class CreateWorkCenterHandler(IAppDbContext db) : IRequestHandler<CreateWorkCenterCommand, WorkCenterDto>
{
    public async Task<WorkCenterDto> Handle(CreateWorkCenterCommand cmd, CancellationToken ct)
    {
        var line = await db.Lines.AsNoTracking().SingleOrDefaultAsync(l => l.Id == cmd.LineId, ct)
                   ?? throw new KeyNotFoundException($"Line {cmd.LineId} not found.");
        if (!line.IsActive) throw new DomainException($"Line '{line.Code}' is inactive.");
        if (await db.WorkCenters.AnyAsync(w => w.Code == cmd.Code, ct))
            throw new ConflictException($"Work center '{cmd.Code}' already exists.");

        var wc = new WorkCenter(line.Id, cmd.Code, cmd.Name, cmd.Type);
        db.WorkCenters.Add(wc);
        await db.SaveChangesAsync(ct);
        return wc.ToDto();
    }
}

public sealed class UpdateWorkCenterHandler(IAppDbContext db) : IRequestHandler<UpdateWorkCenterCommand, WorkCenterDto>
{
    public async Task<WorkCenterDto> Handle(UpdateWorkCenterCommand cmd, CancellationToken ct)
    {
        var wc = await db.LoadWorkCenterAsync(cmd.Id, ct);
        wc.Update(cmd.Name, cmd.Type);
        await db.SaveChangesAsync(ct);
        return wc.ToDto();
    }
}

public sealed class DeactivateWorkCenterHandler(IAppDbContext db) : IRequestHandler<DeactivateWorkCenterCommand>
{
    public async Task Handle(DeactivateWorkCenterCommand cmd, CancellationToken ct)
    {
        var wc = await db.LoadWorkCenterAsync(cmd.Id, ct);
        wc.SetActive(false); // existing routings keep pointing at it; new drafts cannot pick it (step 5)
        await db.SaveChangesAsync(ct);
    }
}
