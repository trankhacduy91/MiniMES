using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Quality;
using MiniMes.Contracts.Realtime;
using MiniMes.Domain.Material;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;
using MiniMes.Domain.Quality.Fai;
using MiniMes.Infrastructure.Persistence.Seed;
using MiniMes.IntegrationTests.Execution;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Quality;

// [v2] Spec M7 step 8b: the FAI worksheet (three automatic checks) and the QC result that opens the M3 gate, across
// M2 (WO FaiStatus), M3 (route check, scans), M6 (trace of the sample) and M7 (measurements, FaiRecord).
// Each test builds its own item (routing SMT-ASSY-TEST-PACK on L1, two reels at SMT and one at ASSY, a 3.3 V plan
// at TEST, a FAI checklist): plans other tests add to PCB-A would otherwise turn "not measured" red here.
// The trace rows are written straight to SerialComponent, as if the M6 consumption had run: loading reels at the
// shared L1 stations would leak into every other test's boards. M6's own tests cover the consumption.
[Collection(ApiCollection.Name)]
public class FaiApiTests(ApiFixture api)
{
    private static readonly DateTime T0 = new(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);
    private static int _nextSerial;
    private readonly ScanTests scan = new(api);

    private sealed record Setup(int WoId, int ItemId, string Plan, List<string> Boards, int[] RawItems, HttpClient Op);

    [Fact]
    public async Task Pass_with_a_green_worksheet_opens_the_line_for_the_whole_lot()
    {
        var s = await SetupAsync();
        await SampleThroughTestAsync(s);
        Assert.Equal("FaiNotApproved", (await ScanSmtAsync(s, s.Boards[1])).ReasonCode);
        var qc = await api.LoginAsync("qc01");
        await using var kiosk = await LineHubAsync(s.Op);
        var pushed = NextFaiPush(kiosk);

        var sheet = await WorksheetAsync(qc, s.WoId);
        Assert.Equal((true, (string?)null, s.Boards[0], "InProcess", true), (sheet.SampleReady, sheet.NotReadyReason, sheet.SampleSerialNo,
            sheet.SampleStatus, sheet.AutoChecksPassed));
        Assert.All(sheet.Items.Where(i => i.Kind == "Auto"), i => Assert.True(i.Passed, $"{i.Code}: {i.Detail}"));
        Assert.All(sheet.Items.Where(i => i.Kind == "Manual"), i => Assert.Null(i.Passed));

        var response = await RecordAsync(qc, s.WoId, new RecordFaiRequest("Pass", ["VISUAL", "LABEL"], "First board OK", null, sheet.RowVersion));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = (await response.Content.ReadFromJsonAsync<FaiWorksheetDto>())!;
        var record = Assert.Single(after.History);
        Assert.Equal(("Passed", "Pass", "qc01", s.Boards[0], true, (string?)null), (after.FaiStatus, record.Result, record.InspectedBy,
            record.SerialNo, record.AutoChecksPassed, record.DefectCode));
        Assert.All(record.Items, i => Assert.True(i.Passed));   // what QC saw and ticked, kept with the record
        Assert.Equal(("Ok", "Ok"), ((await ScanSmtAsync(s, s.Boards[1])).Result, (await ScanSmtAsync(s, s.Boards[2])).Result));
        var push = await pushed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((s.WoId, "L1", "Passed", "Pass"), (push.WorkOrderId, push.Line, push.FaiStatus, push.Result));
    }

    [Fact]
    public async Task Fail_sends_the_sample_to_repair_and_the_next_board_becomes_the_sample()
    {
        var s = await SetupAsync();
        await SampleThroughTestAsync(s);
        var qc = await api.LoginAsync("qc01");
        var sheet = await WorksheetAsync(qc, s.WoId);

        var response = await RecordAsync(qc, s.WoId, new RecordFaiRequest("Fail", ["LABEL"], "D3 polarity reversed", null, sheet.RowVersion));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = (await response.Content.ReadFromJsonAsync<FaiWorksheetDto>())!;
        Assert.Equal(("Pending", (string?)null, "Fail", "FAI-FAIL"), (after.FaiStatus, after.SampleSerialNo, after.History[0].Result,
            after.History[0].DefectCode));
        Assert.Equal(SerialStatus.Ng, await SerialStatusAsync(s.Boards[0]));
        Assert.Equal("Ok", (await ScanSmtAsync(s, s.Boards[1])).Result);   // the new sample
        Assert.Equal("FaiNotApproved", (await ScanSmtAsync(s, s.Boards[2])).ReasonCode);
        Assert.Equal(s.Boards[1], (await WorksheetAsync(qc, s.WoId)).SampleSerialNo);
    }

