using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Application.Traceability;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Material;
using MiniMes.Contracts.Traceability;
using MiniMes.Domain.Material;
using MiniMes.Domain.Planning;
using MiniMes.Infrastructure.Persistence.Seed;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Traceability;

// Spec M6 step 8 over HTTP, the demo of spec v2 step 12: QC types L-CAP-B -> 80 serials grouped by location ->
// Hold lot -> loading L-CAP-B on a machine is refused (LotOnHold). Each test seeds its own tagged scenario.
[Collection(ApiCollection.Name)]
public class TraceApiTests(ApiFixture api)
{
    private async Task<TraceabilitySeeder.Scenario> SeedAsync()
    {
        using var scope = api.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TraceabilitySeeder>().SeedAsync(ApiFixture.Unique("A")[2..]);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Forward_from_a_lot_lists_the_serials_grouped_by_location()
    {
        var s = await SeedAsync();
        var qc = await api.LoginAsync("qc01");

        var result = await GetAsync<ForwardTraceResultDto>(qc, $"/api/trace/forward?lotNo={s.Code("L-CAP-B").ToLowerInvariant()}");

        Assert.Equal([s.Code("L-CAP-B")], result.StartLots);
        Assert.Equal([new LocationCountDto("CompletedUnpacked", 80)], result.ByLocation);
        Assert.Equal(80, result.Serials.Count);
        Assert.Equal(s.SerialNo(121), result.Serials[0].SerialNo);
        Assert.All(result.Serials, r => Assert.Equal((s.Code("L-CAP-B"), "L1-SMT-01", "EMP00123", (string?)null), (r.LotNo, r.StationCode, r.OperatorCode, r.CartonNo)));
    }

    [Fact]
    public async Task Forward_from_a_supplier_lot_needs_the_item_and_reaches_every_board()
    {
        var s = await SeedAsync();
        var qc = await api.LoginAsync("qc01");

        var result = await GetAsync<ForwardTraceResultDto>(qc, $"/api/trace/forward?supplierLot={s.Code("SUP-S1")}&itemCode=cap-100nf");
        var noItem = await qc.GetAsync($"/api/trace/forward?supplierLot={s.Code("SUP-S1")}");
        var both = await qc.GetAsync($"/api/trace/forward?lotNo={s.Code("L-CAP-A")}&supplierLot={s.Code("SUP-S1")}&itemCode=CAP-100NF");
        var none = await qc.GetAsync("/api/trace/forward");
        var unknown = await GetAsync<ForwardTraceResultDto>(qc, "/api/trace/forward?lotNo=L-NOPE");

        Assert.Equal([s.Code("L-CAP-A"), s.Code("L-CAP-B"), s.Code("L-CAP-P")], result.StartLots);
        Assert.Equal(200, result.Serials.Count);
        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest], new[] { noItem.StatusCode, both.StatusCode, none.StatusCode });
        Assert.Contains("item code", await noItem.Content.ReadAsStringAsync());
        Assert.Equal((0, 0), (unknown.Serials.Count, unknown.StartLots.Count));
    }

    [Fact]
    public async Task Excel_has_one_row_per_serial_and_the_packing_columns()
    {
        var s = await SeedAsync();
        var qc = await api.LoginAsync("qc01");

        var response = await qc.GetAsync($"/api/trace/forward.xlsx?lotNo={s.Code("L-CAP-B")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        using var wb = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var ws = wb.Worksheet("Serials");
        Assert.Equal(TraceWorkbook.Columns, Enumerable.Range(1, TraceWorkbook.Columns.Length).Select(c => ws.Cell(3, c).GetString()));
        Assert.Contains("Carton", TraceWorkbook.Columns);
        Assert.Contains("Shipment ref", TraceWorkbook.Columns);
        Assert.Equal(80, ws.RangeUsed()!.RowCount() - 3);
        Assert.Equal(s.SerialNo(121), ws.Cell(4, 1).GetString());
        // 20/10/2026 08:00 plant time (UTC+7) + 121 cycles of 30 s = 09:00:30.
        Assert.Equal(new DateTime(2026, 10, 20, 9, 0, 30), ws.Cell(4, 7).GetDateTime());
        Assert.Equal(80, (int)wb.Worksheet("By location").Cell(2, 2).GetDouble());
    }

    [Fact]
    public async Task Backward_flat_and_lazy_tree_give_the_same_history()
    {
        var s = await SeedAsync();
        var qc = await api.LoginAsync("qc01");

        var flat = await GetAsync<List<BackwardTraceDto>>(qc, $"/api/trace/backward?serialNo={s.SerialNo(150)}");
        var level1 = await GetAsync<List<TraceNodeDto>>(qc, $"/api/trace/nodes/serial/{s.SerialNo(150)}/children");
        var level2 = await GetAsync<List<TraceNodeDto>>(qc, $"/api/trace/nodes/lot/{s.Code("L-CAP-B")}/children");
        var level3 = await GetAsync<List<TraceNodeDto>>(qc, $"/api/trace/nodes/lot/{s.Code("L-CAP-P")}/children");
        var badKind = await qc.GetAsync($"/api/trace/nodes/pallet/{s.Code("L-CAP-P")}/children");

        Assert.Equal([s.Code("L-CAP-B"), s.Code("L-CAP-P"), s.Code("L-RES-A")], flat.Select(r => r.LotNo));
        Assert.Equal([(s.Code("L-CAP-B"), "CAP-100NF", true, "EMP00123"), (s.Code("L-RES-A"), "RES-100K", false, "EMP00123")],
            level1.Select(n => (n.Key, n.ItemCode, n.HasChildren, n.LoadedBy)));
        Assert.Equal([(s.Code("L-CAP-P"), (decimal?)2_000m, false)], level2.Select(n => (n.Key, n.Qty, n.HasChildren)));
        Assert.Empty(level3);
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
    }

    // The containment: the delivery SUP-S1 (L-CAP-A, L-CAP-P) and the split L-CAP-B, held at once; loading L-CAP-B
    // on a machine is then refused, with no override.
    [Fact]
    public async Task Hold_lot_holds_the_delivery_and_its_splits_and_loading_is_refused()
    {
        var s = await SeedAsync();
        var supervisor = await api.LoginAsync("sup01");
        var (stationId, op) = await StationNeedingCapAsync();

        var response = await supervisor.PostAsJsonAsync("/api/trace/hold-lot", new HoldLotRequest(s.Code("L-CAP-B")));
        var again = await (await supervisor.PostAsJsonAsync("/api/trace/hold-lot", new HoldLotRequest(s.Code("L-CAP-B"))))
            .Content.ReadFromJsonAsync<HoldLotResultDto>();
        var load = await (await op.PostAsJsonAsync($"/api/stations/{stationId}/material-loads", new LoadMaterialRequest(s.Code("L-CAP-B"), null, null, null)))
            .Content.ReadFromJsonAsync<LoadMaterialResponse>();
        var missing = await supervisor.PostAsJsonAsync("/api/trace/hold-lot", new HoldLotRequest("L-NOPE"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var held = (await response.Content.ReadFromJsonAsync<HoldLotResultDto>())!;
        Assert.Equal([s.Code("L-CAP-A"), s.Code("L-CAP-B"), s.Code("L-CAP-P")], held.Held);
        Assert.Empty(held.Skipped);
        Assert.Equal(3, again!.Skipped.Count(x => x.Status == "Hold"));
        Assert.Equal(MaterialLotStatus.Available, await api.WithDbAsync(db => db.MaterialLots.Where(l => l.Id == s.LotIds["L-RES-A"]).Select(l => l.Status).SingleAsync()));
        Assert.Equal(("Rejected", "LotOnHold"), (load!.Outcome, load.Code));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var audit = await api.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.TableName == "MaterialLots" && a.ColumnName == "Status"
            && a.NewValue == "Hold" && a.ChangedBy == "sup01" && s.LotIds.Values.Select(v => v.ToString()).Contains(a.RecordKey)));
        Assert.Equal(3, audit);
    }

    [Fact]
    public async Task Only_quality_roles_trace()
    {
        foreach (var user in new[] { "wh01", "planner01" })
            Assert.Equal(HttpStatusCode.Forbidden, (await (await api.LoginAsync(user)).GetAsync("/api/trace/forward?lotNo=L-X")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await api.LoginOperatorAsync()).GetAsync("/api/trace/backward?serialNo=X")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await api.LoginAsync("admin")).GetAsync("/api/trace/backward?serialNo=X")).StatusCode);
    }

    // A WO open at L1-SMT-01 whose snapshot uses CAP-100NF at SMT, so a CAP reel is a valid candidate there.
    private async Task<(int StationId, HttpClient Operator)> StationNeedingCapAsync()
    {
        var ids = await api.WithDbAsync(async db =>
        {
            var station = await db.Stations.SingleAsync(x => x.Code == "L1-SMT-01");
            var smt = await db.WorkCenters.SingleAsync(w => w.Code == "WC-SMT");
            var pcbA = await db.Items.SingleAsync(i => i.Code == "PCB-A");
            var cap = await db.Items.SingleAsync(i => i.Code == "CAP-100NF");
            var at = new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc);
            var wo = WorkOrder.Create(ApiFixture.Unique("WO-HOLD"), pcbA.Id, station.LineId, 10, at, at.AddHours(8), requireFai: false);
            wo.Release(1, 1, [new MaterialSnapshot(cap.Id, 10, 1m)], [new RoutingStepSnapshot(10, "SMT", smt.Id, 20m, false, true, null)], at);
            db.WorkOrders.Add(wo);
            await db.SaveChangesAsync();
            return (station.Id, wo.Id);
        });
        var op = await api.LoginOperatorAsync(stationCode: "L1-SMT-01");
        (await op.PostAsJsonAsync($"/api/stations/{ids.Item1}/sessions", new OpenSessionRequest(ids.Item2))).EnsureSuccessStatusCode();
        return (ids.Item1, op);
    }
}
