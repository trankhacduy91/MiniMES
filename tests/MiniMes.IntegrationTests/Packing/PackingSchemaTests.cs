using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Common;
using MiniMes.Domain.Packing;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Packing;

// Spec M3b step 1 check: the rules the database itself enforces (P3, P4), the sequences and the seeded spec.
// Each test has its own WO and serials, so cartons of other tests on L1-PACK-01 never collide with its own.
[Collection(ApiCollection.Name)]
public class PackingSchemaTests(ApiFixture api)
{
    private static readonly DateTime T0 = new(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc);
    private static int _nextSerial;

    private sealed record Setup(int WorkOrderId, int ItemId, int StationId, long[] SerialIds);

    private Task<Setup> SetupAsync(int serials = 2) => api.WithDbAsync(async db =>
    {
        var station = await db.Stations.SingleAsync(s => s.Code == "L1-PACK-01");
        var pcbA = await db.Items.SingleAsync(i => i.Code == "PCB-A");
        var wo = WorkOrder.Create(ApiFixture.Unique("WO-PK"), pcbA.Id, station.LineId, 10, T0, T0.AddHours(8), requireFai: false);
        db.WorkOrders.Add(wo);
        await db.SaveChangesAsync();
        var rows = Enumerable.Range(0, serials)
            .Select(_ => new Serial(SerialFormat.Build("PKST", new DateOnly(2026, 11, 3), Interlocked.Increment(ref _nextSerial)), wo.Id, pcbA.Id))
            .ToList();
        db.Serials.AddRange(rows);
        await db.SaveChangesAsync();
        return new Setup(wo.Id, pcbA.Id, station.Id, rows.Select(r => r.Id).ToArray());
    });

    private static string CartonNo() => ApiFixture.Unique("CT")[..11];

    private Task<int> CartonAsync(Setup s) => api.WithDbAsync(async db =>
    {
        var carton = new Carton(CartonNo(), s.WorkOrderId, s.ItemId, s.StationId, T0);
        db.Cartons.Add(carton);
        await db.SaveChangesAsync();
        return carton.Id;
    });

    // P3: a serial in two live cartons is refused; once the first row is unpacked (kept as history) it may go again.
    [Fact]
    public async Task A_serial_is_in_one_live_carton_and_keeps_its_history()
    {
        var s = await SetupAsync();
        var first = await CartonAsync(s);
        await api.WithDbAsync(db => { db.CartonSerials.Add(new CartonSerial(first, s.SerialIds[0], "EMP00123", T0)); return db.SaveChangesAsync(); });
        await api.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE dbo.Cartons SET Status = 'Closed' WHERE Id = {first}"));
        var second = await CartonAsync(s);

        await Assert.ThrowsAsync<ConflictException>(() => api.WithDbAsync(db =>
        {
            db.CartonSerials.Add(new CartonSerial(second, s.SerialIds[0], "EMP00123", T0.AddMinutes(1)));
            return db.SaveChangesAsync();
        }));

        await api.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE dbo.CartonSerials SET UnpackedAt = {T0.AddMinutes(2)}, UnpackedBy = 'sup01', UnpackReason = 'Test' WHERE CartonId = {first}"));
        await api.WithDbAsync(db => { db.CartonSerials.Add(new CartonSerial(second, s.SerialIds[0], "EMP00123", T0.AddMinutes(3))); return db.SaveChangesAsync(); });

        var history = await api.WithDbAsync(db => db.CartonSerials.Where(c => c.SerialId == s.SerialIds[0])
            .OrderBy(c => c.Id).Select(c => new { c.CartonId, c.UnpackedAt }).ToListAsync());
        Assert.Equal([(first, true), (second, false)], history.Select(h => (h.CartonId, h.UnpackedAt != null)));
    }

    // P4: one Open carton per (station, WO); a closed one does not count.
    [Fact]
    public async Task One_open_carton_per_station_and_work_order()
    {
        var s = await SetupAsync();
        var first = await CartonAsync(s);

        await Assert.ThrowsAsync<ConflictException>(() => CartonAsync(s));

        await api.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE dbo.Cartons SET Status = 'Closed' WHERE Id = {first}"));
        await CartonAsync(s);
        Assert.Equal(2, await api.WithDbAsync(db => db.Cartons.CountAsync(c => c.WorkOrderId == s.WorkOrderId)));
    }

