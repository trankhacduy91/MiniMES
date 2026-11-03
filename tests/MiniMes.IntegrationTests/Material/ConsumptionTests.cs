using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MiniMes.Application.Material;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Material;
using MiniMes.Domain.Material;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Execution;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Material;

// Spec M6 step 5, checks (a)-(g): the scan gate and consumption at the SMT station of L1, for a WO whose snapshot
// consumes a capacitor (4 per board) and a resistor (2 per board) at SMT (seq 10) and an MCU at ASSY (seq 20).
// Each test has its own items and boards. The shared host runs with the gate off (M3-M5 tests load no reels):
// this class turns it on for each of its tests and back off after (tests of one collection never run together).
[Collection(ApiCollection.Name)]
public class ConsumptionTests : IDisposable
{
    private static readonly DateTime Received = new(2026, 9, 15, 0, 30, 0, DateTimeKind.Utc);
    private static int _nextSerial;

    private readonly ApiFixture api;
    private readonly ScanTests scan;
    private readonly MaterialOptions options;

    public ConsumptionTests(ApiFixture api)
    {
        this.api = api;
        scan = new ScanTests(api);
        options = api.Factory.Services.GetRequiredService<IOptions<MaterialOptions>>().Value;
        options.EnforceLoadedMaterial = true;
    }

    public void Dispose()
    {
        options.EnforceLoadedMaterial = false;
        api.Factory.ConsumptionFault.FailForSerialNo = null;
        GC.SuppressFinalize(this);
    }

    private sealed record Setup(int WoId, int CapId, int ResId, int McuId, string ResCode, List<string> Boards, HttpClient Operator);

    private async Task<Setup> SetupAsync(int boards = 10, decimal capPerBoard = 4m)
    {
        var ids = await api.WithDbAsync(async db =>
        {
            Item NewItem(string prefix) => Item.Create(ApiFixture.Unique(prefix), prefix, ItemType.Raw, Units.Pieces, false, true);
            var (cap, res, mcu) = (NewItem("CAP"), NewItem("RES"), NewItem("MCU"));
            db.Items.AddRange(cap, res, mcu);
            await db.SaveChangesAsync();
            var line = await db.Stations.Where(s => s.Code == "L1-SMT-01").Select(s => s.LineId).SingleAsync();
            var smt = await db.WorkCenters.SingleAsync(w => w.Code == "WC-SMT");
            var assy = await db.WorkCenters.SingleAsync(w => w.Code == "WC-ASSY");
            var pcbA = await db.Items.SingleAsync(i => i.Code == "PCB-A");
            var wo = WorkOrder.Create(ApiFixture.Unique("WO-CONS"), pcbA.Id, line, 50, Received, Received.AddHours(8), requireFai: false);
            wo.Release(1, 1, [new MaterialSnapshot(cap.Id, 10, capPerBoard), new MaterialSnapshot(res.Id, 10, 2m), new MaterialSnapshot(mcu.Id, 20, 1m)],
                [new RoutingStepSnapshot(10, "SMT", smt.Id, 20m, false, true, null), new RoutingStepSnapshot(20, "ASSY", assy.Id, 60m, false, true, null)],
                Received);
            db.WorkOrders.Add(wo);
            await db.SaveChangesAsync();
            // Valid check digits (the scan refuses others before reading the table); model CONS never clashes with PCBA.
            var serials = Enumerable.Range(0, boards)
                .Select(_ => SerialFormat.Build("CONS", new DateOnly(2026, 11, 2), Interlocked.Increment(ref _nextSerial))).ToList();
            db.Serials.AddRange(serials.Select(sn => new Serial(sn, wo.Id, pcbA.Id)));
            await db.SaveChangesAsync();
            return (wo.Id, cap.Id, res.Id, mcu.Id, res.Code, serials);
        });
        await scan.OpenSessionAsync("L1-SMT-01", ids.Item1);
        return new Setup(ids.Item1, ids.Item2, ids.Item3, ids.Item4, ids.Item5, ids.Item6, await api.LoginOperatorAsync(stationCode: "L1-SMT-01"));
    }

