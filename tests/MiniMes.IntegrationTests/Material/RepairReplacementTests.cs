using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MiniMes.Application.Material;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Traceability;
using MiniMes.Domain.Material;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Execution;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Material;

// Spec M6 step 10b: a part replaced at Repair is recorded as the board's material (SerialComponent with RepairLogId),
// checked like a reel at loading, subtracted from its lot, and found by both traces. The board: a 1-unit WO of PCB-A
// that passed SMT and ASSY and failed at TEST, checked in at L1-REPAIR. Material is enforced for each test here and
// turned off after (the shared host runs M3-M5 tests without reels).
[Collection(ApiCollection.Name)]
public class RepairReplacementTests : IDisposable
{
    private static readonly DateTime Received = new(2026, 9, 15, 0, 30, 0, DateTimeKind.Utc);

    private readonly ApiFixture api;
    private readonly ScanTests scan;
    private readonly MaterialOptions options;

    public RepairReplacementTests(ApiFixture api)
    {
        this.api = api;
        scan = new ScanTests(api);
        options = api.Factory.Services.GetRequiredService<IOptions<MaterialOptions>>().Value;
    }

    public void Dispose()
    {
        options.EnforceLoadedMaterial = false;
        GC.SuppressFinalize(this);
    }

    private sealed record Board(int WoId, long SerialId, string SerialNo, int ItemId, HttpClient Technician);

    // The scans run with material off (no reels loaded on L1); enforcement starts at the repair.
    private async Task<Board> CheckedInAtRepairAsync()
    {
        var (wo, serials) = await scan.ReleasedAsync(1);
        var op = await api.LoginOperatorAsync();
        var board = serials[0];
        foreach (var station in new[] { "L1-SMT-01", "L1-ASM-01", "L1-TEST-01" })
        {
            await scan.OpenSessionAsync(station, wo.Id);
            var ng = station == "L1-TEST-01";
            await scan.ScanAsync(op, station, board, ng ? "NG" : "OK", ng ? "SLD-BRG" : null);
        }
        await scan.OpenSessionAsync("L1-REPAIR", wo.Id);
        Assert.Equal("Ok", (await scan.ScanAsync(op, "L1-REPAIR", board)).Result);
        options.EnforceLoadedMaterial = true;

        var (serialId, itemId) = await api.WithDbAsync(async db =>
        {
            var id = await db.Serials.Where(s => s.SerialNo == board).Select(s => s.Id).SingleAsync();
            // A lot-controlled material of the WO's snapshot (PCB-A's BOM has several).
            var item = await (from m in db.WorkOrderMaterials
                              join i in db.Items on m.ComponentItemId equals i.Id
                              where m.WorkOrderId == wo.Id && i.IsLotControlled
                              orderby m.OperationSeq, i.Code
                              select i.Id).FirstAsync();
            return (id, item);
        });
        return new Board(wo.Id, serialId, board, itemId, op);
    }

    private Task<MaterialLot> LotAsync(int itemId, Action<MaterialLot>? change = null, bool needsIqc = false, DateTime? expiry = null)
        => api.WithDbAsync(async db =>
        {
            var lot = new MaterialLot(ApiFixture.Unique("L-REP"), itemId, 1_000, "SUP-REP", Received, expiry, needsIqc);
            change?.Invoke(lot);
            db.MaterialLots.Add(lot);
            await db.SaveChangesAsync();
            return lot;
        });

    private async Task<HttpResponseMessage> RepairAsync(Board b, string? lotNo, string action = "Replace U3")
        => await b.Technician.PostAsJsonAsync($"/api/stations/{await scan.StationIdAsync("L1-REPAIR")}/repair",
            new RepairRequest(b.SerialNo, "SLD-BRG", action, "U3", null, lotNo));

    // Repair rows only: with material off the SMT scan still records reels other tests left loaded on L1-SMT-01.
    private Task<List<SerialComponent>> ComponentsAsync(Board b)
        => api.WithDbAsync(db => db.SerialComponents.Where(c => c.SerialId == b.SerialId && c.RepairLogId != null).ToListAsync());

