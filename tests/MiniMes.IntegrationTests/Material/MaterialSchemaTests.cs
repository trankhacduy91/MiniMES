using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Common;
using MiniMes.Domain.Material;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Material;

// Spec M6 step 1 check: the rules the database itself enforces, whatever the application does. Each test uses its
// own raw item, so open loads of other tests on the same station never collide with it.
[Collection(ApiCollection.Name)]
public class MaterialSchemaTests(ApiFixture api)
{
    private static readonly DateTime T0 = new(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);

    private sealed record Setup(int ItemId, int StationId, int WorkOrderId, int LotId, int OtherLotId);

    private Task<Setup> SetupAsync() => api.WithDbAsync(async db =>
    {
        var item = Item.Create(ApiFixture.Unique("CAP-T"), "Test capacitor", ItemType.Raw, Units.Pieces, false, true);
        db.Items.Add(item);
        await db.SaveChangesAsync();
        var station = await db.Stations.SingleAsync(s => s.Code == "L1-SMT-01");
        var pcbA = await db.Items.SingleAsync(i => i.Code == "PCB-A");
        var wo = WorkOrder.Create(ApiFixture.Unique("WO-M6"), pcbA.Id, station.LineId, 10, T0, T0.AddHours(8), requireFai: false);
        db.WorkOrders.Add(wo);
        var lot = new MaterialLot(ApiFixture.Unique("L-T"), item.Id, 10_000, "SUP-T", T0, T0.AddYears(2), needsIqc: false);
        var other = new MaterialLot(ApiFixture.Unique("L-T"), item.Id, 10_000, "SUP-T", T0, T0.AddYears(2), needsIqc: false);
        db.MaterialLots.AddRange(lot, other);
        await db.SaveChangesAsync();
        return new Setup(item.Id, station.Id, wo.Id, lot.Id, other.Id);
    });

    // R6: one lot per (station, item) at a time. Two kiosks loading at once: the second gets a 409, not two loads.
    [Fact]
    public async Task Database_refuses_a_second_open_load_of_the_same_item_on_a_station()
    {
        var s = await SetupAsync();
        await api.WithDbAsync(db =>
        {
            db.MaterialLoads.Add(new MaterialLoad(s.StationId, s.ItemId, s.LotId, s.WorkOrderId, null, "EMP00123", T0));
            return db.SaveChangesAsync();
        });

        await Assert.ThrowsAsync<ConflictException>(() => api.WithDbAsync(db =>
        {
            db.MaterialLoads.Add(new MaterialLoad(s.StationId, s.ItemId, s.OtherLotId, s.WorkOrderId, null, "EMP00456", T0));
            return db.SaveChangesAsync();
        }));

        // Unloaded first, then the new reel goes on: allowed, and the closed load stays as history.
        await api.WithDbAsync(async db =>
        {
            var open = await db.MaterialLoads.SingleAsync(l => l.ItemId == s.ItemId && l.UnloadedAt == null);
            open.Unload("EMP00123", T0.AddHours(1));
            db.MaterialLoads.Add(new MaterialLoad(s.StationId, s.ItemId, s.OtherLotId, s.WorkOrderId, "12", "EMP00123", T0.AddHours(1)));
            await db.SaveChangesAsync();
            (await db.MaterialLoads.SingleAsync(l => l.ItemId == s.ItemId && l.UnloadedAt == null)).Unload("EMP00123", T0.AddHours(2));
            return await db.SaveChangesAsync();
        });
        Assert.Equal(2, await api.WithDbAsync(db => db.MaterialLoads.CountAsync(l => l.ItemId == s.ItemId)));
    }

    // R10: theoretical stock is never negative and never above the received quantity, even for raw SQL (step 5
    // subtracts with an UPDATE that skips the entity).
    [Theory]
    [InlineData("-1")]
    [InlineData("10001")]
    public async Task Database_refuses_remaining_quantity_out_of_range(string remaining)
    {
        var s = await SetupAsync();

        var ex = await Assert.ThrowsAsync<SqlException>(() => api.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync($"UPDATE MaterialLots SET RemainingQty = {remaining} WHERE Id = {s.LotId}")));
        Assert.Contains("CK_MaterialLots_Qty", ex.Message);
    }

    // R9 + R11: one component row per (serial, station, item, lot); the rows cannot be changed or deleted.
    [Fact]
    public async Task Serial_components_are_unique_and_insert_only()
    {
        var s = await SetupAsync();
        SerialComponent Row() => new(serialId: 900_000_001, s.LotId, s.ItemId, 4, s.StationId, scanLogId: 1, T0);
        await api.WithDbAsync(db => { db.SerialComponents.Add(Row()); return db.SaveChangesAsync(); });

        await Assert.ThrowsAsync<ConflictException>(() => api.WithDbAsync(db => { db.SerialComponents.Add(Row()); return db.SaveChangesAsync(); }));
        foreach (var sql in new[] { $"UPDATE SerialComponents SET Qty = 1 WHERE LotId = {s.LotId}", $"DELETE FROM SerialComponents WHERE LotId = {s.LotId}" })
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql)));
            Assert.Contains("append-only", ex.Message);
        }
    }

    // R12: a lot is never its own parent (check constraint); genealogy rows are insert-only too.
    [Fact]
    public async Task Genealogy_refuses_a_self_edge_and_edits()
    {
        var s = await SetupAsync();
        await api.WithDbAsync(db => { db.LotGenealogy.Add(new LotGenealogy(s.LotId, s.OtherLotId, 4000, T0)); return db.SaveChangesAsync(); });

        var self = await Assert.ThrowsAsync<SqlException>(() => api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO LotGenealogy (ParentLotId, ChildLotId, Qty, CreatedAt) VALUES ({s.LotId}, {s.LotId}, 1, SYSUTCDATETIME())")));
        Assert.Contains("CK_LotGenealogy_NotSelf", self.Message);
        var edit = await Assert.ThrowsAsync<SqlException>(() => api.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync($"DELETE FROM LotGenealogy WHERE ParentLotId = {s.LotId}")));
        Assert.Contains("append-only", edit.Message);
    }

    // R1: the label code is unique.
    [Fact]
    public async Task Lot_numbers_are_unique()
    {
        var s = await SetupAsync();
        var lotNo = await api.WithDbAsync(db => db.MaterialLots.Where(l => l.Id == s.LotId).Select(l => l.LotNo).SingleAsync());

        await Assert.ThrowsAsync<ConflictException>(() => api.WithDbAsync(db =>
        {
            db.MaterialLots.Add(new MaterialLot(lotNo, s.ItemId, 1, null, T0, null, needsIqc: true));
            return db.SaveChangesAsync();
        }));
    }
}
