using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Common;
using MiniMes.Domain.MasterData.Boms;

namespace MiniMes.Application.MasterData.Boms;

// The tree for the WPF TreeView, built in memory: every BOM effective at asOf is loaded in ONE query,
// then BomExplosion recurses over a dictionary (no query per level, no N+1).
public sealed record GetBomTreeQuery(int ItemId, decimal Qty, DateTime? AsOfUtc) : IRequest<BomTreeNodeDto>;

public sealed class GetBomTreeValidator : AbstractValidator<GetBomTreeQuery>
{
    public GetBomTreeValidator() => RuleFor(x => x.Qty).GreaterThan(0);
}

public sealed class GetBomTreeHandler(IAppDbContext db, TimeProvider time) : IRequestHandler<GetBomTreeQuery, BomTreeNodeDto>
{
    public async Task<BomTreeNodeDto> Handle(GetBomTreeQuery q, CancellationToken ct)
    {
        var asOf = q.AsOfUtc?.ToUniversalTime() ?? time.GetUtcNow().UtcDateTime;
        var root = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == q.ItemId, ct)
                   ?? throw new KeyNotFoundException($"Item {q.ItemId} not found.");

        var boms = await db.Boms.AsNoTracking().Include(b => b.Lines).EffectiveAt(asOf).ToListAsync(ct);
        var linesByItem = boms.ToDictionary(b => b.ItemId, b => (IReadOnlyList<BomLine>)b.Lines);
        if (!linesByItem.ContainsKey(root.Id))
            throw new DomainException($"Item '{root.Code}' has no approved BOM at {asOf:yyyy-MM-dd HH:mm} UTC.");

        var tree = BomExplosion.BuildTree(root.Id, q.Qty, id => linesByItem.GetValueOrDefault(id));

        var ids = BomExplosion.Totals(tree).Keys.Append(root.Id).ToList();
        var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        BomQueries.EnsureSemisHaveBom(items.Values, linesByItem.Keys.ToHashSet(), asOf);

        return ToDto(tree);

        BomTreeNodeDto ToDto(BomNode n)
        {
            var i = items[n.ItemId];
            return new BomTreeNodeDto(i.Id, i.Code, i.Name, i.Type.ToString(), i.Uom,
                n.QtyPer, n.ScrapRate, n.OperationSeq, n.RequiredQty, n.Children.Select(ToDto).ToList());
        }
    }
}
