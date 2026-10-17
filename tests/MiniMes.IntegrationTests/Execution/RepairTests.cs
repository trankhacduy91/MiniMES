using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Execution;

// Spec M3 step 4: Repair (POST /api/stations/{id}/repair), Rework (ReturnToSeq) and Scrap (POST /api/serials/{no}/scrap).
[Collection(ApiCollection.Name)]
public class RepairTests(ApiFixture api)
{
    private readonly ScanTests scan = new(api);

    // Demo scenario, step 5 of spec-v2 (09:15): NG at Test -> Test blocked -> Repair check-in -> repaired
    // -> Test OK -> Pack -> Completed.
    [Fact]
    public async Task Ng_board_is_repaired_retested_and_completed()
    {
        var (wo, board, op) = await FailedAtTestAsync();
        var blocked = await scan.ScanAsync(op, "L1-TEST-01", board);
        await scan.OpenSessionAsync("L1-REPAIR", wo.Id);
        await scan.ScanAsync(op, "L1-REPAIR", board);

        var repaired = await RepairAsync(op, new RepairRequest(board, "sld-brg", "Reflow solder joint U3"));
        var retest = await scan.ScanAsync(op, "L1-TEST-01", board);
        await scan.OpenSessionAsync("L1-PACK-01", wo.Id);
        var pack = await scan.ScanAsync(op, "L1-PACK-01", board);

        Assert.Equal("MustGoRepair", blocked.ReasonCode);
        Assert.Equal(HttpStatusCode.OK, repaired.StatusCode);
        Assert.Equal(new RepairResponse(board, "InProcess", 30, "TEST"), await repaired.Content.ReadFromJsonAsync<RepairResponse>());
        Assert.Equal(("Ok", (int?)40), (retest.Result, retest.NextStep));
        Assert.Equal("OK - board completed.", pack.Message);
        var log = await api.WithDbAsync(db => db.RepairLogs.SingleAsync(r => r.SerialNo == board));
        Assert.Equal(("SLD-BRG", "Reflow solder joint U3", (string?)null, (int?)null, "EMP00123"),
            (log.DefectCode, log.Action, log.ComponentReplaced, log.ReturnToSeq, log.RepairedBy));
        var test = await api.WithDbAsync(db => db.WorkOrderOperations.SingleAsync(o => o.WorkOrderId == wo.Id && o.Seq == 30));
        Assert.Equal((1, 1), (test.QtyGood, test.QtyNg));   // the first-pass NG stays counted (FPY in step 9)
    }

    // Spec case 4d: rework from SMT. Test is blocked (SMT, ASSY again first); SMT accepts the board.
    [Fact]
    public async Task Rework_sends_the_board_back_to_an_earlier_step()
    {
        var (wo, board, op) = await FailedAtTestAsync();
        await scan.OpenSessionAsync("L1-REPAIR", wo.Id);
        await scan.ScanAsync(op, "L1-REPAIR", board);

        var repaired = await RepairAsync(op, new RepairRequest(board, "SLD-BRG", "Replace U3", "U3 IC-MCU-QFN lot L2610-07", ReturnToSeq: 10));
        var test = await scan.ScanAsync(op, "L1-TEST-01", board);
        // The SMT scan of this board is seconds old: inside the duplicate window it would be answered "duplicate".
        // A real rework takes minutes; the test waits the window out.
        await Task.Delay(TimeSpan.FromSeconds(5.5));
        var smt = await scan.ScanAsync(op, "L1-SMT-01", board);

        Assert.Equal(new RepairResponse(board, "InProcess", 10, "SMT"), await repaired.Content.ReadFromJsonAsync<RepairResponse>());
        Assert.Equal(("Blocked", "SkippedStep"), (test.Result, test.ReasonCode));
        Assert.Equal(("Ok", false, (int?)20), (smt.Result, smt.Duplicate, smt.NextStep));
        var log = await api.WithDbAsync(db => db.RepairLogs.SingleAsync(r => r.SerialNo == board));
        Assert.Equal(("U3 IC-MCU-QFN lot L2610-07", (int?)10), (log.ComponentReplaced, log.ReturnToSeq));
    }