    private Task<decimal> RemainingAsync(int lotId)
        => api.WithDbAsync(db => db.MaterialLots.Where(l => l.Id == lotId).Select(l => l.RemainingQty).SingleAsync());

    [Fact]
    public async Task Replaced_part_is_recorded_subtracted_and_found_by_both_traces()
    {
        var b = await CheckedInAtRepairAsync();
        var lot = await LotAsync(b.ItemId);

        var response = await RepairAsync(b, $" {lot.LotNo.ToLowerInvariant()} ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var repair = await api.WithDbAsync(db => db.RepairLogs.SingleAsync(r => r.SerialId == b.SerialId));
        var repairStation = await scan.StationIdAsync("L1-REPAIR");
        var row = Assert.Single(await ComponentsAsync(b));
        Assert.Equal((lot.Id, b.ItemId, 1m, repairStation, (long?)null, (long?)repair.Id, repair.RepairedAt),
            (row.LotId, row.ItemId, row.Qty, row.StationId, row.ScanLogId, row.RepairLogId, row.ConsumedAt));
        Assert.Equal(999m, await RemainingAsync(lot.Id));

        var qc = await api.LoginAsync("qc01");
        var backward = await qc.GetFromJsonAsync<List<BackwardTraceDto>>($"/api/trace/backward?serialNo={b.SerialNo}");
        var part = Assert.Single(backward!, r => r.FromRepair);
        Assert.Equal((lot.LotNo, "L1-REPAIR", "EMP00123", (string?)null, true),
            (part.LotNo, part.StationCode, part.OperatorCode, part.LoadedBy, part.FromRepair));
        var nodes = (await qc.GetFromJsonAsync<List<TraceNodeDto>>($"/api/trace/nodes/serial/{b.SerialNo}/children"))!;
        Assert.Equal(lot.LotNo, Assert.Single(nodes, n => n.FromRepair).Key);

        var forward = await qc.GetFromJsonAsync<ForwardTraceResultDto>($"/api/trace/forward?lotNo={lot.LotNo}");
        var hit = Assert.Single(forward!.Serials);
        Assert.Equal((b.SerialNo, "L1-REPAIR", "EMP00123"), (hit.SerialNo, hit.StationCode, hit.OperatorCode));
    }

    // While material is enforced a "Replace" must say which lot the new part came from (400 on the field);
    // a repair that replaces nothing needs no lot.
    [Fact]
    public async Task Replace_without_a_lot_is_refused_while_material_is_enforced()
    {
        var b = await CheckedInAtRepairAsync();

        var noLot = await RepairAsync(b, null);
        var reflow = await RepairAsync(b, null, action: "Reflow solder joint U3");

        Assert.Equal(HttpStatusCode.BadRequest, noLot.StatusCode);
        Assert.Contains("\"ReplacementLotNo\"", await noLot.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, reflow.StatusCode);
        Assert.Empty(await ComponentsAsync(b));
    }

    // Quality checks of a load apply (R2-R4, R15): each refusal leaves the board in Repair with nothing written, and
    // the right lot afterwards goes through.
    [Fact]
    public async Task Wrong_lots_are_refused_and_nothing_is_written()
    {
        var b = await CheckedInAtRepairAsync();
        var otherItem = await api.WithDbAsync(db => db.Items.Where(i => i.Code == "PCB-A").Select(i => i.Id).SingleAsync());
        var notInBom = await LotAsync(otherItem);
        var onHold = await LotAsync(b.ItemId, l => l.PutOnHold());
        var pendingIqc = await LotAsync(b.ItemId, needsIqc: true);
        var expired = await LotAsync(b.ItemId, expiry: DateTime.UtcNow.AddMinutes(-1));

        var unknown = await RepairAsync(b, "L-NO-SUCH-LOT");
        var refused = new List<(string LotNo, HttpResponseMessage Response)>();
        foreach (var lot in new[] { notInBom, onHold, pendingIqc, expired }) refused.Add((lot.LotNo, await RepairAsync(b, lot.LotNo)));

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("\"ReplacementLotNo\"", await unknown.Content.ReadAsStringAsync());
        Assert.All(refused, r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Response.StatusCode));
        Assert.Contains("not a material of this work order", await refused[0].Response.Content.ReadAsStringAsync());
        Assert.Contains("on hold", await refused[1].Response.Content.ReadAsStringAsync());
        Assert.Contains("incoming inspection", await refused[2].Response.Content.ReadAsStringAsync());
        Assert.Contains("expired", await refused[3].Response.Content.ReadAsStringAsync());
        Assert.Empty(await ComponentsAsync(b));
        Assert.False(await api.WithDbAsync(db => db.RepairLogs.AnyAsync(r => r.SerialId == b.SerialId)));
        Assert.Equal(SerialStatus.Repair, await api.WithDbAsync(db => db.Serials.Where(s => s.Id == b.SerialId).Select(s => s.Status).SingleAsync()));
        foreach (var lot in new[] { onHold, pendingIqc, expired }) Assert.Equal(1_000m, await RemainingAsync(lot.Id));

        var good = await LotAsync(b.ItemId);
        Assert.Equal(HttpStatusCode.OK, (await RepairAsync(b, good.LotNo)).StatusCode);
    }

