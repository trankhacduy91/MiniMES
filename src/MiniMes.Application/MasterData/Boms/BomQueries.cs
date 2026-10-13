using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.MasterData.Boms;

namespace MiniMes.Application.MasterData.Boms;

internal static class BomQueries
{
    public static async Task<Bom> LoadBomAsync(this IAppDbContext db, int id, CancellationToken ct)
        => await db.Boms.Include(b => b.Lines).SingleOrDefaultAsync(b => b.Id == id, ct)
           ?? throw new KeyNotFoundException($"BOM {id} not found.");

    public static async Task<BomDto> GetBomDtoAsync(this IAppDbContext db, int id, CancellationToken ct)
    {
        var bom = await db.Boms.AsNoTracking().Include(b => b.Lines).SingleOrDefaultAsync(b => b.Id == id, ct)
                  ?? throw new KeyNotFoundException($"BOM {id} not found.");
        var ids = bom.Lines.Select(l => l.ComponentItemId).Append(bom.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

        var lines = bom.Lines
            .OrderBy(l => l.OperationSeq).ThenBy(l => items[l.ComponentItemId].Code)
            .Select(l =>
            {
                var c = items[l.ComponentItemId];
                return new BomLineDto(c.Id, c.Code, c.Name, c.Uom, l.QtyPer, l.ScrapRate, l.OperationSeq);
            })
            .ToList();
        return new BomDto(bom.Id, bom.ItemId, items[bom.ItemId].Code, bom.Version, bom.Status.ToString(),
            bom.EffectiveFrom, bom.EffectiveTo, lines);
    }

    // The graph the cycle check runs on (spec M1 2.4): every item's current versions (open Approved
    // and Draft, i.e. EffectiveTo NULL), with this item's edges replaced by the lines being saved.
    // Closed old versions are left out: v1 A->B and a later v2 B->A never apply at the same time.
    // Returns the loop as item codes ("PCB-A -> PCBA-SMT -> PCB-A"), or null.
    public static async Task<string?> FindCycleAsync(
        this IAppDbContext db, int itemId, IEnumerable<int> components, CancellationToken ct)
    {
        var edges = await db.Boms.AsNoTracking()
            .Where(b => b.ItemId != itemId && b.EffectiveTo == null)
            .SelectMany(b => b.Lines.Select(l => new { b.ItemId, l.ComponentItemId }))
            .ToListAsync(ct);

        var graph = edges.GroupBy(e => e.ItemId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ComponentItemId).Distinct().ToList());
        graph[itemId] = components.ToList();

        var cycle = BomCycleDetector.FindCycle(graph);
        if (cycle is null) return null;

        var codes = await db.Items.AsNoTracking().Where(i => cycle.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Code, ct);
        return string.Join(" -> ", cycle.Select(id => codes.GetValueOrDefault(id, $"#{id}")));
    }
}
