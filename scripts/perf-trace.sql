-- Spec M6 2.7 / step 9: the trace queries of TraceQueries (step 7) on 5 million SerialComponents rows
-- (seed-trace.sql first). Sent like EF sends them: sp_executesql with parameters. Warm cache, average of N runs.
--   sqlcmd -S localhost,14333 -U sa -C -I -d MiniMesPerf -i scripts/perf-trace.sql
-- The SQL texts are copies of src/MiniMes.Infrastructure/Traceability/TraceQueries.cs: keep them in step.
-- Variants of index IX_SerialComponents_LotId: none / (LotId) INCLUDE (SerialId) / covering; parameters nvarchar
-- (a .NET string by default) or varchar (the column type). Ends with the index the EF model declares.
SET NOCOUNT ON;
IF DB_NAME() IN (N'MiniMes', N'master') THROW 51000, 'perf-trace.sql changes indexes: run it on a perf database only.', 1;

DECLARE @forward nvarchar(max) = N'
WITH LotTree AS (
    SELECT l.Id AS LotId, 0 AS Depth FROM dbo.MaterialLots l WHERE {anchor}
    UNION ALL
    SELECT g.ChildLotId, t.Depth + 1 FROM LotTree t JOIN dbo.LotGenealogy g ON g.ParentLotId = t.LotId WHERE t.Depth < @MaxDepth
),
Hit AS (
    SELECT sc.SerialId, sc.LotId, sc.StationId, sc.ScanLogId, sc.ConsumedAt, t.Depth,
           ROW_NUMBER() OVER (PARTITION BY sc.SerialId ORDER BY t.Depth, sc.ConsumedAt, sc.Id) AS rn
    FROM LotTree t JOIN dbo.SerialComponents sc ON sc.LotId = t.LotId
)
SELECT s.SerialNo, s.Status AS SerialStatus, w.WoNo, h.Depth, l.LotNo, st.Code AS StationCode, h.ConsumedAt, sl.OperatorCode,
       CASE s.Status WHEN ''Completed'' THEN ''CompletedUnpacked'' WHEN ''Scrapped'' THEN ''Scrapped'' ELSE ''Wip'' END AS Location
FROM Hit h
JOIN dbo.Serials s ON s.Id = h.SerialId
JOIN dbo.WorkOrders w ON w.Id = s.WorkOrderId
JOIN dbo.MaterialLots l ON l.Id = h.LotId
LEFT JOIN dbo.Stations st ON st.Id = h.StationId
LEFT JOIN dbo.ScanLogs sl ON sl.Id = h.ScanLogId
WHERE h.rn = 1
ORDER BY s.SerialNo
OPTION (MAXRECURSION 20)';
DECLARE @backward nvarchar(max) = N'
WITH Anc AS (
    SELECT sc.Id AS ComponentId, sc.LotId, CAST(NULL AS int) AS ChildLotId, sc.StationId, sc.ScanLogId, sc.ConsumedAt, 0 AS Depth
    FROM dbo.Serials s JOIN dbo.SerialComponents sc ON sc.SerialId = s.Id
    WHERE s.SerialNo = @SerialNo
    UNION ALL
    SELECT a.ComponentId, g.ParentLotId, a.LotId, a.StationId, a.ScanLogId, a.ConsumedAt, a.Depth + 1
    FROM Anc a JOIN dbo.LotGenealogy g ON g.ChildLotId = a.LotId
    WHERE a.Depth < @MaxDepth
),
Firsts AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY ComponentId, LotId ORDER BY Depth) AS rn FROM Anc)
SELECT a.Depth, l.LotNo, child.LotNo AS ChildLotNo, i.Code AS ItemCode, l.SupplierLot, st.Code AS StationCode,
       a.ConsumedAt, sl.OperatorCode, ld.LoadedBy
FROM Firsts a
JOIN dbo.MaterialLots l ON l.Id = a.LotId
JOIN dbo.Items i ON i.Id = l.ItemId
LEFT JOIN dbo.MaterialLots child ON child.Id = a.ChildLotId
LEFT JOIN dbo.Stations st ON st.Id = a.StationId
LEFT JOIN dbo.ScanLogs sl ON sl.Id = a.ScanLogId
OUTER APPLY (SELECT TOP (1) m.LoadedBy FROM dbo.MaterialLoads m
             WHERE a.Depth = 0 AND m.StationId = a.StationId AND m.LotId = a.LotId AND m.LoadedAt <= a.ConsumedAt
             ORDER BY m.LoadedAt DESC) ld