    // A reel received and loaded at L1-SMT-01 through the API (step 4).
    private async Task<int> LoadedLotAsync(Setup s, int itemId, decimal qty = 1_000)
    {
        var lot = await api.WithDbAsync(async db =>
        {
            var lot = new MaterialLot(ApiFixture.Unique("L"), itemId, qty, "SUP-T", Received, null, false);
            db.MaterialLots.Add(lot);
            await db.SaveChangesAsync();
            return lot;
        });
        var stationId = await scan.StationIdAsync("L1-SMT-01");
        var response = await s.Operator.PostAsJsonAsync($"/api/stations/{stationId}/material-loads", new LoadMaterialRequest(lot.LotNo, null, null, null));
        response.EnsureSuccessStatusCode();
        Assert.Equal("Accepted", (await response.Content.ReadFromJsonAsync<LoadMaterialResponse>())!.Outcome);
        return lot.Id;
    }

    private Task<ScanResponse> ScanAsync(Setup s, string board) => scan.ScanAsync(s.Operator, "L1-SMT-01", board);

    private Task<(decimal Remaining, MaterialLotStatus Status)> LotAsync(int lotId) => api.WithDbAsync(async db =>
        await db.MaterialLots.Where(l => l.Id == lotId).Select(l => new ValueTuple<decimal, MaterialLotStatus>(l.RemainingQty, l.Status)).SingleAsync());

    private Task<List<SerialComponent>> ComponentsAsync(Setup s) => api.WithDbAsync(db =>
    {
        var items = new[] { s.CapId, s.ResId, s.McuId };
        return db.SerialComponents.Where(c => items.Contains(c.ItemId)).ToListAsync();
    });

    private Task<int> ScanLogsAsync(string board) => api.WithDbAsync(db => db.ScanLogs.CountAsync(l => l.SerialNo == board));

    // (a) Ten boards: one row per board and consumed item, linked to the scan; the lots go down by 10 x QtyPer.
    [Fact]
    public async Task Each_scan_ok_records_the_loaded_lots_and_subtracts_them()
    {
        var s = await SetupAsync();
        var (cap, res) = (await LoadedLotAsync(s, s.CapId), await LoadedLotAsync(s, s.ResId));

        foreach (var board in s.Boards) Assert.Equal("Ok", (await ScanAsync(s, board)).Result);

        var rows = await ComponentsAsync(s);
        Assert.Equal(20, rows.Count);
        Assert.Equal(10, rows.Count(r => (r.ItemId, r.LotId, r.Qty) == (s.CapId, cap, 4m)));
        Assert.Equal(10, rows.Count(r => (r.ItemId, r.LotId, r.Qty) == (s.ResId, res, 2m)));
        Assert.DoesNotContain(rows, r => r.ItemId == s.McuId);   // consumed at ASSY, not here
        Assert.Equal(10, rows.Select(r => r.SerialId).Distinct().Count());
        var scanIds = await api.WithDbAsync(db => db.ScanLogs.Where(l => s.Boards.Contains(l.SerialNo)).Select(l => l.Id).ToListAsync());
        Assert.All(rows, r => Assert.Contains(r.ScanLogId!.Value, scanIds));
        Assert.Equal((960m, MaterialLotStatus.Available), await LotAsync(cap));
        Assert.Equal((980m, MaterialLotStatus.Available), await LotAsync(res));
    }

    // (b) R8: the resistor is not on the station -> blocked, nothing written, the capacitor not touched.
    [Fact]
    public async Task Scan_is_blocked_when_a_consumed_item_is_not_loaded()
    {
        var s = await SetupAsync(1);
        var cap = await LoadedLotAsync(s, s.CapId);

        var answer = await ScanAsync(s, s.Boards[0]);

        Assert.Equal(("Blocked", "MaterialNotLoaded"), (answer.Result, answer.ReasonCode));
        Assert.Contains($"{s.ResCode} not loaded", answer.Message);
        Assert.Equal(0, await ScanLogsAsync(s.Boards[0]));
        Assert.Empty(await ComponentsAsync(s));
        Assert.Equal((1_000m, MaterialLotStatus.Available), await LotAsync(cap));
        Assert.Equal(SerialStatus.Created, await api.WithDbAsync(db => db.Serials.Where(x => x.SerialNo == s.Boards[0]).Select(x => x.Status).SingleAsync()));
    }

