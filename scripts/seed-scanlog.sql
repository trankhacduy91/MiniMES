-- Spec M3 2.6 / step 10: fill dbo.ScanLogs with millions of history rows to measure the scan on a realistic table.
-- Run against a PERF database (never dev or prod), after the API created the schema and seed:
--   sqlcmd -S localhost,14333 -U sa -C -d MiniMesPerf -v Rows=10000000 -i scripts/seed-scanlog.sql
-- Shape: 4 scans per serial (10 SMT, 20 ASSY, 30 TEST, 40 PACK), 2 % NG at TEST, 2,000 serials per work order,
-- one scan per second. Serial numbers 'SEED' + 9 digits never collide with real ones (no check digit, other prefix).
-- Generated in T-SQL (ROW_NUMBER over a cross join): seconds per million rows, where EF would need minutes.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() IN (N'MiniMes', N'master')
    THROW 51000, 'seed-scanlog.sql writes millions of rows: run it on a perf database only.', 1;

-- A log test database: no point-in-time restore needed, and FULL recovery would grow the log by gigabytes.
ALTER DATABASE CURRENT SET RECOVERY SIMPLE;

DECLARE @rows int = $(Rows), @batch int = 1000000, @done int = 0, @t0 datetime2 = SYSDATETIME();
DECLARE @smt int = (SELECT Id FROM dbo.Stations WHERE Code = 'L1-SMT-01'),
        @asm int = (SELECT Id FROM dbo.Stations WHERE Code = 'L1-ASM-01'),
        @test int = (SELECT Id FROM dbo.Stations WHERE Code = 'L1-TEST-01'),
        @pack int = (SELECT Id FROM dbo.Stations WHERE Code = 'L1-PACK-01');
IF @smt IS NULL THROW 51001, 'Seeded stations not found: start the API once on this database first.', 1;

-- Inserting into a table without its secondary indexes, then building them once, is much faster than
-- maintaining two B-trees on every row. Same names and keys as the EF migration, so the model still matches.
DROP INDEX IF EXISTS IX_ScanLogs_SerialNo_ScannedAt ON dbo.ScanLogs;
DROP INDEX IF EXISTS IX_ScanLogs_WorkOrderId ON dbo.ScanLogs;

WHILE @done < @rows
BEGIN
    ;WITH n AS (
        SELECT TOP (CASE WHEN @rows - @done < @batch THEN @rows - @done ELSE @batch END)
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 + @done AS i
        FROM sys.all_objects a CROSS JOIN sys.all_objects b
    )
    INSERT INTO dbo.ScanLogs (SerialNo, SerialId, WorkOrderId, StationId, Seq, Result, DefectCode, OperatorCode, Source, ScannedAt)
    SELECT CONCAT('SEED', RIGHT(CONCAT('000000000', i / 4), 9)),
           1000000000 + i / 4,
           100000 + i / 8000,                                              -- 2,000 serials (8,000 scans) per WO
           CHOOSE(i % 4 + 1, @smt, @asm, @test, @pack),
           (i % 4 + 1) * 10,
           CASE WHEN i % 4 = 2 AND (i / 4) % 50 = 0 THEN 'Ng' ELSE 'Ok' END, -- 2 % NG at TEST
           CASE WHEN i % 4 = 2 AND (i / 4) % 50 = 0 THEN 'SLD-BRG' END,
           'EMP00123',
           'Kiosk',
           DATEADD(SECOND, i, '2026-01-01')
    FROM n;

    SET @done += @@ROWCOUNT;
    RAISERROR('%d rows', 0, 1, @done) WITH NOWAIT;
END

DECLARE @inserted int = DATEDIFF(SECOND, @t0, SYSDATETIME());
CREATE INDEX IX_ScanLogs_SerialNo_ScannedAt ON dbo.ScanLogs (SerialNo, ScannedAt);
CREATE INDEX IX_ScanLogs_WorkOrderId ON dbo.ScanLogs (WorkOrderId);
UPDATE STATISTICS dbo.ScanLogs WITH FULLSCAN;

SELECT COUNT_BIG(*) AS ScanLogRows, @inserted AS InsertSeconds, DATEDIFF(SECOND, @t0, SYSDATETIME()) - @inserted AS IndexSeconds
FROM dbo.ScanLogs;
