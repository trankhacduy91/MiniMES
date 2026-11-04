using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Material;
using MiniMes.Contracts.Packing;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Material;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;
using MiniMes.IntegrationTests.Execution;
using MiniMes.IntegrationTests.Infrastructure;
using MiniMes.IntegrationTests.Packing;

namespace MiniMes.IntegrationTests.Quality;

// Spec M7 step 9 / DoD 5.1: Hold -> blocked at every gate (route check M3, reel loading M6, shipment M3b) -> Release
// gives back the old status; overlapping holds (Q8); the NCR decision acts on its targets in the same transaction.
// The DoD chain end to end (real M6 consumption) and the Hold vs scan race: HoldFlowTests (step 10).
// Each test has its own WO of its own item (routing SMT-ASSY-TEST-PACK on L1, one traced part at SMT) and its own
// lots, so holds never touch another test's boards; window holds are released at the end of their test.
[Collection(ApiCollection.Name)]
public class HoldApiTests(ApiFixture api)
{
    private static readonly DateTime T0 = new(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);
    private static int _nextSerial;
    private readonly ScanTests scan = new(api);

    internal sealed record Setup(int WoId, int PartId, List<string> Boards, HttpClient Op);

    internal sealed record Lots(string Used, string Sibling, string Pending);

    // ---------- Hold all by lot -> gates -> Release ----------