    // (c) R9: the double trigger is answered "duplicate" and consumes nothing more.
    [Fact]
    public async Task Duplicate_scan_does_not_consume_twice()
    {
        var s = await SetupAsync(1);
        var (cap, _) = (await LoadedLotAsync(s, s.CapId), await LoadedLotAsync(s, s.ResId));

        var first = await ScanAsync(s, s.Boards[0]);
        var second = await ScanAsync(s, s.Boards[0]);

        Assert.Equal((false, true), (first.Duplicate, second.Duplicate));
        Assert.Equal(2, (await ComponentsAsync(s)).Count);
        Assert.Equal(996m, (await LotAsync(cap)).Remaining);
    }

    // Rework back to SMT (repair ReturnToSeq): the board passes SMT twice, its parts were placed once.
    [Fact]
    public async Task Rework_through_the_same_station_does_not_consume_twice()
    {
        var s = await SetupAsync(1);
        var (cap, _) = (await LoadedLotAsync(s, s.CapId), await LoadedLotAsync(s, s.ResId));
        var board = s.Boards[0];
        await ScanAsync(s, board);
        await scan.OpenSessionAsync("L1-ASM-01", s.WoId);
        Assert.Equal("Ng", (await scan.ScanAsync(s.Operator, "L1-ASM-01", board, "NG", "SLD-BRG")).Result);   // NG: no gate, no MCU needed
        await scan.OpenSessionAsync("L1-REPAIR", s.WoId);
        await scan.ScanAsync(s.Operator, "L1-REPAIR", board);
        var repaired = await s.Operator.PostAsJsonAsync($"/api/stations/{await scan.StationIdAsync("L1-REPAIR")}/repair",
            new RepairRequest(board, "SLD-BRG", "Reflow", ReturnToSeq: 10));
        Assert.Equal(HttpStatusCode.OK, repaired.StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(5.5));   // out of the duplicate window, like a real rework

        var again = await ScanAsync(s, board);

        Assert.Equal(("Ok", false), (again.Result, again.Duplicate));
        Assert.Equal(4, await ScanLogsAsync(board));   // SMT, ASSY NG, Repair check-in, SMT again
        Assert.Equal(2, (await ComponentsAsync(s)).Count);
        Assert.Equal(996m, (await LotAsync(cap)).Remaining);
    }

    // (d) Ten boards at the same moment on one reel: the SQL UPDATE subtracts atomically, nothing is lost.
    [Fact]
    public async Task Simultaneous_scans_subtract_exactly()
    {
        var s = await SetupAsync();
        var (cap, res) = (await LoadedLotAsync(s, s.CapId), await LoadedLotAsync(s, s.ResId));
        var clients = new List<HttpClient>();
        foreach (var _ in s.Boards) clients.Add(await api.LoginOperatorAsync(stationCode: "L1-SMT-01"));

        var answers = await ScanTests.TogetherAsync(s.Boards.Select((b, i) => (Func<Task<ScanResponse>>)(() =>
            scan.ScanAsync(clients[i], "L1-SMT-01", b))).ToArray());

        Assert.All(answers, a => Assert.Equal("Ok", a.Result));
        Assert.Equal(20, (await ComponentsAsync(s)).Count);
        Assert.Equal(960m, (await LotAsync(cap)).Remaining);
        Assert.Equal(980m, (await LotAsync(res)).Remaining);
    }

    // (e) A loaded reel put on Hold (what M7 containment does) or expiring mid-shift stops the next board.
    [Fact]
    public async Task Lot_put_on_hold_or_expired_after_loading_blocks_the_scan()
    {
        var s = await SetupAsync(3);
        var (cap, res) = (await LoadedLotAsync(s, s.CapId), await LoadedLotAsync(s, s.ResId));
        Assert.Equal("Ok", (await ScanAsync(s, s.Boards[0])).Result);

        await api.WithDbAsync(db => db.MaterialLots.Where(l => l.Id == cap).ExecuteUpdateAsync(x => x.SetProperty(l => l.Status, MaterialLotStatus.Hold)));
        var held = await ScanAsync(s, s.Boards[1]);
        await api.WithDbAsync(db => db.MaterialLots.Where(l => l.Id == cap).ExecuteUpdateAsync(x => x.SetProperty(l => l.Status, MaterialLotStatus.Available)));
        await api.WithDbAsync(db => db.MaterialLots.Where(l => l.Id == res)
            .ExecuteUpdateAsync(x => x.SetProperty(l => l.ExpiryAt, DateTime.UtcNow.AddMinutes(-1))));
        var expired = await ScanAsync(s, s.Boards[2]);

        Assert.Equal(("Blocked", "MaterialNotLoaded"), (held.Result, held.ReasonCode));
        Assert.Contains("is on hold", held.Message);
        Assert.Equal(("Blocked", "MaterialNotLoaded"), (expired.Result, expired.ReasonCode));
        Assert.Contains("has expired", expired.Message);
        Assert.Equal(2, (await ComponentsAsync(s)).Count);   // only the first board
        Assert.Equal(996m, (await LotAsync(cap)).Remaining);
    }

