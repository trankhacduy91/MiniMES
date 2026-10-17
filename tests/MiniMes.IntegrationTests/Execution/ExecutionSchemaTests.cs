using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Execution;

// Spec M3 step 1: the database enforces what the scan path relies on.
[Collection(ApiCollection.Name)]
public class ExecutionSchemaTests(ApiFixture api)
{
    private static readonly DateTime Now = new(2026, 10, 18, 1, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("UPDATE dbo.ScanLogs SET Seq = 99 WHERE Id = @id")]
    [InlineData("DELETE FROM dbo.ScanLogs WHERE Id = @id")]
    public async Task Scan_log_is_append_only(string sql)
    {
        var id = await AddScanLogAsync();

        var ex = await Assert.ThrowsAsync<SqlException>(() => api.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync(sql, new SqlParameter("@id", id))));

        Assert.Equal(51000, ex.Number);
        Assert.Equal(10, await api.WithDbAsync(db => db.ScanLogs.Where(s => s.Id == id).Select(s => s.Seq).SingleAsync()));
    }

    [Fact]
    public async Task Repair_log_is_append_only()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => api.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM dbo.RepairLogs")));

        Assert.Equal(51000, ex.Number);
    }

    // Two kiosks logging in at the same station at the same moment: the filtered unique index keeps one.
    [Fact]
    public async Task A_station_has_at_most_one_open_session()
    {
        var (stationId, woId) = await StationAndWorkOrderAsync();
        await api.WithDbAsync(async db =>
        {
            foreach (var open in await db.StationSessions.Where(s => s.StationId == stationId && s.ClosedAt == null).ToListAsync())
                open.Close(Now);
            db.StationSessions.Add(new StationSession(stationId, woId, "EMP00123", Now));
            return await db.SaveChangesAsync();
        });

        await Assert.ThrowsAsync<ConflictException>(() => api.WithDbAsync(db =>
        {
            db.StationSessions.Add(new StationSession(stationId, woId, "EMP00999", Now));
            return db.SaveChangesAsync();
        }));
    }

    // varchar end to end: an nvarchar column (or parameter) would make the SerialNo seek a scan (spec 2.5).
    [Fact]
    public async Task Serial_numbers_are_stored_as_varchar()
    {
        var types = await api.WithDbAsync(db => db.Database.SqlQueryRaw<string>("""
            SELECT TABLE_NAME + '.' + DATA_TYPE AS [Value] FROM INFORMATION_SCHEMA.COLUMNS
            WHERE COLUMN_NAME = 'SerialNo' AND TABLE_NAME IN ('Serials', 'ScanLogs', 'RepairLogs')
            """).ToListAsync());

        Assert.Equal(["RepairLogs.varchar", "ScanLogs.varchar", "Serials.varchar"], types.Order());
    }

    private async Task<(int StationId, int WorkOrderId)> StationAndWorkOrderAsync() => await api.WithDbAsync(async db =>
    {
        var station = await db.Stations.Where(s => s.Code == "L1-ASM-01").Select(s => s.Id).SingleAsync();
        var itemId = await db.Items.Where(i => i.Code == "PCB-A").Select(i => i.Id).SingleAsync();
        var wo = WorkOrder.Create(ApiFixture.Unique("WO"), itemId, 1, 1, Now, Now.AddHours(1));
        db.WorkOrders.Add(wo);
        await db.SaveChangesAsync();
        return (station, wo.Id);
    });

    private async Task<long> AddScanLogAsync() => await api.WithDbAsync(async db =>
    {
        var (stationId, woId) = await StationAndWorkOrderAsync();
        var serial = new Serial(ApiFixture.Unique("SN"), woId, await db.Items.Where(i => i.Code == "PCB-A").Select(i => i.Id).SingleAsync());
        db.Serials.Add(serial);
        await db.SaveChangesAsync();
        var log = new ScanLog(serial, stationId, 10, ScanResult.Ok, null, "EMP00123", ScanSource.Kiosk, Now);
        db.ScanLogs.Add(log);
        await db.SaveChangesAsync();
        return log.Id;
    });
}
