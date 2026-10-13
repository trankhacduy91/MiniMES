using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Common;

namespace MiniMes.Application.Factory;

public sealed record GetLinesQuery(int? AreaId) : IRequest<IReadOnlyList<LineDto>>;
public sealed record GetLineByIdQuery(int Id) : IRequest<LineDto>;
public sealed record CreateLineCommand(int AreaId, string Code, string Name) : IRequest<LineDto>;
public sealed record UpdateLineCommand(int Id, string Name, bool IsActive) : IRequest<LineDto>;

public sealed class CreateLineValidator : AbstractValidator<CreateLineCommand>
{
    public CreateLineValidator()
    {
        RuleFor(x => x.AreaId).GreaterThan(0);
        RuleFor(x => x.Code).FactoryCode();
        RuleFor(x => x.Name).FactoryName();
    }
}

public sealed class UpdateLineValidator : AbstractValidator<UpdateLineCommand>
{
    public UpdateLineValidator() => RuleFor(x => x.Name).FactoryName();
}

public sealed class GetLinesHandler(IAppDbContext db) : IRequestHandler<GetLinesQuery, IReadOnlyList<LineDto>>
{
    public async Task<IReadOnlyList<LineDto>> Handle(GetLinesQuery q, CancellationToken ct)
        => (await db.Lines.AsNoTracking()
                .Where(x => q.AreaId == null || x.AreaId == q.AreaId)
                .OrderBy(x => x.Code).ToListAsync(ct))
            .Select(x => x.ToDto()).ToList();
}

public sealed class GetLineByIdHandler(IAppDbContext db) : IRequestHandler<GetLineByIdQuery, LineDto>
{
    public async Task<LineDto> Handle(GetLineByIdQuery q, CancellationToken ct)
        => (await db.Lines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == q.Id, ct)
            ?? throw new KeyNotFoundException($"Line {q.Id} not found.")).ToDto();
}

public sealed class CreateLineHandler(IAppDbContext db) : IRequestHandler<CreateLineCommand, LineDto>
{
    public async Task<LineDto> Handle(CreateLineCommand cmd, CancellationToken ct)
    {
        var area = await db.Areas.SingleOrDefaultAsync(x => x.Id == cmd.AreaId, ct)
                   ?? throw new KeyNotFoundException($"Area {cmd.AreaId} not found.");

        if (await db.Lines.AnyAsync(x => x.AreaId == area.Id && x.Code == cmd.Code, ct))
            throw new ConflictException($"Line '{cmd.Code}' already exists in area '{area.Code}'.");

        var line = area.AddLine(cmd.Code, cmd.Name); // Domain rejects an inactive area (422)
        await db.SaveChangesAsync(ct);
        return line.ToDto();
    }
}

public sealed class UpdateLineHandler(IAppDbContext db) : IRequestHandler<UpdateLineCommand, LineDto>
{
    public async Task<LineDto> Handle(UpdateLineCommand cmd, CancellationToken ct)
    {
        var line = await db.Lines.SingleOrDefaultAsync(x => x.Id == cmd.Id, ct)
                   ?? throw new KeyNotFoundException($"Line {cmd.Id} not found.");
        line.Rename(cmd.Name);
        line.SetActive(cmd.IsActive);
        await db.SaveChangesAsync(ct);
        return line.ToDto();
    }
}
