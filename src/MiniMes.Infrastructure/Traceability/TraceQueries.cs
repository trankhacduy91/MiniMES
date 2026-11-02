using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Traceability;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Traceability;

// Spec M6 2.2 as SQL through EF Core's SqlQueryRaw (same choice as the M3 reports: one data-access library, EF
// already has the connection; the query is never composed with LINQ, so EF does not wrap the CTE in a subquery).
// Two phases: the recursion walks only LotGenealogy (small), the huge SerialComponents is joined once afterwards.
// Cycles (R12 is checked on write, but data can still be wrong): Depth < MaxDepth stops every branch first;
// MAXRECURSION is the hard stop behind it (an error instead of a hang if the guard were ever removed).
public sealed class TraceQueries(AppDbContext db) : ITraceQueries
{
    public const int MaxDepth = 10;

    public async Task<IReadOnlyList<ForwardTraceRow>> ForwardByLotAsync(string lotNo, CancellationToken ct)
        => await db.Database.SqlQueryRaw<ForwardTraceRow>(Forward("l.LotNo = @LotNo"),
            new SqlParameter("@LotNo", lotNo), Depth()).ToListAsync(ct);

    // A supplier lot is several reels: the anchor starts the tree from all of them (index (SupplierLot, ItemId)).
    public async Task<IReadOnlyList<ForwardTraceRow>> ForwardBySupplierLotAsync(string supplierLot, int itemId, CancellationToken ct)
        => await db.Database.SqlQueryRaw<ForwardTraceRow>(Forward("l.SupplierLot = @SupplierLot AND l.ItemId = @ItemId"),
            new SqlParameter("@SupplierLot", supplierLot), new SqlParameter("@ItemId", itemId), Depth()).ToListAsync(ct);

    public async Task<IReadOnlyList<BackwardTraceRow>> BackwardAsync(string serialNo, CancellationToken ct)
        => await db.Database.SqlQueryRaw<BackwardTraceRow>(BackwardSql,
            new SqlParameter("@SerialNo", serialNo), Depth()).ToListAsync(ct);

    private static SqlParameter Depth() => new("@MaxDepth", MaxDepth);

    // The anchor is one of the two constant conditions above, never user text: values travel as parameters.
    private static string Forward(string anchor) => $"""
        WITH LotTree AS (
            SELECT l.Id AS LotId, 0 AS Depth
            FROM   dbo.MaterialLots l
            WHERE  {anchor}
            UNION ALL
            -- parent -> child: index (ParentLotId, ChildLotId), one seek per level
            SELECT g.ChildLotId, t.Depth + 1
            FROM   LotTree t
            JOIN   dbo.LotGenealogy g ON g.ParentLotId = t.LotId
            WHERE  t.Depth < @MaxDepth
        ),
        Hit AS (
            -- the big table, joined once (index (LotId) INCLUDE (SerialId)). A board reached through several lots
            -- of the tree (two reels of one supplier lot, a diamond, a cycle) keeps one row: the closest lot.
            SELECT sc.SerialId, sc.LotId, sc.StationId, sc.ScanLogId, sc.ConsumedAt, t.Depth,
                   ROW_NUMBER() OVER (PARTITION BY sc.SerialId ORDER BY t.Depth, sc.ConsumedAt, sc.Id) AS rn
            FROM   LotTree t
            JOIN   dbo.SerialComponents sc ON sc.LotId = t.LotId
        )
        SELECT s.SerialNo, s.Status AS SerialStatus, w.WoNo, h.Depth, l.LotNo, st.Code AS StationCode, h.ConsumedAt,
               sl.OperatorCode,
               CASE s.Status WHEN 'Completed' THEN 'CompletedUnpacked' WHEN 'Scrapped' THEN 'Scrapped' ELSE 'Wip' END AS Location
        FROM   Hit h
        JOIN   dbo.Serials s       ON s.Id = h.SerialId
        JOIN   dbo.WorkOrders w    ON w.Id = s.WorkOrderId
        JOIN   dbo.MaterialLots l  ON l.Id = h.LotId
        LEFT JOIN dbo.Stations st  ON st.Id = h.StationId      -- no FK on SerialComponents (hot insert)
        LEFT JOIN dbo.ScanLogs sl  ON sl.Id = h.ScanLogId
        WHERE  h.rn = 1
        ORDER BY s.SerialNo
        OPTION (MAXRECURSION 20)
        """;

    private const string BackwardSql = """
        WITH Anc AS (
            -- the board's own consumption: seek on the unique (SerialId, StationId, ItemId, LotId)
            SELECT sc.Id AS ComponentId, sc.LotId, CAST(NULL AS int) AS ChildLotId, sc.StationId, sc.ScanLogId,
                   sc.ConsumedAt, 0 AS Depth
            FROM   dbo.Serials s
            JOIN   dbo.SerialComponents sc ON sc.SerialId = s.Id
            WHERE  s.SerialNo = @SerialNo
            UNION ALL
            -- child -> parent: index (ChildLotId) INCLUDE (ParentLotId, Qty)
            SELECT a.ComponentId, g.ParentLotId, a.LotId, a.StationId, a.ScanLogId, a.ConsumedAt, a.Depth + 1
            FROM   Anc a
            JOIN   dbo.LotGenealogy g ON g.ChildLotId = a.LotId
            WHERE  a.Depth < @MaxDepth
        ),
        Firsts AS (
            -- a cycle brings the same lot back at deeper levels: keep its first appearance per consumption
            SELECT *, ROW_NUMBER() OVER (PARTITION BY ComponentId, LotId ORDER BY Depth) AS rn
            FROM   Anc
        )
        SELECT a.Depth, l.LotNo, child.LotNo AS ChildLotNo, i.Code AS ItemCode, l.SupplierLot, st.Code AS StationCode,
               a.ConsumedAt, sl.OperatorCode, ld.LoadedBy
        FROM   Firsts a
        JOIN   dbo.MaterialLots l      ON l.Id = a.LotId
        JOIN   dbo.Items i             ON i.Id = l.ItemId
        LEFT JOIN dbo.MaterialLots child ON child.Id = a.ChildLotId
        LEFT JOIN dbo.Stations st      ON st.Id = a.StationId
        LEFT JOIN dbo.ScanLogs sl      ON sl.Id = a.ScanLogId
        -- who put the reel on the machine: the load of that lot on that station running at the consumption time
        OUTER APPLY (SELECT TOP (1) m.LoadedBy
                     FROM   dbo.MaterialLoads m
                     WHERE  a.Depth = 0 AND m.StationId = a.StationId AND m.LotId = a.LotId AND m.LoadedAt <= a.ConsumedAt
                     ORDER  BY m.LoadedAt DESC) ld
        WHERE  a.rn = 1
        ORDER BY a.ConsumedAt, i.Code, a.Depth
        OPTION (MAXRECURSION 20)
        """;
}