    // The same reel used twice on one board (U3 replaced, fails again, replaced again): one row already proves the
    // link (R9), the quantity goes down twice, both repairs are logged.
    [Fact]
    public async Task Same_lot_twice_on_one_board_keeps_one_row()
    {
        var b = await CheckedInAtRepairAsync();
        var lot = await LotAsync(b.ItemId);
        Assert.Equal(HttpStatusCode.OK, (await RepairAsync(b, lot.LotNo)).StatusCode);

        options.EnforceLoadedMaterial = false;   // TEST consumes nothing, but keep the scans independent of reels
        await Task.Delay(TimeSpan.FromSeconds(5.5));   // out of the duplicate window, like a real retest
        Assert.Equal("Ng", (await scan.ScanAsync(b.Technician, "L1-TEST-01", b.SerialNo, "NG", "SLD-BRG")).Result);
        Assert.Equal("Ok", (await scan.ScanAsync(b.Technician, "L1-REPAIR", b.SerialNo)).Result);
        options.EnforceLoadedMaterial = true;
        Assert.Equal(HttpStatusCode.OK, (await RepairAsync(b, lot.LotNo)).StatusCode);

        var row = Assert.Single(await ComponentsAsync(b));
        var repairs = await api.WithDbAsync(db => db.RepairLogs.Where(r => r.SerialId == b.SerialId).OrderBy(r => r.Id).Select(r => r.Id).ToListAsync());
        Assert.Equal(2, repairs.Count);
        Assert.Equal(repairs[0], row.RepairLogId);
        Assert.Equal(998m, await RemainingAsync(lot.Id));
    }

    // CK_SerialComponents_OneSource: a row comes from a scan or from a repair, never both, never neither.
    [Theory]
    [InlineData("NULL", "NULL")]
    [InlineData("1", "1")]
    public async Task A_component_has_exactly_one_source(string scanLogId, string repairLogId)
    {
        var lot = await LotAsync(await api.WithDbAsync(db => db.Items.Where(i => i.IsLotControlled).Select(i => i.Id).FirstAsync()));
        var sql = "INSERT INTO dbo.SerialComponents (SerialId, LotId, ItemId, Qty, StationId, ScanLogId, RepairLogId, ConsumedAt) "
                  + $"VALUES (900000002, {lot.Id}, {lot.ItemId}, 1, 1, {scanLogId}, {repairLogId}, SYSUTCDATETIME())";

        var ex = await Assert.ThrowsAsync<SqlException>(() => api.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql)));

        Assert.Contains("CK_SerialComponents_OneSource", ex.Message);
    }
}