    [Fact]
    public async Task Repair_refuses_boards_not_checked_in_and_wrong_steps()
    {
        var (wo, board, op) = await FailedAtTestAsync();

        var notCheckedIn = await RepairAsync(op, new RepairRequest(board, "SLD-BRG", "Reflow"));
        await scan.OpenSessionAsync("L1-REPAIR", wo.Id);
        await scan.ScanAsync(op, "L1-REPAIR", board);
        var forward = await RepairAsync(op, new RepairRequest(board, "SLD-BRG", "Reflow", ReturnToSeq: 40));
        var notRepairStation = await op.PostAsJsonAsync($"/api/stations/{await scan.StationIdAsync("L1-TEST-01")}/repair",
            new RepairRequest(board, "SLD-BRG", "Reflow"));
        var unknownSerial = await RepairAsync(op, new RepairRequest("PCBA2610179999995", "SLD-BRG", "Reflow"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, notCheckedIn.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forward.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notRepairStation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownSerial.StatusCode);
        Assert.Equal(SerialStatus.Repair, await StatusAsync(board));
        Assert.False(await api.WithDbAsync(db => db.RepairLogs.AnyAsync(r => r.SerialNo == board)));
    }

    [Theory]
    [InlineData("", "SLD-BRG", "Reflow", null, "SerialNo")]
    [InlineData("PCBA2610170000011", "NOPE", "Reflow", null, "DefectCode")]
    [InlineData("PCBA2610170000011", "SLD-BRG", "", null, "Action")]
    [InlineData("PCBA2610170000011", "SLD-BRG", "Replace U3", null, "ComponentReplaced")]   // spec 1.5
    [InlineData("PCBA2610170000011", "SLD-BRG", "Reflow", 0, "ReturnToSeq")]
    public async Task Malformed_repair_is_400_on_the_field(string serialNo, string defect, string action, int? returnToSeq, string field)
    {
        var response = await RepairAsync(await api.LoginOperatorAsync(), new RepairRequest(serialNo, defect, action, null, returnToSeq));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
    }

    // Two "complete" taps on the same board: one repair log, the other is refused (409 overlap / 422 after).
    [Fact]
    public async Task Double_complete_writes_one_repair_log()
    {
        var (wo, board, op) = await FailedAtTestAsync();
        await scan.OpenSessionAsync("L1-REPAIR", wo.Id);
        await scan.ScanAsync(op, "L1-REPAIR", board);

        var answers = await ScanTests.TogetherAsync(
            () => RepairAsync(op, new RepairRequest(board, "SLD-BRG", "Reflow")),
            () => RepairAsync(op, new RepairRequest(board, "SLD-BRG", "Reflow")));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Single(answers, a => a.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity);
        Assert.Equal(1, await api.WithDbAsync(db => db.RepairLogs.CountAsync(r => r.SerialNo == board)));
    }

    [Fact]
    public async Task Supervisor_scraps_a_board_and_route_check_blocks_it()
    {
        var (_, board, op) = await FailedAtTestAsync();
        var supervisor = await api.LoginAsync("sup01");

        var byOperator = await ScrapAsync(op, board, "Burnt pad");
        var scrapped = await ScrapAsync(supervisor, board.ToLowerInvariant(), "Burnt pad U3, not repairable");
        var again = await ScrapAsync(supervisor, board, "Again");
        var noReason = await ScrapAsync(supervisor, board, "");
        var unknown = await ScrapAsync(supervisor, "PCBA2610179999995", "Burnt");
        var rescan = await scan.ScanAsync(op, "L1-TEST-01", board);

        Assert.Equal(HttpStatusCode.Forbidden, byOperator.StatusCode);
        Assert.Equal(HttpStatusCode.OK, scrapped.StatusCode);
        Assert.Equal("Scrapped", (await scrapped.Content.ReadFromJsonAsync<SerialDto>())!.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("SerialScrapped", rescan.ReasonCode);
        Assert.Equal("Burnt pad U3, not repairable", await api.WithDbAsync(db => db.Serials.Where(s => s.SerialNo == board).Select(s => s.ScrapReason).SingleAsync()));
    }

    // ---------- helpers ----------

    // A 1-board WO whose board passed SMT and ASSY and failed at TEST (SLD-BRG).
    private async Task<(WorkOrderDetailDto Wo, string Board, HttpClient Op)> FailedAtTestAsync()
    {
        var (wo, serials) = await scan.ReleasedAsync(1);
        var op = await api.LoginOperatorAsync();
        var board = serials[0];
        foreach (var station in new[] { "L1-SMT-01", "L1-ASM-01", "L1-TEST-01" })
        {
            await scan.OpenSessionAsync(station, wo.Id);
            var result = station == "L1-TEST-01" ? "NG" : "OK";
            await scan.ScanAsync(op, station, board, result, result == "NG" ? "SLD-BRG" : null);
        }
        return (wo, board, op);
    }

    private async Task<HttpResponseMessage> RepairAsync(HttpClient client, RepairRequest request)
        => await client.PostAsJsonAsync($"/api/stations/{await scan.StationIdAsync("L1-REPAIR")}/repair", request);

    private static Task<HttpResponseMessage> ScrapAsync(HttpClient client, string serialNo, string reason)
        => client.PostAsJsonAsync($"/api/serials/{serialNo}/scrap", new ScrapRequest(reason));

    private Task<SerialStatus> StatusAsync(string serialNo)
        => api.WithDbAsync(db => db.Serials.Where(s => s.SerialNo == serialNo).Select(s => s.Status).SingleAsync());
}
