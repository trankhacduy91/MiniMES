-- Spec M6 2.7 / step 9: 5 million SerialComponents rows to measure the trace queries on a realistic table.
-- Run against a PERF database (never dev or prod), after the API created the schema and seed:
--   sqlcmd -S localhost,14333 -U sa -C -I -d MiniMesPerf -i scripts/seed-trace.sql
-- Shape (spec 2.7): 625,000 serials x the 8 lot-controlled items = 5,000,000 rows; one reel (lot) per 2,500 serials
-- = 250 lots per item, 2,000 lots; 5 reels per supplier lot ('PT-SUP-0'..'PT-SUP-49' per item). Added to the spec's
-- shape so the recursion has work: every 5th reel was split off the reel before it (LotGenealogy, 400 edges).
-- Serials 'PT' + 9 digits never collide with real ones. Lots are history: Consumed, nothing left (not FIFO candidates).
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() IN (N'MiniMes', N'master')
    THROW 51000, 'seed-trace.sql writes millions of rows: run it on a perf database only.', 1;
IF EXISTS (SELECT 1 FROM dbo.MaterialLots WHERE LotNo LIKE 'PT-%')
    THROW 51002, 'Trace perf data already seeded (lots PT-...).', 1;

ALTER DATABASE CURRENT SET RECOVERY SIMPLE;

DECLARE @t0 datetime2 = SYSDATETIME();
DECLARE @wo int = (SELECT TOP (1) Id FROM dbo.WorkOrders ORDER BY Id),
        @pcb int = (SELECT Id FROM dbo.Items WHERE Code = 'PCB-A'),
        @smt int = (SELECT Id FROM dbo.Stations WHERE Code = 'L1-SMT-01');
IF @wo IS NULL OR @smt IS NULL THROW 51001, 'Seeded work order / stations not found: run the API and scan-bench once on this database first.', 1;

-- The 8 lot-controlled items, numbered k = 1..8.
SELECT ROW_NUMBER() OVER (ORDER BY Id) AS k, Id AS ItemId INTO #items FROM dbo.Items WHERE IsLotControlled = 1;
IF (SELECT COUNT(*) FROM #items) < 8 THROW 51003, 'Expected 8 lot-controlled items.', 1;
DELETE FROM #items WHERE k > 8;

-- 2,000 lots: item k, reel j (0..249) -> 'PT-k-jjj', supplier lot 'PT-SUP-' + j / 5.
;WITH j AS (SELECT TOP (250) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS j FROM sys.all_objects)
INSERT INTO dbo.MaterialLots (LotNo, ItemId, Qty, RemainingQty, SupplierLot, ReceivedAt, ExpiryAt, Status)
SELECT CONCAT('PT-', i.k, '-', RIGHT(CONCAT('00', j.j), 3)), i.ItemId, 10000, 0, CONCAT('PT-SUP-', j.j / 5),
       DATEADD(DAY, j.j / 5, '2026-01-01'), NULL, 2
FROM #items i CROSS JOIN j;

SELECT i.k, CAST(RIGHT(l.LotNo, 3) AS int) AS j, l.Id AS LotId
INTO #lots FROM dbo.MaterialLots l JOIN #items i ON i.ItemId = l.ItemId WHERE l.LotNo LIKE 'PT-%';
CREATE UNIQUE CLUSTERED INDEX cx ON #lots (k, j);

-- Splits: reel j (j % 5 = 4) is a child of reel j - 1 of the same item and supplier lot.
INSERT INTO dbo.LotGenealogy (ParentLotId, ChildLotId, Qty, CreatedAt)
SELECT p.LotId, c.LotId, 2000, '2026-01-01' FROM #lots c JOIN #lots p ON p.k = c.k AND p.j = c.j - 1 WHERE c.j % 5 = 4;

-- 625,000 serials of PCB-A, all completed.
;WITH n AS (SELECT TOP (625000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT INTO dbo.Serials (SerialNo, WorkOrderId, ItemId, CurrentSeq, Status)
SELECT CONCAT('PT', RIGHT(CONCAT('000000000', n), 9)), @wo, @pcb, 40, 'Completed' FROM n ORDER BY n;   -- ORDER BY: identity follows n
DECLARE @firstSerial bigint = (SELECT MIN(Id) FROM dbo.Serials WHERE SerialNo LIKE 'PT%');   -- ids are consecutive: n = Id - first

-- Without the secondary indexes, then build them once (same names and keys as the EF migration).
DROP INDEX IF EXISTS IX_SerialComponents_LotId ON dbo.SerialComponents;
DROP INDEX IF EXISTS UX_SerialComponents_SerialStationItemLot ON dbo.SerialComponents;

DECLARE @done int = 0, @batch int = 125000;   -- serials per batch = 1,000,000 rows
WHILE @done < 625000
BEGIN
    ;WITH n AS (SELECT TOP (@batch) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 + @done AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b)
    INSERT INTO dbo.SerialComponents (SerialId, LotId, ItemId, Qty, StationId, ScanLogId, ConsumedAt)
    SELECT @firstSerial + n.n, l.LotId, i.ItemId, 1, @smt, 0, DATEADD(SECOND, n.n * 30, '2026-02-01')
    FROM n CROSS JOIN #items i
    JOIN #lots l ON l.k = i.k AND l.j = n.n / 2500;
    SET @done += @batch;
    RAISERROR('%d serials', 0, 1, @done) WITH NOWAIT;
END

DECLARE @inserted int = DATEDIFF(SECOND, @t0, SYSDATETIME());
CREATE UNIQUE INDEX UX_SerialComponents_SerialStationItemLot ON dbo.SerialComponents (SerialId, StationId, ItemId, LotId);
CREATE INDEX IX_SerialComponents_LotId ON dbo.SerialComponents (LotId) INCLUDE (SerialId);
UPDATE STATISTICS dbo.SerialComponents WITH FULLSCAN;
UPDATE STATISTICS dbo.Serials WITH FULLSCAN;

SELECT (SELECT COUNT_BIG(*) FROM dbo.SerialComponents) AS ComponentRows,
       (SELECT COUNT(*) FROM dbo.Serials WHERE SerialNo LIKE 'PT%') AS PerfSerials,
       (SELECT COUNT(*) FROM #lots) AS PerfLots,
       (SELECT COUNT(*) FROM dbo.LotGenealogy g JOIN #lots l ON l.LotId = g.ChildLotId) AS Splits,
       @inserted AS InsertSeconds, DATEDIFF(SECOND, @t0, SYSDATETIME()) - @inserted AS IndexSeconds;
