-- Spec M3 2.5 / step 10: the ScanLogs read of every scan ("last scan of this serial at this station", duplicate
-- window), measured on the seeded perf database with three index variants, plus the nvarchar parameter trap.
--   sqlcmd -S localhost,14333 -U sa -C -I -d MiniMesPerf -i scripts/perf-lastscan.sql
-- Exactly the SQL EF Core sends (ScanContextReader.LoadContextAsync), as a parameterized sp_executesql.
-- Ends with the index of the EF model, so the database matches the migration again.
SET NOCOUNT ON;
IF DB_NAME() IN (N'MiniMes', N'master') THROW 51000, 'Run perf-lastscan.sql on a perf database only.', 1;
GO

-- Runs the query @n times for random seeded serials; prints the average time per query (server side, warm cache).
CREATE OR ALTER PROCEDURE #measure @label varchar(60), @n int, @unicode bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @test int = (SELECT Id FROM dbo.Stations WHERE Code = 'L1-TEST-01');
    DECLARE @serials int = (SELECT COUNT_BIG(*) FROM dbo.ScanLogs) / 4;
    DECLARE @sql nvarchar(400) = N'SELECT TOP(1) @at = [s].[ScannedAt], @r = [s].[Result] FROM [ScanLogs] AS [s]
        WHERE [s].[SerialNo] = @serialNo AND [s].[StationId] = @stationId ORDER BY [s].[ScannedAt] DESC';
    DECLARE @params nvarchar(200) = CASE WHEN @unicode = 1
        THEN N'@serialNo nvarchar(30), @stationId int, @at datetime2 OUTPUT, @r varchar(10) OUTPUT'
        ELSE N'@serialNo varchar(30), @stationId int, @at datetime2 OUTPUT, @r varchar(10) OUTPUT' END;
    DECLARE @k int = 0, @sn varchar(30), @at datetime2, @r varchar(10), @t0 datetime2 = SYSDATETIME();
    WHILE @k < @n
    BEGIN
        SET @sn = CONCAT('SEED', RIGHT(CONCAT('000000000', ABS(CHECKSUM(NEWID())) % @serials), 9));
        EXEC sp_executesql @sql, @params, @serialNo = @sn, @stationId = @test, @at = @at OUTPUT, @r = @r OUTPUT;
        SET @k += 1;
    END
    DECLARE @us bigint = DATEDIFF(MICROSECOND, @t0, SYSDATETIME());
    PRINT CONCAT(@label, ': ', @n, ' queries, avg ', CAST(@us / 1000.0 / @n AS decimal(10, 3)), ' ms');
END
GO

-- Logical reads and plan of one query (literal serial; the TEST station of Line 1).
CREATE OR ALTER PROCEDURE #reads @unicode bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @test int = (SELECT Id FROM dbo.Stations WHERE Code = 'L1-TEST-01'), @at datetime2, @r varchar(10);
    SET STATISTICS IO ON;
    IF @unicode = 1
        EXEC sp_executesql N'SELECT TOP(1) @at = ScannedAt, @r = Result FROM ScanLogs WHERE SerialNo = @p AND StationId = @s ORDER BY ScannedAt DESC',
            N'@p nvarchar(30), @s int, @at datetime2 OUTPUT, @r varchar(10) OUTPUT', N'SEED001234567', @test, @at OUTPUT, @r OUTPUT;
    ELSE
        EXEC sp_executesql N'SELECT TOP(1) @at = ScannedAt, @r = Result FROM ScanLogs WHERE SerialNo = @p AND StationId = @s ORDER BY ScannedAt DESC',
            N'@p varchar(30), @s int, @at datetime2 OUTPUT, @r varchar(10) OUTPUT', 'SEED001234567', @test, @at OUTPUT, @r OUTPUT;
    SET STATISTICS IO OFF;
END
GO

PRINT '===== A. no index on SerialNo =====';
DROP INDEX IF EXISTS IX_ScanLogs_SerialNo_ScannedAt ON dbo.ScanLogs;
GO
EXEC #reads;
EXEC #measure 'A no index', 20;
GO
SET SHOWPLAN_TEXT ON;
GO
SELECT TOP(1) ScannedAt, Result FROM ScanLogs WHERE SerialNo = 'SEED001234567' AND StationId = 3 ORDER BY ScannedAt DESC;
GO
SET SHOWPLAN_TEXT OFF;
GO

PRINT '===== B. spec index (SerialNo, ScannedAt) =====';
CREATE INDEX IX_ScanLogs_SerialNo_ScannedAt ON dbo.ScanLogs (SerialNo, ScannedAt);
GO
EXEC #reads;
EXEC #measure 'B spec index', 5000;
GO
SET SHOWPLAN_TEXT ON;
GO
SELECT TOP(1) ScannedAt, Result FROM ScanLogs WHERE SerialNo = 'SEED001234567' AND StationId = 3 ORDER BY ScannedAt DESC;
GO
SET SHOWPLAN_TEXT OFF;
GO

SELECT i.name, SUM(ps.used_page_count) * 8 / 1024 AS UsedMB
FROM sys.dm_db_partition_stats ps JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE ps.object_id = OBJECT_ID('dbo.ScanLogs') GROUP BY i.name;
GO

PRINT '===== B2. spec index, nvarchar parameter (EF without IsUnicode(false)) =====';
EXEC #reads @unicode = 1;
EXEC #measure 'B2 nvarchar parameter', 20, @unicode = 1;
GO
SET SHOWPLAN_TEXT ON;
GO
SELECT TOP(1) ScannedAt, Result FROM ScanLogs WHERE SerialNo = N'SEED001234567' AND StationId = 3 ORDER BY ScannedAt DESC;
GO
SET SHOWPLAN_TEXT OFF;
GO

PRINT '===== C. covering index (SerialNo, ScannedAt) INCLUDE (StationId, Result) =====';
DROP INDEX IX_ScanLogs_SerialNo_ScannedAt ON dbo.ScanLogs;
CREATE INDEX IX_ScanLogs_SerialNo_ScannedAt ON dbo.ScanLogs (SerialNo, ScannedAt) INCLUDE (StationId, Result);
GO
EXEC #reads;
EXEC #measure 'C covering index', 5000;
GO
SET SHOWPLAN_TEXT ON;
GO
SELECT TOP(1) ScannedAt, Result FROM ScanLogs WHERE SerialNo = 'SEED001234567' AND StationId = 3 ORDER BY ScannedAt DESC;
GO
SET SHOWPLAN_TEXT OFF;
GO

-- Size of each variant's index: the price of INCLUDE.
SELECT i.name, SUM(ps.used_page_count) * 8 / 1024 AS UsedMB
FROM sys.dm_db_partition_stats ps JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE ps.object_id = OBJECT_ID('dbo.ScanLogs') GROUP BY i.name;
GO

PRINT '===== back to the EF model index =====';
DROP INDEX IX_ScanLogs_SerialNo_ScannedAt ON dbo.ScanLogs;
CREATE INDEX IX_ScanLogs_SerialNo_ScannedAt ON dbo.ScanLogs (SerialNo, ScannedAt);
GO