    // Same rule for pallets: one Open pallet per (station, item). A test item keeps it apart from other tests.
    [Fact]
    public async Task One_open_pallet_per_station_and_item()
    {
        var (itemId, stationId) = await api.WithDbAsync(async db =>
        {
            var item = Domain.MasterData.Items.Item.Create(ApiFixture.Unique("FG-T"), "Test board", Domain.MasterData.Items.ItemType.Finished,
                Domain.MasterData.Items.Units.Pieces, true, false);
            db.Items.Add(item);
            await db.SaveChangesAsync();
            return (item.Id, await db.Stations.Where(st => st.Code == "L1-PACK-01").Select(st => st.Id).SingleAsync());
        });
        Task AddPallet() => api.WithDbAsync(db => { db.Pallets.Add(new Pallet(ApiFixture.Unique("PT")[..11], itemId, stationId, T0)); return db.SaveChangesAsync(); });

        await AddPallet();

        await Assert.ThrowsAsync<ConflictException>(AddPallet);
    }

    [Theory]
    [InlineData("UPDATE dbo.Cartons SET Qty = -1 WHERE Id = {0}", "CK_Cartons_Qty")]
    [InlineData("UPDATE dbo.PackingSpecs SET QtyPerCarton = 0 WHERE ItemId = {1}", "CK_PackingSpecs_Positive")]
    public async Task Database_refuses_negative_counts(string sqlFormat, string constraint)
    {
        var s = await SetupAsync(0);
        var carton = await CartonAsync(s);
        var sql = string.Format(sqlFormat, carton, s.ItemId);

        var ex = await Assert.ThrowsAsync<SqlException>(() => api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql)));

        Assert.Contains(constraint, ex.Message);
    }

    // Seeded with the master data (PCB-A 10 / 20); the sequences give increasing numbers.
    [Fact]
    public async Task Pcb_a_has_its_packing_spec_and_the_sequences_count()
    {
        var spec = await api.WithDbAsync(db => (from p in db.PackingSpecs join i in db.Items on p.ItemId equals i.Id
                                                where i.Code == "PCB-A" select p).SingleAsync());
        var numbers = await api.WithDbAsync(async db => new[]
        {
            (await db.Database.SqlQueryRaw<long>("SELECT NEXT VALUE FOR dbo.CartonSeq AS [Value]").ToListAsync())[0],
            (await db.Database.SqlQueryRaw<long>("SELECT NEXT VALUE FOR dbo.CartonSeq AS [Value]").ToListAsync())[0],
            (await db.Database.SqlQueryRaw<long>("SELECT NEXT VALUE FOR dbo.PalletSeq AS [Value]").ToListAsync())[0],
        });

        Assert.Equal((10, 20), (spec.QtyPerCarton, spec.CartonsPerPallet));
        Assert.True(numbers[1] > numbers[0]);
        Assert.True(numbers[2] >= 1);
    }

    // PackingSpec is master data: a change is in the audit trail (M0), one row per column.
    [Fact]
    public async Task Changing_a_packing_spec_is_audited()
    {
        var itemId = await api.WithDbAsync(async db =>
        {
            var item = Domain.MasterData.Items.Item.Create(ApiFixture.Unique("FG-A"), "Audited board", Domain.MasterData.Items.ItemType.Finished,
                Domain.MasterData.Items.Units.Pieces, true, false);
            db.Items.Add(item);
            await db.SaveChangesAsync();
            db.PackingSpecs.Add(new PackingSpec(item.Id, 10, 20));
            await db.SaveChangesAsync();
            return item.Id;
        });

        await api.WithDbAsync(async db =>
        {
            (await db.PackingSpecs.SingleAsync(p => p.ItemId == itemId)).Change(12, 20);
            return await db.SaveChangesAsync();
        });

        var rows = await api.WithDbAsync(db => db.AuditLogs.Where(a => a.TableName == "PackingSpecs" && a.RecordKey == itemId.ToString())
            .Select(a => new { a.ColumnName, a.OldValue, a.NewValue }).ToListAsync());
        Assert.Contains(rows, r => r.ColumnName == "*");   // Added
        Assert.Contains(rows, r => (r.ColumnName, r.OldValue, r.NewValue) == ("QtyPerCarton", "10", "12"));
        Assert.DoesNotContain(rows, r => r.ColumnName == "CartonsPerPallet");
    }
}