    // (f) R10: 10 parts, 4 per board. 6, 2, then 0 (not -2) and Consumed; the reel may still have parts, so the
    // next board passes and is still traced to it.
    [Fact]
    public async Task Theoretical_quantity_floors_at_zero_and_the_lot_becomes_consumed()
    {
        var s = await SetupAsync(4);
        var cap = await LoadedLotAsync(s, s.CapId, qty: 10);
        await LoadedLotAsync(s, s.ResId);

        var after = new List<(decimal, MaterialLotStatus)>();
        foreach (var board in s.Boards)
        {
            Assert.Equal("Ok", (await ScanAsync(s, board)).Result);
            after.Add(await LotAsync(cap));
        }

        Assert.Equal([(6m, MaterialLotStatus.Available), (2m, MaterialLotStatus.Available), (0m, MaterialLotStatus.Consumed),
            (0m, MaterialLotStatus.Consumed)], after);
        Assert.Equal(4, (await ComponentsAsync(s)).Count(r => r.LotId == cap));
    }

    // (g) The consumption fails after the scan was written: the whole scan rolls back. Without the fault the same
    // board then scans normally (nothing half-written stood in the way).
    [Fact]
    public async Task Failing_consumption_rolls_the_scan_back()
    {
        var s = await SetupAsync(1);
        var (cap, _) = (await LoadedLotAsync(s, s.CapId), await LoadedLotAsync(s, s.ResId));
        var board = s.Boards[0];
        api.Factory.ConsumptionFault.FailForSerialNo = board;

        var failed = await s.Operator.PostAsJsonAsync($"/api/stations/{await scan.StationIdAsync("L1-SMT-01")}/scan", new ScanRequest(board));

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(0, await ScanLogsAsync(board));
        Assert.Empty(await ComponentsAsync(s));
        Assert.Equal(1_000m, (await LotAsync(cap)).Remaining);
        Assert.Equal(0, await api.WithDbAsync(db => db.WorkOrderOperations.Where(o => o.WorkOrderId == s.WoId && o.Seq == 10).Select(o => o.QtyGood).SingleAsync()));
        Assert.Equal(WorkOrderStatus.Released, await api.WithDbAsync(db => db.WorkOrders.Where(w => w.Id == s.WoId).Select(w => w.Status).SingleAsync()));

        api.Factory.ConsumptionFault.FailForSerialNo = null;
        Assert.Equal("Ok", (await ScanAsync(s, board)).Result);
        Assert.Equal(2, (await ComponentsAsync(s)).Count);
    }

    // Solder paste, 0.0006363 kg per board (BOM snapshot decimal(28,9)): kept to 6 decimals, not rounded to 0.001
    // as with the first decimal(18,3) columns (docs/incidents.md, 2026-11-02).
    [Fact]
    public async Task Small_quantities_per_board_keep_six_decimals()
    {
        var s = await SetupAsync(1, capPerBoard: 0.0006363m);
        var (paste, _) = (await LoadedLotAsync(s, s.CapId, qty: 10), await LoadedLotAsync(s, s.ResId));

        Assert.Equal("Ok", (await ScanAsync(s, s.Boards[0])).Result);

        Assert.Equal(0.000636m, (await ComponentsAsync(s)).Single(r => r.ItemId == s.CapId).Qty);
        Assert.Equal(9.999364m, (await LotAsync(paste)).Remaining);
    }

    // Gate off (Material:EnforceLoadedMaterial = false): the scan passes; what is loaded is still recorded.
    [Fact]
    public async Task With_the_gate_off_the_scan_passes_and_records_what_is_loaded()
    {
        options.EnforceLoadedMaterial = false;
        var s = await SetupAsync(1);
        var cap = await LoadedLotAsync(s, s.CapId);

        var answer = await ScanAsync(s, s.Boards[0]);

        Assert.Equal("Ok", answer.Result);
        Assert.Equal([(s.CapId, cap)], (await ComponentsAsync(s)).Select(r => (r.ItemId, r.LotId)));
    }
}
