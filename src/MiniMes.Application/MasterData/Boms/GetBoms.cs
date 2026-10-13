using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Application.MasterData.Boms;

// All versions of one item's BOM, newest first (a handful per item: no paging needed).
public sealed record GetBomsQuery(int ItemId) : IRequest<IReadOnlyList<BomSummaryDto>>;

public sealed record GetBomByIdQuery(int Id) : IRequest<BomDto>;

public sealed class GetBomsHandler(IAppDbContext db) : IRequestHandler<GetBomsQuery, IReadOnlyList<BomSummaryDto>>
{
    public async Task<IReadOnlyList<BomSummaryDto>> Handle(GetBomsQuery q, CancellationToken ct)
        => await db.Boms.AsNoTracking()
            .Where(b => b.ItemId == q.ItemId)
            .OrderByDescending(b => b.Version)
            .Join(db.Items, b => b.ItemId, i => i.Id, (b, i) => new BomSummaryDto(
                b.Id, b.ItemId, i.Code, b.Version, b.Status.ToString(), b.EffectiveFrom, b.EffectiveTo, b.Lines.Count))
            .ToListAsync(ct);
}

public sealed class GetBomByIdHandler(IAppDbContext db) : IRequestHandler<GetBomByIdQuery, BomDto>
{
    public Task<BomDto> Handle(GetBomByIdQuery q, CancellationToken ct) => db.GetBomDtoAsync(q.Id, ct);
}