    // Spec check: a wrong reel on the sample (verification bypassed in the test) -> BomMatch red, Pass refused.
    [Fact]
    public async Task A_wrong_reel_on_the_sample_turns_bom_match_red_and_pass_is_refused()
    {
        var s = await SetupAsync();
        var wrong = await RawItemAsync("RESW");
        await SampleThroughTestAsync(s, swapFirstReelFor: wrong.Id);
        var qc = await api.LoginAsync("qc01");

        var sheet = await WorksheetAsync(qc, s.WoId);
        var bom = sheet.Items.Single(i => i.Code == "BomMatch");
        var response = await RecordAsync(qc, s.WoId, new RecordFaiRequest("Pass", ["VISUAL", "LABEL"], null, null, sheet.RowVersion));

        Assert.False(sheet.AutoChecksPassed);
        Assert.False(bom.Passed);
        Assert.Contains(wrong.Code, bom.Detail);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("BomMatch", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await api.WithDbAsync(db => db.FaiRecords.CountAsync(r => r.WorkOrderId == s.WoId)));
        Assert.Equal(FaiStatus.Pending, await api.WithDbAsync(db => db.WorkOrders.Where(w => w.Id == s.WoId).Select(w => w.FaiStatus).SingleAsync()));
    }

    // Q17: the sample must be through Test before QC judges it, and Pass needs every manual item ticked.
    [Fact]
    public async Task Fai_waits_for_the_sample_to_pass_test_and_for_every_manual_item()
    {
        var s = await SetupAsync();
        var qc = await api.LoginAsync("qc01");
        var noSample = await WorksheetAsync(qc, s.WoId);
        var noSampleFail = await RecordAsync(qc, s.WoId, new RecordFaiRequest("Fail", [], null, null, noSample.RowVersion));
        await ScanSmtAsync(s, s.Boards[0]);

        var atSmt = await WorksheetAsync(qc, s.WoId);
        var tooEarly = await RecordAsync(qc, s.WoId, new RecordFaiRequest("Fail", [], null, null, atSmt.RowVersion));
        await SampleThroughTestAsync(s, alreadyAtSmt: true);
        var ready = await WorksheetAsync(qc, s.WoId);
        var unticked = await RecordAsync(qc, s.WoId, new RecordFaiRequest("Pass", ["VISUAL"], null, null, ready.RowVersion));
        var unknownItem = await RecordAsync(qc, s.WoId, new RecordFaiRequest("Pass", ["VISUAL", "LABEL", "SMELL"], null, null, ready.RowVersion));

        Assert.Equal((false, "No sample yet: the first board scanned at the first step becomes the sample."), (noSample.SampleReady, noSample.NotReadyReason));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noSampleFail.StatusCode);
        Assert.Equal((false, $"Sample {s.Boards[0]} has not passed TEST yet."), (atSmt.SampleReady, atSmt.NotReadyReason));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooEarly.StatusCode);
        Assert.True(ready.SampleReady);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unticked.StatusCode);
        Assert.Contains("LABEL", await unticked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, unknownItem.StatusCode);
        Assert.Equal(0, await api.WithDbAsync(db => db.FaiRecords.CountAsync(r => r.WorkOrderId == s.WoId)));
    }

    [Fact]
    public async Task Only_qc_records_a_stale_worksheet_is_409_and_the_queue_shows_ready_samples_first()
    {
        var s = await SetupAsync();
        await SampleThroughTestAsync(s);
        var (qc, supervisor, planner) = (await api.LoginAsync("qc01"), await api.LoginAsync("sup01"), await api.LoginAsync("planner01"));
        var sheet = await WorksheetAsync(supervisor, s.WoId);   // a Supervisor reads it
        var pass = new RecordFaiRequest("Pass", ["VISUAL", "LABEL"], null, null, sheet.RowVersion);

        var queue = await qc.GetFromJsonAsync<List<FaiQueueItemDto>>("/api/fai/queue");
        var bySupervisor = await RecordAsync(supervisor, s.WoId, pass);
        var byPlanner = await RecordAsync(planner, s.WoId, pass);
        var first = await RecordAsync(qc, s.WoId, pass);
        var second = await RecordAsync(await api.LoginAsync("qc01"), s.WoId, pass with { Result = "Fail" });   // same old worksheet
        var unknown = await RecordAsync(qc, int.MaxValue, pass);

        var mine = Assert.Single(queue!, q => q.WorkOrderId == s.WoId);
        Assert.Equal((s.Boards[0], true), (mine.SampleSerialNo, mine.SampleReady));
        Assert.True(queue!.TakeWhile(q => q.SampleReady).Count() == queue!.Count(q => q.SampleReady), "ready samples first");
        Assert.Equal(HttpStatusCode.Forbidden, bySupervisor.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byPlanner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(1, await api.WithDbAsync(db => db.FaiRecords.CountAsync(r => r.WorkOrderId == s.WoId)));
        Assert.DoesNotContain((await qc.GetFromJsonAsync<List<FaiQueueItemDto>>("/api/fai/queue"))!, q => q.WorkOrderId == s.WoId);
    }

    // ---------- helpers ----------

    private Task<Item> RawItemAsync(string prefix) => api.WithDbAsync(async db =>
    {
        var item = Item.Create(ApiFixture.Unique(prefix), prefix, ItemType.Raw, Units.Pieces, false, true);
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item;
    });

    // A released WO (FAI required) of a new finished item with its own plan and checklist, 3 boards, operator logged in.
    private async Task<Setup> SetupAsync()
    {
        var (cap, res, mcu) = (await RawItemAsync("CAP"), await RawItemAsync("RES"), await RawItemAsync("MCU"));
        var setup = await api.WithDbAsync(async db =>
        {
            var board = Item.Create(ApiFixture.Unique("FAIT"), "FAI test board", ItemType.Finished, Units.Pieces, true, false);
            db.Items.Add(board);
            await db.SaveChangesAsync();
            var wc = await db.WorkCenters.ToDictionaryAsync(w => w.Code, w => w.Id);
            var line = await db.Stations.Where(st => st.Code == "L1-SMT-01").Select(st => st.LineId).SingleAsync();
            var wo = WorkOrder.Create(ApiFixture.Unique("WO-FAI"), board.Id, line, 3, T0, T0.AddHours(8), requireFai: true);
            wo.Release(1, 1, [new MaterialSnapshot(cap.Id, 10, 4m), new MaterialSnapshot(res.Id, 10, 2m), new MaterialSnapshot(mcu.Id, 20, 1m)],
                [new RoutingStepSnapshot(10, "SMT", wc["WC-SMT"], 30m, false, true, null),
                 new RoutingStepSnapshot(20, "ASSY", wc["WC-ASSY"], 60m, false, true, null),
                 new RoutingStepSnapshot(30, "TEST", wc["WC-TEST"], 45m, true, true, null),
                 new RoutingStepSnapshot(40, "PACK", wc["WC-PACK"], 20m, false, true, null)], T0);
            db.WorkOrders.Add(wo);
            var ng = await db.DefectCodes.Where(d => d.Code == MasterDataSeeder.OutOfSpecDefect).Select(d => d.Id).SingleAsync();
            var plan = InspectionPlan.ForStation(ApiFixture.Unique("FAIV"), "Rail of the FAI board", board.Id, "TEST", "V", 3.135m, 3.465m, 3.300m, ng);
            db.InspectionPlans.Add(plan);
            db.FaiTemplates.Add(new FaiTemplate(board.Id, 1,
            [
                new(nameof(FaiAutoCheck.BomMatch), "Components match the BOM", FaiCheckKind.Auto),
                new(nameof(FaiAutoCheck.TestPassed), "Passed every inspection step", FaiCheckKind.Auto),
                new(nameof(FaiAutoCheck.MeasurementsInSpec), "Every measurement in spec", FaiCheckKind.Auto),
                new("VISUAL", "Visual under the magnifier", FaiCheckKind.Manual),
                new("LABEL", "Label model and format", FaiCheckKind.Manual),
            ]));
            await db.SaveChangesAsync();
            var boards = Enumerable.Range(0, 3)
                .Select(_ => SerialFormat.Build("FAIT", new DateOnly(2026, 11, 4), Interlocked.Increment(ref _nextSerial))).ToList();
            db.Serials.AddRange(boards.Select(sn => new Serial(sn, wo.Id, board.Id)));
            await db.SaveChangesAsync();
            return (wo.Id, board.Id, plan.Code, boards);
        });
        foreach (var station in new[] { "L1-SMT-01", "L1-ASM-01", "L1-TEST-01" }) await scan.OpenSessionAsync(station, setup.Item1);
        return new Setup(setup.Item1, setup.Item2, setup.Item3, setup.Item4, [cap.Id, res.Id, mcu.Id], await api.LoginOperatorAsync());
    }

    private Task<Contracts.Execution.ScanResponse> ScanSmtAsync(Setup s, string board) => scan.ScanAsync(s.Op, "L1-SMT-01", board);

    // The sample (board 0) through SMT and ASSY with its parts traced, then TEST with 3.301 V through the inspection
    // use case (step 5), which scans it OK. swapFirstReelFor: the first SMT part comes from a reel of another item.
    private async Task SampleThroughTestAsync(Setup s, int? swapFirstReelFor = null, bool alreadyAtSmt = false)
    {
        var sample = s.Boards[0];
        if (!alreadyAtSmt) Assert.Equal("Ok", (await ScanSmtAsync(s, sample)).Result);
        Assert.Equal("Ok", (await scan.ScanAsync(s.Op, "L1-ASM-01", sample)).Result);
        await api.WithDbAsync(async db =>
        {
            var serial = await db.Serials.SingleAsync(x => x.SerialNo == sample);
            var scans = await db.ScanLogs.Where(l => l.SerialId == serial.Id).ToDictionaryAsync(l => l.Seq, l => (l.Id, l.StationId));
            var parts = new (int Item, int Seq)[] { (swapFirstReelFor ?? s.RawItems[0], 10), (s.RawItems[1], 10), (s.RawItems[2], 20) };
            foreach (var (item, seq) in parts)
            {
                var lot = new MaterialLot(ApiFixture.Unique("L"), item, 1_000, "SUP-T", T0, null, false);
                db.MaterialLots.Add(lot);
                await db.SaveChangesAsync();
                db.SerialComponents.Add(new SerialComponent(serial.Id, lot.Id, item, 1m, scans[seq].StationId, scans[seq].Id, T0));
            }
            return await db.SaveChangesAsync();
        });
        var testStation = await scan.StationIdAsync("L1-TEST-01");
        var inspected = await s.Op.PostAsJsonAsync("/api/quality/inspections",
            new RecordInspectionRequest(testStation, sample, [new InspectionValue(s.Plan, 3.301m)]));
        inspected.EnsureSuccessStatusCode();
        Assert.Equal("Ok", (await inspected.Content.ReadFromJsonAsync<InspectionResponse>())!.Result);
    }

    private static async Task<FaiWorksheetDto> WorksheetAsync(HttpClient client, int woId)
        => (await client.GetFromJsonAsync<FaiWorksheetDto>($"/api/fai/work-orders/{woId}"))!;

    private static Task<HttpResponseMessage> RecordAsync(HttpClient client, int woId, RecordFaiRequest r)
        => client.PostAsJsonAsync($"/api/fai/work-orders/{woId}/result", r);

    private Task<SerialStatus> SerialStatusAsync(string serialNo)
        => api.WithDbAsync(db => db.Serials.Where(x => x.SerialNo == serialNo).Select(x => x.Status).SingleAsync());

    // A kiosk of line L1: SignalR connection in the line group.
    private async Task<HubConnection> LineHubAsync(HttpClient client)
    {
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var server = api.Factory.Server;
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, RealtimeHub.Path.TrimStart('/')), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.WebSocketFactory = async (ctx, ct) =>
                {
                    var ws = server.CreateWebSocketClient();
                    ws.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
                    return await ws.ConnectAsync(ctx.Uri, ct);
                };
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        await hub.StartAsync();
        await hub.InvokeAsync(RealtimeHub.JoinLine, "L1");
        return hub;
    }

    private static Task<FaiStatusChangedDto> NextFaiPush(HubConnection hub)
    {
        var tcs = new TaskCompletionSource<FaiStatusChangedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<FaiStatusChangedDto>(nameof(IMachineClient.FaiStatusChanged), d => tcs.TrySetResult(d));
        return tcs.Task;
    }
}
