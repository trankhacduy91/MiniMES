using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Domain.Planning;
using MiniMes.Infrastructure.Persistence.Seed;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Traceability;

// Spec M6 step 6: the standard trace data, counted in SQL (200 serials, 400 SerialComponent rows, 1 LotGenealogy row).
[Collection(ApiCollection.Name)]
public class TraceabilitySeedTests(ApiFixture api)
{
    private async Task<TraceabilitySeeder.Scenario> SeedAsync(string tag)
    {
        using var scope = api.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TraceabilitySeeder>().SeedAsync(tag);
    }

    private sealed record Counts(int Serials, int Components, int Genealogy, int ScanLogs);

    // Raw SQL on purpose: the check of the spec, independent of the EF model.
    private Task<Counts> CountAsync(TraceabilitySeeder.Scenario s) => api.WithDbAsync(async db =>
    {
        var lotIds = string.Join(",", s.LotIds.Values);
        Task<int> Scalar(string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();
        return new Counts(
            await Scalar($"SELECT COUNT(*) AS Value FROM Serials WHERE WorkOrderId = {s.WorkOrderId}"),
            await Scalar($"SELECT COUNT(*) AS Value FROM SerialComponents c JOIN Serials s ON s.Id = c.SerialId WHERE s.WorkOrderId = {s.WorkOrderId}"),
            await Scalar($"SELECT COUNT(*) AS Value FROM LotGenealogy WHERE ParentLotId IN ({lotIds}) OR ChildLotId IN ({lotIds})"),
            await Scalar($"SELECT COUNT(*) AS Value FROM ScanLogs WHERE WorkOrderId = {s.WorkOrderId}"));
    });

    [Fact]
    public async Task Scenario_has_the_rows_of_the_test_oracle()
    {
        var s = await SeedAsync(ApiFixture.Unique("T")[2..]);

        Assert.Equal(new Counts(200, 400, 1, 200), await CountAsync(s));
        var perLot = await api.WithDbAsync(db => db.SerialComponents.Where(c => s.LotIds.Values.Contains(c.LotId))
            .GroupBy(c => c.LotId).Select(g => new { g.Key, Count = g.Count(), Serials = g.Select(c => c.SerialId).Distinct().Count() })
            .ToDictionaryAsync(g => g.Key, g => (g.Count, g.Serials)));
        Assert.Equal((120, 120), perLot[s.LotIds["L-CAP-A"]]);
        Assert.Equal((80, 80), perLot[s.LotIds["L-CAP-B"]]);
        Assert.Equal((200, 200), perLot[s.LotIds["L-RES-A"]]);
        Assert.False(perLot.ContainsKey(s.LotIds["L-CAP-P"]));   // the split reel was never on a machine

        // Board 120 is the last on L-CAP-A, 121 the first on L-CAP-B (the boundary step 7 tests rely on).
        var capOf = await api.WithDbAsync(db => (from c in db.SerialComponents
                                                 join sr in db.Serials on c.SerialId equals sr.Id
                                                 join l in db.MaterialLots on c.LotId equals l.Id
                                                 where sr.SerialNo == s.SerialNo(120) || sr.SerialNo == s.SerialNo(121)
                                                 where l.LotNo.StartsWith("L-CAP")
                                                 orderby sr.SerialNo
                                                 select l.LotNo).ToListAsync());
        Assert.Equal([s.Code("L-CAP-A"), s.Code("L-CAP-B")], capOf);

        var edge = await api.WithDbAsync(db => db.LotGenealogy.SingleAsync(g => g.ChildLotId == s.LotIds["L-CAP-B"]));
        Assert.Equal((s.LotIds["L-CAP-P"], 2_000m), (edge.ParentLotId, edge.Qty));

        var lots = await api.WithDbAsync(db => db.MaterialLots.Where(l => s.LotIds.Values.Contains(l.Id))
            .ToDictionaryAsync(l => l.LotNo, l => (l.SupplierLot, l.RemainingQty)));
        Assert.Equal((s.Code("SUP-S1"), 4_880m), lots[s.Code("L-CAP-A")]);
        Assert.Equal((s.Code("SUP-S1"), 8_000m), lots[s.Code("L-CAP-P")]);
        Assert.Equal((s.Code("SUP-S1"), 1_920m), lots[s.Code("L-CAP-B")]);
        Assert.Equal((s.Code("SUP-R1"), 9_800m), lots[s.Code("L-RES-A")]);

        // History only: the order is closed and no reel is left on the station.
        Assert.Equal(WorkOrderStatus.Closed, await api.WithDbAsync(db => db.WorkOrders.Where(w => w.Id == s.WorkOrderId).Select(w => w.Status).SingleAsync()));
        Assert.Equal(0, await api.WithDbAsync(db => db.MaterialLoads.CountAsync(l => s.LotIds.Values.Contains(l.LotId) && l.UnloadedAt == null)));
        Assert.Equal(3, await api.WithDbAsync(db => db.MaterialLoads.CountAsync(l => s.LotIds.Values.Contains(l.LotId))));
        // Every component points at the scan of its own board.
        Assert.Equal(0, await api.WithDbAsync(db => (from c in db.SerialComponents
                                                     join l in db.ScanLogs on c.ScanLogId equals l.Id
                                                     where s.LotIds.Values.Contains(c.LotId) && l.SerialId != c.SerialId
                                                     select c).CountAsync()));
    }

    // The trace tables are insert-only, so "build it again" = a new tag; the same tag is found, not duplicated.
    [Fact]
    public async Task Seeding_again_returns_the_same_scenario_and_another_tag_is_independent()
    {
        var tag = ApiFixture.Unique("T")[2..];
        var first = await SeedAsync(tag);
        var again = await SeedAsync(tag);
        var other = await SeedAsync(ApiFixture.Unique("T")[2..]);

        Assert.Equal(first.WorkOrderId, again.WorkOrderId);
        Assert.Equal(first.LotIds, again.LotIds);
        Assert.Equal(new Counts(200, 400, 1, 200), await CountAsync(first));
        Assert.NotEqual(first.WorkOrderId, other.WorkOrderId);
        Assert.Empty(first.LotIds.Values.Intersect(other.LotIds.Values));
        Assert.Equal(new Counts(200, 400, 1, 200), await CountAsync(other));
    }
}