WHERE a.rn = 1
ORDER BY a.ConsumedAt, i.Code, a.Depth
OPTION (MAXRECURSION 20)';

CREATE TABLE #q (Query varchar(30) PRIMARY KEY, Sql nvarchar(max), Decl nvarchar(200), Args nvarchar(200));
INSERT INTO #q VALUES
 ('forward supplier lot', REPLACE(@forward, '{anchor}', 'l.SupplierLot = @SupplierLot AND l.ItemId = @ItemId'),
  N'@SupplierLot {t}, @ItemId int, @MaxDepth int', N'@SupplierLot = @key, @ItemId = @item, @MaxDepth = 10'),
 ('forward lot', REPLACE(@forward, '{anchor}', 'l.LotNo = @LotNo'), N'@LotNo {t}, @MaxDepth int', N'@LotNo = @key, @MaxDepth = 10'),
 ('backward serial', @backward, N'@SerialNo {t}, @MaxDepth int', N'@SerialNo = @key, @MaxDepth = 10');

CREATE TABLE #fwd (SerialNo varchar(30), SerialStatus nvarchar(20), WoNo varchar(30), Depth int, LotNo varchar(30), StationCode varchar(50),
                   ConsumedAt datetime2, OperatorCode varchar(50), Location varchar(20));
CREATE TABLE #bwd (Depth int, LotNo varchar(30), ChildLotNo varchar(30), ItemCode varchar(50), SupplierLot varchar(50), StationCode varchar(50),
                   ConsumedAt datetime2, OperatorCode varchar(50), LoadedBy varchar(50));
CREATE TABLE #results (Variant varchar(40), Query varchar(30), Params varchar(10), Runs int, AvgMs decimal(10, 2), LogicalReads bigint, Rows int,
                       PlanOnBigTables varchar(400));
GO

-- One query with one parameter type: a first run gives the logical reads (and warms the cache), then @runs timed runs.
CREATE PROCEDURE #measure @variant varchar(40), @query varchar(30), @varchar bit, @key varchar(30), @runs int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @item int = (SELECT ItemId FROM dbo.MaterialLots WHERE LotNo = 'PT-1-000');
    DECLARE @sql nvarchar(max), @decl nvarchar(200), @pass nvarchar(200);
    SELECT @sql = Sql,
           -- a .NET string goes out as nvarchar(4000) unless the parameter says otherwise
           @decl = REPLACE(Decl, '{t}', CASE WHEN @varchar = 1 THEN 'varchar(30)' ELSE 'nvarchar(4000)' END),
           @pass = Args
    FROM #q WHERE Query = @query;
    DECLARE @call nvarchar(max) = CASE WHEN @query LIKE 'forward%' THEN N'INSERT INTO #fwd ' ELSE N'INSERT INTO #bwd ' END
        + N'EXEC sp_executesql @sql, @decl, ' + @pass;
    DECLARE @args nvarchar(200) = N'@sql nvarchar(max), @decl nvarchar(200), @key varchar(30), @item int';
    DECLARE @reads0 bigint, @reads1 bigint, @rows int, @i int = 0, @t0 datetime2;

    TRUNCATE TABLE #fwd; TRUNCATE TABLE #bwd;
    SELECT @reads0 = logical_reads FROM sys.dm_exec_requests WHERE session_id = @@SPID;
    EXEC sp_executesql @call, @args, @sql, @decl, @key, @item;
    SELECT @reads1 = logical_reads FROM sys.dm_exec_requests WHERE session_id = @@SPID;
    SET @rows = (SELECT COUNT(*) FROM #fwd) + (SELECT COUNT(*) FROM #bwd);

    SET @t0 = SYSDATETIME();
    WHILE @i < @runs
    BEGIN
        TRUNCATE TABLE #fwd; TRUNCATE TABLE #bwd;
        EXEC sp_executesql @call, @args, @sql, @decl, @key, @item;
        SET @i += 1;
    END
    DECLARE @ms decimal(10, 2) = DATEDIFF(MICROSECOND, @t0, SYSDATETIME()) / 1000.0 / @runs;

    -- The plan actually used, from the plan cache (sp_executesql text = "(declarations)statement"): the operators
    -- on the two big tables, Key Lookups marked.
    DECLARE @plan xml = (SELECT TOP (1) qp.query_plan FROM sys.dm_exec_query_stats qs
                         CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
                         WHERE st.text LIKE '(' + @decl + ')%' ORDER BY qs.last_execution_time DESC);
    DECLARE @ops varchar(400);
    WITH XMLNAMESPACES (DEFAULT 'http://schemas.microsoft.com/sqlserver/2004/07/showplan')
    SELECT @ops = STRING_AGG(op, ', ') FROM (
        SELECT DISTINCT CASE WHEN r.exist('./IndexScan[@Lookup = 1]') = 1 THEN 'Key Lookup' ELSE r.value('@PhysicalOp', 'varchar(60)') END
               + ' ' + r.value('(./*/Object/@Table)[1]', 'varchar(60)') + ISNULL('.' + r.value('(./*/Object/@Index)[1]', 'varchar(128)'), '') AS op
        FROM @plan.nodes('//RelOp') n(r)
        WHERE r.exist('./*/Object[@Table = "[SerialComponents]" or @Table = "[Serials]"]') = 1) x;

    INSERT INTO #results VALUES (@variant, @query, CASE WHEN @varchar = 1 THEN 'varchar' ELSE 'nvarchar' END, @runs, @ms,
        @reads1 - @reads0, @rows, @ops);