    [Fact]
    public async Task Hold_all_by_lot_blocks_every_gate_and_release_gives_back_the_old_status()
    {
        var s = await SetupAsync(3);
        await ThroughAsync(s, s.Boards[0], "L1-SMT-01", "L1-ASM-01", "L1-TEST-01");                // waits for PACK
        await ThroughAsync(s, s.Boards[1], "L1-SMT-01", "L1-ASM-01", "L1-TEST-01", "L1-PACK-01");  // Completed
        var lots = await LotUsedByAsync(s, s.Boards[0], s.Boards[1]);
        var qc = await api.LoginAsync("qc01");
        var request = new HoldTargetsRequest("Lot", LotNo: lots.Used, Reason: "Wrong resistor value reported by supplier");

        var preview = await OkAsync<HoldPreviewDto>(qc.PostAsJsonAsync("/api/quality/holds/preview", request));
        var held = await OkAsync<HoldResultDto>(qc.PostAsJsonAsync("/api/quality/holds", request));

        Assert.Equal((2, 0), (preview.SerialCount, preview.AlreadyHeld));
        Assert.Equal(["Completed:1", "InProcess:1"], preview.SerialsByStatus.Select(x => $"{x.Status}:{x.Count}").Order());
        Assert.Equal(new[] { lots.Used, lots.Sibling }.Order(), preview.Lots);   // the same delivery too
        Assert.Equal((lots.Pending, "PendingIqc"), (preview.SkippedLots.Single().LotNo, preview.SkippedLots.Single().Status));
        Assert.Equal((2, 2), (held.Serials, held.Lots));
        Assert.Equal((SerialStatus.Hold, SerialStatus.InProcess), await SerialAsync(s.Boards[0]));
        Assert.Equal((SerialStatus.Hold, SerialStatus.Completed), await SerialAsync(s.Boards[1]));
        Assert.Equal(SerialStatus.Created, (await SerialAsync(s.Boards[2])).Status);                  // not made from the lot
        Assert.Equal((MaterialLotStatus.Hold, MaterialLotStatus.Available), await LotAsync(lots.Used));
        Assert.Equal(MaterialLotStatus.PendingIqc, (await LotAsync(lots.Pending)).Status);
        Assert.Equal(4, await api.WithDbAsync(db => db.HoldRecords.CountAsync(h => h.BatchId == held.BatchId && h.ReleasedAt == null)));
        Assert.Equal(1, await api.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.TableName == "HoldRecords" && a.RecordKey == held.BatchId.ToString())));

        // The gates read Status: Pack refuses the board, the station refuses the reel.
        var atPack = await scan.ScanAsync(s.Op, "L1-PACK-01", s.Boards[0]);
        Assert.Equal(("Blocked", "SerialHold"), (atPack.Result, atPack.ReasonCode));
        var load = await LoadAsync(s, lots.Sibling);
        Assert.Equal(("Rejected", "LotOnHold"), (load.Outcome, load.Code));

        var sup = await api.LoginAsync("sup01");
        var noNote = await sup.PostAsJsonAsync($"/api/quality/holds/{held.BatchId}/release", new ReleaseHoldRequest(" "));
        var released = await OkAsync<ReleaseResultDto>(sup.PostAsJsonAsync($"/api/quality/holds/{held.BatchId}/release",
            new ReleaseHoldRequest("Supplier confirmed: value printed wrong on the label only")));
        var again = await sup.PostAsJsonAsync($"/api/quality/holds/{held.BatchId}/release", new ReleaseHoldRequest("again"));

        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);
        Assert.Equal((4, 4, 0), (released.Released, released.Restored, released.StillHeld));
        Assert.Equal((SerialStatus.InProcess, (SerialStatus?)null), await SerialAsync(s.Boards[0]));
        Assert.Equal((SerialStatus.Completed, (SerialStatus?)null), await SerialAsync(s.Boards[1]));   // not "Created"
        Assert.Equal((MaterialLotStatus.Available, (MaterialLotStatus?)null), await LotAsync(lots.Used));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("Ok", (await scan.ScanAsync(s.Op, "L1-PACK-01", s.Boards[0])).Result);
    }

    // Q8: two NCRs suspect the same board; the first release leaves it held, the second gives its status back.
    [Fact]
    public async Task Overlapping_holds_restore_only_when_the_last_one_is_released()
    {
        var s = await SetupAsync(1);
        await ThroughAsync(s, s.Boards[0], "L1-SMT-01");
        var qc = await api.LoginAsync("qc01");
        var first = await HoldSerialsAsync(qc, "Solder bridge seen at AOI", s.Boards[0]);
        var second = await HoldSerialsAsync(qc, "Customer complaint on the same date code", s.Boards[0]);

        var one = await ReleaseAsync(qc, first.BatchId, "AOI false call");
        var stillHeld = await SerialAsync(s.Boards[0]);
        var two = await ReleaseAsync(qc, second.BatchId, "Complaint not related");

        Assert.Equal((1, 0, 1), (one.Released, one.Restored, one.StillHeld));
        Assert.Equal((SerialStatus.Hold, SerialStatus.InProcess), stillHeld);
        Assert.Equal((1, 1, 0), (two.Released, two.Restored, two.StillHeld));
        Assert.Equal((SerialStatus.InProcess, (SerialStatus?)null), await SerialAsync(s.Boards[0]));
    }

    // [v2] (a) DoD: holding a pallet holds every board in it, and the pallet cannot ship (M3b).
    [Fact]
    public async Task Holding_a_pallet_holds_its_boards_and_stops_the_shipment()
    {
        var pallets = new PalletTests(api);
        await pallets.FreshPalletAsync();
        var (_, serials, cartons, op) = await pallets.PackedAsync(20);
        foreach (var c in cartons) await pallets.PalletizeAsync(op, c);
        var palletNo = (await op.GetFromJsonAsync<CartonDto>($"/api/cartons/{cartons[0]}"))!.PalletNo!;
        var sup = await api.LoginAsync("sup01");
        await OkAsync<PalletDto>(sup.PostAsJsonAsync($"/api/pallets/{palletNo}/close-partial", new ClosePartialRequest("Hold test")));

        var held = await OkAsync<HoldResultDto>(sup.PostAsJsonAsync("/api/quality/holds",
            new HoldTargetsRequest("Pallet", PalletNo: palletNo, Reason: "Customer return of the same week")));
        var ship = await sup.PostAsJsonAsync($"/api/pallets/{palletNo}/ship", new ShipPalletRequest("DO-HOLD-1"));
        await ReleaseAsync(sup, held.BatchId, "Week cleared");

        Assert.Equal((20, 0), (held.Serials, held.Lots));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ship.StatusCode);
        Assert.Contains("held serials", await ship.Content.ReadAsStringAsync());
        Assert.All(await api.WithDbAsync(db => db.Serials.Where(x => serials.Contains(x.SerialNo)).Select(x => x.Status).ToListAsync()),
            st => Assert.Equal(SerialStatus.Completed, st));
    }

    // [v2] (b) the oven drifted between t1 and t2: the boards scanned OK at its work center (SMT) in [t1, t2) are held,
    // the one scanned after is not.
    [Fact]
    public async Task Machine_window_holds_the_boards_that_passed_the_machine_in_the_window()
    {
        var s = await SetupAsync(3);
        var from = DateTime.UtcNow.AddSeconds(-1);
        await ThroughAsync(s, s.Boards[0], "L1-SMT-01");
        await ThroughAsync(s, s.Boards[1], "L1-SMT-01");
        await Task.Delay(50);
        var to = await api.WithDbAsync(db => db.ScanLogs.Where(l => l.SerialNo == s.Boards[1]).Select(l => l.ScannedAt).SingleAsync());
        to = to.AddTicks(1);
        await Task.Delay(50);
        await ThroughAsync(s, s.Boards[2], "L1-SMT-01");
        var qc = await api.LoginAsync("qc01");
        var request = new HoldTargetsRequest("MachineWindow", MachineCode: "L1-REFLOW-01", From: from, To: to, Reason: "Reflow peak drifted (WE run 7)");

        var preview = await OkAsync<HoldPreviewDto>(qc.PostAsJsonAsync("/api/quality/holds/preview", request));
        var held = await OkAsync<HoldResultDto>(qc.PostAsJsonAsync("/api/quality/holds", request));
        var states = new[] { (await SerialAsync(s.Boards[0])).Status, (await SerialAsync(s.Boards[1])).Status, (await SerialAsync(s.Boards[2])).Status };
        await ReleaseAsync(qc, held.BatchId, "Profile checked, boards re-inspected");

        Assert.StartsWith("L1-REFLOW-01 ", preview.ScopeRef);
        Assert.Equal([SerialStatus.Hold, SerialStatus.Hold, SerialStatus.InProcess], states);
        var tooLong = await qc.PostAsJsonAsync("/api/quality/holds/preview", request with { To = from.AddDays(8) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    // ---------- NCR decisions act on the NCR's targets (user decision: only those) ----------

    [Fact]
    public async Task Use_as_is_on_a_lot_ncr_frees_the_lot_but_its_boards_wait_for_an_explicit_release()
    {
        var s = await SetupAsync(2);
        await ThroughAsync(s, s.Boards[0], "L1-SMT-01");
        await ThroughAsync(s, s.Boards[1], "L1-SMT-01");
        var lots = await LotUsedByAsync(s, s.Boards[0], s.Boards[1]);
        var qc = await api.LoginAsync("qc01");
        var held = await OkAsync<HoldResultDto>(qc.PostAsJsonAsync("/api/quality/holds",
            new HoldTargetsRequest("Lot", LotNo: lots.Used, Reason: "Supplier SCAR on resistor value", CreateNcr: true)));

        var ncr = await DecideAsync(held.NcrId!.Value, "UseAsIs", "Value within the design margin");
        var close = await CloseAsync(ncr);
        var boards = (await SerialAsync(s.Boards[0])).Status;
        await ReleaseAsync(qc, held.BatchId, $"{held.NcrNo} use as is: boards released");
        var closed = await CloseAsync(await GetNcrAsync(ncr.Id));

        Assert.NotNull(held.NcrNo);
        Assert.Equal(("Dispositioned", 2), (ncr.Status, ncr.ActiveHolds));          // the two boards
        Assert.Equal((MaterialLotStatus.Available, MaterialLotStatus.Available), ((await LotAsync(lots.Used)).Status, (await LotAsync(lots.Sibling)).Status));
        Assert.Equal(SerialStatus.Hold, boards);                                    // not let go silently
        Assert.Equal(HttpStatusCode.UnprocessableEntity, close.StatusCode);         // Q12
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal(SerialStatus.InProcess, (await SerialAsync(s.Boards[0])).Status);
    }

    [Theory]
    [InlineData("Scrap", SerialStatus.Scrapped)]
    [InlineData("Rework", SerialStatus.Repair)]
    public async Task A_serial_ncr_decision_releases_its_hold_and_moves_the_boards(string disposition, SerialStatus expected)
    {
        var s = await SetupAsync(2);
        await ThroughAsync(s, s.Boards[0], "L1-SMT-01", "L1-ASM-01");
        await ThroughAsync(s, s.Boards[1], "L1-SMT-01");
        var qc = await api.LoginAsync("qc01");
        var held = await OkAsync<HoldResultDto>(qc.PostAsJsonAsync("/api/quality/holds",
            new HoldTargetsRequest("Serials", SerialNos: s.Boards, Reason: "Cracked connector", CreateNcr: true)));

        var ncr = await DecideAsync(held.NcrId!.Value, disposition, "Decided by MRB");

        Assert.Equal(0, ncr.ActiveHolds);
        foreach (var b in s.Boards) Assert.Equal(expected, (await SerialAsync(b)).Status);
        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(ncr)).StatusCode);
    }

    // Q8 with an NCR: scrapped by NCR 1 while NCR 2 still holds it -> stays Hold; the scrap happens at the last release.
    [Fact]
    public async Task A_decision_on_a_board_another_hold_keeps_waits_for_that_release()
    {
        var s = await SetupAsync(1);
        await ThroughAsync(s, s.Boards[0], "L1-SMT-01");
        var qc = await api.LoginAsync("qc01");
        var first = await OkAsync<HoldResultDto>(qc.PostAsJsonAsync("/api/quality/holds",
            new HoldTargetsRequest("Serials", SerialNos: [s.Boards[0]], Reason: "Burnt part", CreateNcr: true)));
        var other = await HoldSerialsAsync(qc, "Customer complaint", s.Boards[0]);

        await DecideAsync(first.NcrId!.Value, "Scrap", "Burnt beyond repair");
        var waiting = await SerialAsync(s.Boards[0]);
        await ReleaseAsync(qc, other.BatchId, "Complaint closed");

        Assert.Equal((SerialStatus.Hold, SerialStatus.Scrapped), waiting);
        Assert.Equal(SerialStatus.Scrapped, (await SerialAsync(s.Boards[0])).Status);
    }

    [Fact]
    public async Task Return_to_vendor_returns_the_lot_and_use_as_is_brings_an_iqc_rejected_lot_back()
    {
        var s = await SetupAsync(1);
        var lots = await LotUsedByAsync(s);
        await api.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE dbo.MaterialLots SET Status = 4 WHERE LotNo = {lots.Pending}"));
        var qc = await api.LoginAsync("qc01");
        var rtv = await CreateNcrAsync(qc, ("Lot", lots.Used));
        var useAsIs = await CreateNcrAsync(qc, ("Lot", lots.Pending));

        await DecideAsync(rtv.Id, "ReturnToVendor", "SCAR-26-031, RMA 4471");
        await DecideAsync(useAsIs.Id, "UseAsIs", "Deviation DV-12 from the customer");

        var returned = await api.WithDbAsync(db => db.MaterialLots.Where(l => l.LotNo == lots.Used).Select(l => new { l.Status, l.RemainingQty }).SingleAsync());
        Assert.Equal((MaterialLotStatus.Returned, 0m), (returned.Status, returned.RemainingQty));
        Assert.Equal(MaterialLotStatus.Available, (await LotAsync(lots.Pending)).Status);
    }

    // ---------- race, roles ----------

    [Fact]
    public async Task Only_qc_and_supervisors_hold_and_a_hold_needs_a_reason()
    {
        var s = await SetupAsync(1);
        var request = new HoldTargetsRequest("Serials", SerialNos: [s.Boards[0]], Reason: "x");

        var byOperator = await s.Op.PostAsJsonAsync("/api/quality/holds", request);
        var byPlanner = await (await api.LoginAsync("planner01")).PostAsJsonAsync("/api/quality/holds", request);
        var qc = await api.LoginAsync("qc01");
        var noReason = await qc.PostAsJsonAsync("/api/quality/holds", request with { Reason = null });
        var unknown = await qc.PostAsJsonAsync("/api/quality/holds/preview", request with { SerialNos = ["PCBA0000000000"] });

        Assert.Equal(HttpStatusCode.Forbidden, byOperator.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byPlanner.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    // ---------- helpers ----------

    // A released WO of a new item with one traced part at SMT, sessions open on SMT/ASSY/TEST/PACK.
    internal async Task<Setup> SetupAsync(int boards)
    {
        var made = await api.WithDbAsync(async db =>
        {
            var part = Item.Create(ApiFixture.Unique("RAWH"), "Hold test part", ItemType.Raw, Units.Pieces, false, true);
            var board = Item.Create(ApiFixture.Unique("HOLD"), "Hold test board", ItemType.Finished, Units.Pieces, true, false);
            db.Items.AddRange(part, board);
            await db.SaveChangesAsync();
            var wc = await db.WorkCenters.ToDictionaryAsync(w => w.Code, w => w.Id);
            var line = await db.Stations.Where(st => st.Code == "L1-SMT-01").Select(st => st.LineId).SingleAsync();
            var wo = WorkOrder.Create(ApiFixture.Unique("WO-HOLD"), board.Id, line, boards, T0, T0.AddHours(8), requireFai: false);
            wo.Release(1, 1, [new MaterialSnapshot(part.Id, 10, 1m)],
                [new RoutingStepSnapshot(10, "SMT", wc["WC-SMT"], 30m, false, true, null),
                 new RoutingStepSnapshot(20, "ASSY", wc["WC-ASSY"], 60m, false, true, null),
                 new RoutingStepSnapshot(30, "TEST", wc["WC-TEST"], 45m, true, true, null),
                 new RoutingStepSnapshot(40, "PACK", wc["WC-PACK"], 20m, false, true, null)], T0);
            db.WorkOrders.Add(wo);
            await db.SaveChangesAsync();
            var serials = Enumerable.Range(0, boards)
                .Select(_ => SerialFormat.Build("HOLD", new DateOnly(2026, 11, 4), Interlocked.Increment(ref _nextSerial))).ToList();
            db.Serials.AddRange(serials.Select(sn => new Serial(sn, wo.Id, board.Id)));
            await db.SaveChangesAsync();
            return (wo.Id, part.Id, serials);
        });
        foreach (var station in new[] { "L1-SMT-01", "L1-ASM-01", "L1-TEST-01", "L1-PACK-01" }) await scan.OpenSessionAsync(station, made.Item1);
        return new Setup(made.Item1, made.Item2, made.Item3, await api.LoginOperatorAsync(stationCode: "L1-SMT-01"));
    }

    internal async Task ThroughAsync(Setup s, string board, params string[] stations)
    {
        foreach (var st in stations) Assert.Equal("Ok", (await scan.ScanAsync(s.Op, st, board)).Result);
    }

    // A reel of the WO's part used on the given boards (as the M6 consumption writes it), a second reel of the same
    // delivery (Available), a third still waiting for IQC.
    internal Task<Lots> LotUsedByAsync(Setup s, params string[] boards) => api.WithDbAsync(async db =>
    {
        var supplierLot = ApiFixture.Unique("SUPH");
        var used = new MaterialLot(ApiFixture.Unique("LH"), s.PartId, 1_000, supplierLot, T0, null, false);
        var sibling = new MaterialLot(ApiFixture.Unique("LH"), s.PartId, 1_000, supplierLot, T0, null, false);
        var pending = new MaterialLot(ApiFixture.Unique("LH"), s.PartId, 1_000, supplierLot, T0, null, true);
        db.MaterialLots.AddRange(used, sibling, pending);
        await db.SaveChangesAsync();
        foreach (var b in boards)
        {
            var log = await db.ScanLogs.Where(l => l.SerialNo == b && l.Seq == 10).Select(l => new { l.Id, l.SerialId, l.StationId }).FirstAsync();
            db.SerialComponents.Add(new SerialComponent(log.SerialId, used.Id, s.PartId, 1m, log.StationId, log.Id, T0));
        }
        await db.SaveChangesAsync();
        return new Lots(used.LotNo, sibling.LotNo, pending.LotNo);
    });

    internal async Task<LoadMaterialResponse> LoadAsync(Setup s, string lotNo)
    {
        var response = await s.Op.PostAsJsonAsync($"/api/stations/{await scan.StationIdAsync("L1-SMT-01")}/material-loads", new LoadMaterialRequest(lotNo));
        return await OkAsync<LoadMaterialResponse>(Task.FromResult(response));
    }

    private static Task<HoldResultDto> HoldSerialsAsync(HttpClient client, string reason, params string[] serials)
        => OkAsync<HoldResultDto>(client.PostAsJsonAsync("/api/quality/holds", new HoldTargetsRequest("Serials", SerialNos: serials, Reason: reason)));

    internal static Task<ReleaseResultDto> ReleaseAsync(HttpClient client, Guid batchId, string note)
        => OkAsync<ReleaseResultDto>(client.PostAsJsonAsync($"/api/quality/holds/{batchId}/release", new ReleaseHoldRequest(note)));

    private static Task<NcrDto> CreateNcrAsync(HttpClient qc, params (string Type, string Code)[] targets)
        => OkAsync<NcrDto>(qc.PostAsJsonAsync("/api/ncrs", new CreateNcrRequest("Manual", null, null, "Hold test NCR", 1,
            targets.Select(t => new NcrTargetRequest(t.Type, t.Code)).ToList(), null)));

    private async Task<NcrDto> GetNcrAsync(int id) => (await (await api.LoginAsync("qc01")).GetFromJsonAsync<NcrDto>($"/api/ncrs/{id}"))!;

    // qc01 reviews and proposes, sup01 approves (Q11).
    private async Task<NcrDto> DecideAsync(int ncrId, string disposition, string note)
    {
        var qc = await api.LoginAsync("qc01");
        var ncr = await GetNcrAsync(ncrId);
        if (ncr.Status == "Open") ncr = await OkAsync<NcrDto>(qc.PostAsJsonAsync($"/api/ncrs/{ncrId}/start-review", new NcrTransitionRequest(ncr.RowVersion)));
        ncr = await OkAsync<NcrDto>(qc.PostAsJsonAsync($"/api/ncrs/{ncrId}/propose", new ProposeDispositionRequest(disposition, note, null, ncr.RowVersion)));
        return await OkAsync<NcrDto>((await api.LoginAsync("sup01")).PostAsJsonAsync($"/api/ncrs/{ncrId}/approve", new NcrTransitionRequest(ncr.RowVersion)));
    }

    private async Task<HttpResponseMessage> CloseAsync(NcrDto ncr)
        => await (await api.LoginAsync("qc01")).PostAsJsonAsync($"/api/ncrs/{ncr.Id}/close", new CloseNcrRequest(null, ncr.RowVersion));

    internal Task<(SerialStatus Status, SerialStatus? Before)> SerialAsync(string serialNo) => api.WithDbAsync(async db =>
        await db.Serials.Where(x => x.SerialNo == serialNo).Select(x => new ValueTuple<SerialStatus, SerialStatus?>(x.Status, x.StatusBeforeHold)).SingleAsync());

    internal Task<(MaterialLotStatus Status, MaterialLotStatus? Before)> LotAsync(string lotNo) => api.WithDbAsync(async db =>
        await db.MaterialLots.Where(x => x.LotNo == lotNo).Select(x => new ValueTuple<MaterialLotStatus, MaterialLotStatus?>(x.Status, x.StatusBeforeHold)).SingleAsync());

    internal static async Task<T> OkAsync<T>(Task<HttpResponseMessage> call)
    {
        var response = await call;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
