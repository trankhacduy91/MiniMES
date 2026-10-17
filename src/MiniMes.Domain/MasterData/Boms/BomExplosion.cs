using MiniMes.Domain.Common;

namespace MiniMes.Domain.MasterData.Boms;

// One node of an exploded BOM tree. RequiredQty already includes the scrap of every level above.
public sealed record BomNode(
    int ItemId, decimal QtyPer, decimal ScrapRate, int OperationSeq, decimal RequiredQty, IReadOnlyList<BomNode> Children);

// In-memory explosion (spec M1 2.5). The API's explosion runs as one recursive CTE in SQL; this is the
// tree for the UI (bom-tree) and the reference the CTE is checked against in tests.
public static class BomExplosion
{
    // Same limit as OPTION (MAXRECURSION 50) in the CTE: a deeper tree means a cycle slipped past the DFS check.
    public const int MaxDepth = 50;

    /// <param name="activeLines">Lines of the item's BOM effective at the chosen time, or null when it has none (Raw).</param>
    public static BomNode BuildTree(int rootItemId, decimal qty, Func<int, IReadOnlyList<BomLine>?> activeLines)
    {
        if (qty <= 0) throw new DomainException("Quantity to explode must be greater than 0.");
        return new BomNode(rootItemId, 1m, 0m, 0, qty, Children(rootItemId, qty, 1));

        IReadOnlyList<BomNode> Children(int itemId, decimal parentQty, int depth)
        {
            if (depth > MaxDepth)
                throw new DomainException($"BOM is deeper than {MaxDepth} levels; check it for a cycle.");

            var lines = activeLines(itemId);
            if (lines is null) return [];

            return lines
                .OrderBy(l => l.OperationSeq).ThenBy(l => l.ComponentItemId)
                .Select(l =>
                {
                    // Scrap compounds level by level: the child needs more because the parent will lose some too.
                    var need = parentQty * l.QtyPer * (1 + l.ScrapRate);
                    return new BomNode(l.ComponentItemId, l.QtyPer, l.ScrapRate, l.OperationSeq, need,
                        Children(l.ComponentItemId, need, depth + 1));
                })
                .ToList();
        }
    }

    // What has to be issued for the tree: the leaves (bought material), per item AND per consuming routing step,
    // exact and unrounded. Semi-finished nodes are left out: they are made on the line, not issued (M2 snapshot).
    public static IReadOnlyList<(int ItemId, int OperationSeq, decimal Qty)> LeafMaterials(BomNode root)
    {
        var totals = new Dictionary<(int ItemId, int OperationSeq), decimal>();
        Add(root.Children);
        return totals.OrderBy(t => t.Key.OperationSeq).ThenBy(t => t.Key.ItemId)
            .Select(t => (t.Key.ItemId, t.Key.OperationSeq, t.Value)).ToList();

        void Add(IEnumerable<BomNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Children.Count > 0) { Add(n.Children); continue; }
                var key = (n.ItemId, n.OperationSeq);
                totals[key] = totals.GetValueOrDefault(key) + n.RequiredQty;
            }
        }
    }

    // Exact totals per item over the whole tree (root excluded). The same item in two branches is
    // ADDED, and nothing is rounded here: rounding happens once, on the total (spec M1 1.2.4).
    public static IReadOnlyDictionary<int, decimal> Totals(BomNode root)
    {
        var totals = new Dictionary<int, decimal>();
        Add(root.Children);
        return totals;

        void Add(IEnumerable<BomNode> nodes)
        {
            foreach (var n in nodes)
            {
                totals[n.ItemId] = totals.GetValueOrDefault(n.ItemId) + n.RequiredQty;
                Add(n.Children);
            }
        }
    }
}