END
GO

-- Supplier lot PT-SUP-3 of item 1 = reels 15..19, reel 19 split off reel 18: 12,500 serials. Lot PT-1-100: 2,500.
-- Serial PT000300000: 8 lots.
-- A. No index on SerialComponents (LotId): forward reads the whole 5-million-row table.
DROP INDEX IF EXISTS IX_SerialComponents_LotId ON dbo.SerialComponents;
EXEC #measure 'A. no index on LotId', 'forward supplier lot', 1, 'PT-SUP-3', 3;
EXEC #measure 'A. no index on LotId', 'forward lot', 1, 'PT-1-100', 3;

-- B. The EF model / spec 2.3: (LotId) INCLUDE (SerialId).
CREATE INDEX IX_SerialComponents_LotId ON dbo.SerialComponents (LotId) INCLUDE (SerialId);
EXEC #measure 'B. (LotId) INCLUDE (SerialId)', 'forward supplier lot', 1, 'PT-SUP-3', 20;
EXEC #measure 'B. (LotId) INCLUDE (SerialId)', 'forward lot', 1, 'PT-1-100', 20;
EXEC #measure 'B. (LotId) INCLUDE (SerialId)', 'forward supplier lot', 0, 'PT-SUP-3', 20;
EXEC #measure 'B. (LotId) INCLUDE (SerialId)', 'backward serial', 1, 'PT000300000', 200;
EXEC #measure 'B. (LotId) INCLUDE (SerialId)', 'backward serial', 0, 'PT000300000', 20;

-- C. Covering every column the forward query reads from SerialComponents.
CREATE INDEX IX_SerialComponents_LotId ON dbo.SerialComponents (LotId) INCLUDE (SerialId, StationId, ScanLogId, ConsumedAt)
    WITH (DROP_EXISTING = ON);
EXEC #measure 'C. covering', 'forward supplier lot', 1, 'PT-SUP-3', 20;
EXEC #measure 'C. covering', 'forward lot', 1, 'PT-1-100', 20;

SELECT r.*, (SELECT SUM(a.used_pages) * 8 / 1024 FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id
             JOIN sys.allocation_units a ON a.container_id = p.partition_id
             WHERE i.object_id = OBJECT_ID('dbo.SerialComponents') AND i.name = 'IX_SerialComponents_LotId') AS IndexMbNow
FROM #results r;
GO

-- Back to the index the EF model declares (the migration's shape), whatever was measured above.
CREATE INDEX IX_SerialComponents_LotId ON dbo.SerialComponents (LotId) INCLUDE (SerialId) WITH (DROP_EXISTING = ON);
GO
