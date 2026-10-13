using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Common;
using MiniMes.Domain.Factory;

namespace MiniMes.Application.Factory;

public sealed record GetAreasQuery(int? SiteId) : IRequest<IReadOnlyList<AreaDto>>;
public sealed record GetAreaByIdQuery(int Id) : IRequest<AreaDto>;
public sealed record CreateAreaCommand(int SiteId, string Code, string Name) : IRequest<AreaDto>;
public sealed record UpdateAreaCommand(int Id, string Name, bool IsActive) : IRequest<AreaDto>;

public sealed class CreateAreaValidator : AbstractValidator<CreateAreaCommand>
{
    public CreateAreaValidator()
    {
        RuleFor(x => x.SiteId).GreaterThan(0);
        RuleFor(x => x.Code).FactoryCode();
        RuleFor(x => x.Name).FactoryName();
    }
}

public sealed class UpdateAreaValidator : AbstractValidator<UpdateAreaCommand>
{
    public UpdateAreaValidator() => RuleFor(x => x.Name).FactoryName();
}

public sealed class GetAreasHandler(IAppDbContext db) : IRequestHandler<GetAreasQuery, IReadOnlyList<AreaDto>>
{
    public async Task<IReadOnlyList<AreaDto>> Handle(GetAreasQuery q, CancellationToken ct)
        => (await db.Areas.AsNoTracking()
                .Where(x => q.SiteId == null || x.SiteId == q.SiteId)
                .OrderBy(x => x.Code).ToListAsync(ct))
            .Select(x => x.ToDto()).ToList();
}

public sealed class GetAreaByIdHandler(IAppDbContext db) : IRequestHandler<GetAreaByIdQuery, AreaDto>
{
    public async Task<AreaDto> Handle(GetAreaByIdQuery q, CancellationToken ct)
        => (await db.Areas.AsNoTracking().SingleOrDefaultAsync(x => x.Id == q.Id, ct)
            ?? throw new KeyNotFoundException($"Area {q.Id} not found.")).ToDto();
}

public sealed class CreateAreaHandler(IAppDbContext db) : IRequestHandler<CreateAreaCommand, AreaDto>
{
    public async Task<AreaDto> Handle(CreateAreaCommand cmd, CancellationToken ct)
    {
        var site = await db.Sites.SingleOrDefaultAsync(x => x.Id == cmd.SiteId, ct)
                   ?? throw new KeyNotFoundException($"Site {cmd.SiteId} not found.");

        if (await db.Areas.AnyAsync(x => x.SiteId == site.Id && x.Code == cmd.Code, ct))
            throw new ConflictException($"Area '{cmd.Code}' already exists in site '{site.Code}'.");

        var area = site.AddArea(cmd.Code, cmd.Name); // Domain rejects an inactive site (422)
        await db.SaveChangesAsync(ct);
        return area.ToDto();
    }
}

public sealed class UpdateAreaHandler(IAppDbContext db) : IRequestHandler<UpdateAreaCommand, AreaDto>
{
    public async Task<AreaDto> Handle(UpdateAreaCommand cmd, CancellationToken ct)
    {
        var area = await db.Areas.SingleOrDefaultAsync(x => x.Id == cmd.Id, ct)
                   ?? throw new KeyNotFoundException($"Area {cmd.Id} not found.");
        area.Rename(cmd.Name);
        area.SetActive(cmd.IsActive);
        await db.SaveChangesAsync(ct);
        return area.ToDto();
    }
}
