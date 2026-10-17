using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Common;
using MiniMes.Domain.Planning;
using MiniMes.Infrastructure.Persistence;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Planning;

// Spec M2 step 2: the database itself refuses duplicates and lost updates, whatever the code above it does.
[Collection(ApiCollection.Name)]
public class PlanningSchemaTests(ApiFixture api)
{
    private static readonly DateTime Start = new(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Serial_sequence_hands_out_consecutive_numbers()
    {
        var numbers = await api.WithDbAsync(async db =>
        {
            var list = new List<long>();
            // ToListAsync, not SingleAsync: composing LINQ on SqlQueryRaw wraps it in a subquery, where
            // SQL Server does not allow NEXT VALUE FOR.
            for (var i = 0; i < 3; i++)
                list.AddRange(await db.Database.SqlQueryRaw<long>("SELECT NEXT VALUE FOR dbo.SerialSeq AS [Value]").ToListAsync());
            return list;
        });

        Assert.Equal([numbers[0], numbers[0] + 1, numbers[0] + 2], numbers);
    }

    [Fact]
    public async Task Duplicate_serial_number_is_refused_by_the_unique_index()
    {
        var wo = await AddWorkOrderAsync();
        var serialNo = ApiFixture.Unique("SN");

        await Assert.ThrowsAsync<ConflictException>(() => api.WithDbAsync(db =>
        {
            db.Serials.Add(new Serial(serialNo, wo.Id, wo.ItemId));
            db.Serials.Add(new Serial(serialNo, wo.Id, wo.ItemId));
            return db.SaveChangesAsync();
        }));
        Assert.Equal(0, await api.WithDbAsync(db => db.Serials.CountAsync(s => s.SerialNo == serialNo)));
    }

    [Fact]
    public async Task Duplicate_wo_number_is_refused_by_the_unique_index()
    {
        var first = await AddWorkOrderAsync();

        await Assert.ThrowsAsync<ConflictException>(() => AddWorkOrderAsync(first.WoNo));
    }

    // Two users load the same WO; the second save finds a different RowVersion and touches 0 rows.
    [Fact]
    public async Task Second_writer_with_a_stale_row_version_gets_a_concurrency_error()
    {
        var wo = await AddWorkOrderAsync();

        await api.WithDbAsync(async first =>
        {
            var mine = await first.WorkOrders.SingleAsync(w => w.Id == wo.Id);
            await api.WithDbAsync(async second =>
            {
                var theirs = await second.WorkOrders.SingleAsync(w => w.Id == wo.Id);
                theirs.ChangePriority(1);
                return await second.SaveChangesAsync();
            });

            mine.ChangePriority(9);
            await Assert.ThrowsAsync<ConflictException>(() => first.SaveChangesAsync());   // 409, from DbUpdateConcurrencyException
            return 0;
        });

        Assert.Equal(1, await api.WithDbAsync(db => db.WorkOrders.Where(w => w.Id == wo.Id).Select(w => w.Priority).SingleAsync()));
    }

    [Theory]
    [InlineData("[Priority] = 0")]
    [InlineData("[PlannedQty] = 0")]
    [InlineData("[PlannedEnd] = [PlannedStart]")]
    public async Task Check_constraints_refuse_impossible_values_written_past_the_domain(string set)
    {
        var wo = await AddWorkOrderAsync();

        var ex = await Assert.ThrowsAsync<SqlException>(() => api.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync($"UPDATE dbo.WorkOrders SET {set} WHERE Id = {wo.Id}")));

        Assert.Equal(547, ex.Number);   // CHECK constraint violation
    }

    [Fact]
    public async Task Creating_a_work_order_is_audited()
    {
        var wo = await AddWorkOrderAsync();

        var row = await api.WithDbAsync(db => db.AuditLogs
            .SingleAsync(a => a.TableName == "WorkOrders" && a.RecordKey == wo.Id.ToString() && a.ColumnName == "*"));

        Assert.Contains($"\"WoNo\":\"{wo.WoNo}\"", row.NewValue);
        Assert.Contains("\"Status\":\"Draft\"", row.NewValue);
    }

    private async Task<WorkOrder> AddWorkOrderAsync(string? woNo = null) => await api.WithDbAsync(async db =>
    {
        var itemId = await db.Items.Where(i => i.Code == "PCB-A").Select(i => i.Id).SingleAsync();
        var lineId = await db.Lines.Where(l => l.Code == "L1").Select(l => l.Id).SingleAsync();
        var wo = WorkOrder.Create(woNo ?? ApiFixture.Unique("WO"), itemId, lineId, 200, Start, Start.AddHours(4));
        db.WorkOrders.Add(wo);
        await db.SaveChangesAsync();
        return wo;
    });
}
