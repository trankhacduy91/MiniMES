using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Common;
using MiniMes.Domain.Factory;

namespace MiniMes.Application.Factory;

public sealed record GetSitesQuery : IRequest<IReadOnlyList<SiteDto>>;
public sealed record GetSiteByIdQuery(int Id) : IRequest<SiteDto>;
public sealed record CreateSiteCommand(string Code, string Name) : IRequest<SiteDto>;
public sealed record UpdateSiteCommand(int Id, string Name, bool IsActive) : IRequest<SiteDto>;

public sealed class CreateSiteValidator : AbstractValidator<CreateSiteCommand>
{
    public CreateSiteValidator()
    {
        RuleFor(x => x.Code).FactoryCode();
        RuleFor(x => x.Name).FactoryName();
    }
}

public sealed class UpdateSiteValidator : AbstractValidator<UpdateSiteCommand>
{
    public UpdateSiteValidator() => RuleFor(x => x.Name).FactoryName();
}

public sealed class GetSitesHandler(IAppDbContext db) : IRequestHandler<GetSitesQuery, IReadOnlyList<SiteDto>>
{
    public async Task<IReadOnlyList<SiteDto>> Handle(GetSitesQuery q, CancellationToken ct)
        => (await db.Sites.AsNoTracking().OrderBy(x => x.Code).ToListAsync(ct)).Select(x => x.ToDto()).ToList();
}

public sealed class GetSiteByIdHandler(IAppDbContext db) : IRequestHandler<GetSiteByIdQuery, SiteDto>
{
    public async Task<SiteDto> Handle(GetSiteByIdQuery q, CancellationToken ct)
        => (await db.Sites.AsNoTracking().SingleOrDefaultAsync(x => x.Id == q.Id, ct)
            ?? throw new KeyNotFoundException($"Site {q.Id} not found.")).ToDto();
}

public sealed class CreateSiteHandler(IAppDbContext db) : IRequestHandler<CreateSiteCommand, SiteDto>
{
    public async Task<SiteDto> Handle(CreateSiteCommand cmd, CancellationToken ct)
    {
        if (await db.Sites.AnyAsync(x => x.Code == cmd.Code, ct))
            throw new ConflictException($"Site '{cmd.Code}' already exists.");

        var site = new Site(cmd.Code, cmd.Name);
        db.Sites.Add(site);
        await db.SaveChangesAsync(ct);
        return site.ToDto();
    }
}

public sealed class UpdateSiteHandler(IAppDbContext db) : IRequestHandler<UpdateSiteCommand, SiteDto>
{
    public async Task<SiteDto> Handle(UpdateSiteCommand cmd, CancellationToken ct)
    {
        var site = await db.Sites.SingleOrDefaultAsync(x => x.Id == cmd.Id, ct)
                   ?? throw new KeyNotFoundException($"Site {cmd.Id} not found.");
        site.Rename(cmd.Name);
        site.SetActive(cmd.IsActive);
        await db.SaveChangesAsync(ct);
        return site.ToDto();
    }
}
