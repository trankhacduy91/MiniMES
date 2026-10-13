using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.MasterData.Boms;
using MiniMes.Domain.Common;

namespace MiniMes.Infrastructure.Persistence.Queries;

// Whole-tree explosion in one round trip with a recursive CTE (spec M1 2.5, ADR-005).
public sealed class BomExplosionQuery(AppDbContext db) : IBomExplosionQuery
{
    public async Task<IReadOnlyList<ExplodedQty>> ExplodeAsync(int itemId, decimal qty, DateTime asOfUtc, CancellationToken ct)
    {
        try
        {
            // Interpolated values become SQL parameters (FormattableString), not string concatenation.
            // ToListAsync right away: extra LINQ would wrap this in a subquery, and a CTE cannot live there.
            var rows = await db.Database.SqlQuery<Row>($"""
                WITH ActiveBom AS (      -- the one Approved version in force at @asOf per item (rule 5)
                    SELECT Id, ItemId FROM Boms
                    WHERE Status = 'Approved' AND EffectiveFrom <= {asOfUtc}
                      AND (EffectiveTo IS NULL OR {asOfUtc} < EffectiveTo)
                ),
                Explosion AS (
                    SELECT l.ComponentItemId AS ItemId,                                  -- anchor: level 1
                           CAST({qty} * l.QtyPer * (1 + l.ScrapRate) AS decimal(28,9)) AS Qty
                    FROM ActiveBom b JOIN BomLines l ON l.BomId = b.Id
                    WHERE b.ItemId = {itemId}
                    UNION ALL
                    SELECT l.ComponentItemId,                                            -- recursive: children of children
                           CAST(e.Qty * l.QtyPer * (1 + l.ScrapRate) AS decimal(28,9))    -- scrap compounds per level
                    FROM Explosion e
                    JOIN ActiveBom b ON b.ItemId = e.ItemId                              -- only items with a BOM go deeper
                    JOIN BomLines l  ON l.BomId = b.Id
                )
                SELECT ItemId, SUM(Qty) AS Qty                                           -- add branches, round later
                FROM Explosion
                GROUP BY ItemId
                OPTION (MAXRECURSION 50)
                """).ToListAsync(ct);

            return rows.Select(r => new ExplodedQty(r.ItemId, r.Qty)).ToList();
        }
        catch (SqlException ex) when (ex.Number == 530) // MAXRECURSION hit: a cycle got into the data
        {
            throw new DomainException("BOM is deeper than 50 levels; check it for a cycle.");
        }
    }

    // SqlQuery maps result columns to properties by name.
    private sealed class Row
    {
        public int ItemId { get; init; }
        public decimal Qty { get; init; }
    }
}
