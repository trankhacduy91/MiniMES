using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Items;

namespace MiniMes.Application.MasterData.Boms;

// Implemented in Infrastructure as one recursive CTE (ADR-005): raw SQL is a database detail.
// Returns the exact (unrounded) total per item below the root, every level, branches added.
public interface IBomExplosionQuery
{
    Task<IReadOnlyList<ExplodedQty>> ExplodeAsync(int itemId, decimal qty, DateTime asOfUtc, CancellationToken ct);
}

public sealed record ExplodedQty(int ItemId, decimal Qty);

public sealed record GetBomExplosionQuery(int ItemId, decimal Qty, DateTime? AsOfUtc) : IRequest<BomExplosionDto>;

public sealed class GetBomExplosionValidator : AbstractValidator<GetBomExplosionQuery>
{
    public GetBomExplosionValidator() => RuleFor(x => x.Qty).GreaterThan(0);
}

public sealed class GetBomExplosionHandler(IAppDbContext db, IBomExplosionQuery explosion, TimeProvider time)
    : IRequestHandler<GetBomExplosionQuery, BomExplosionDto>
{
    public async Task<BomExplosionDto> Handle(GetBomExplosionQuery q, CancellationToken ct)
    {
        var asOf = q.AsOfUtc?.ToUniversalTime() ?? time.GetUtcNow().UtcDateTime;
        var root = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == q.ItemId, ct)
                   ?? throw new KeyNotFoundException($"Item {q.ItemId} not found.");
        if (!await db.Boms.EffectiveAt(asOf).AnyAsync(b => b.ItemId == root.Id, ct))
            throw new DomainException($"Item '{root.Code}' has no approved BOM at {asOf:yyyy-MM-dd HH:mm} UTC.");

        var rows = await explosion.ExplodeAsync(root.Id, q.Qty, asOf, ct);

        var ids = rows.Select(r => r.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var withBom = (await db.Boms.EffectiveAt(asOf).Where(b => ids.Contains(b.ItemId)).Select(b => b.ItemId).ToListAsync(ct))
            .ToHashSet();
        BomQueries.EnsureSemisHaveBom(items.Values, withBom, asOf);

        var lines = rows
            .Select(r => (Row: r, Item: items[r.ItemId]))
            .OrderBy(x => x.Item.Type).ThenBy(x => x.Item.Code)
            .Select(x => new ExplosionLineDto(x.Item.Id, x.Item.Code, x.Item.Name, x.Item.Type.ToString(), x.Item.Uom,
                x.Row.Qty, Units.RoundRequirement(x.Item.Uom, x.Row.Qty)))
            .ToList();
        return new BomExplosionDto(root.Id, root.Code, q.Qty, asOf, lines);
    }
}
