using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Infrastructure;
using MiniMes.IntegrationTests.Planning;

namespace MiniMes.IntegrationTests.Execution;

// Spec M3 step 3: POST /api/stations/{id}/scan end to end on SQL Server, seeded Line 1 stations
// (L1-SMT-01 -> WC-SMT 10, L1-ASM-01 -> WC-ASSY 20, L1-TEST-01 -> WC-TEST 30, L1-PACK-01 -> WC-PACK 40, L1-REPAIR).
// Station sessions are opened directly in the database until step 5 adds their API.
[Collection(ApiCollection.Name)]
public class ScanTests(ApiFixture api)
{
    private static readonly DateTime Start = new(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_board_goes_through_the_four_stations_and_completes_the_order()
    {
        var (wo, serials) = await ReleasedAsync(1);
        var op = await api.LoginOperatorAsync();
        var board = serials[0];

        var answers = new List<ScanResponse>();
        foreach (var station in new[] { "L1-SMT-01", "L1-ASM-01", "L1-TEST-01", "L1-PACK-01" })
        {
            await OpenSessionAsync(station, wo.Id);
            answers.Add(await ScanAsync(op, station, board));
        }

        Assert.All(answers, a => Assert.Equal(("Ok", false), (a.Result, a.Duplicate)));
        Assert.Equal([20, 30, 40, null], answers.Select(a => a.NextStep));
        Assert.Equal("OK - board completed.", answers[^1].Message);
        var serial = await api.WithDbAsync(db => db.Serials.SingleAsync(s => s.SerialNo == board));
        Assert.Equal((SerialStatus.Completed, 40), (serial.Status, serial.CurrentSeq));
        Assert.Equal([10, 20, 30, 40], await api.WithDbAsync(db => db.ScanLogs.Where(l => l.SerialNo == board).OrderBy(l => l.Id).Select(l => l.Seq).ToListAsync()));
        var after = await api.WithDbAsync(db => db.WorkOrders.Include(w => w.Operations).SingleAsync(w => w.Id == wo.Id));
        Assert.All(after.Operations, o => Assert.Equal((1, 0), (o.QtyGood, o.QtyNg)));
        Assert.Equal(WorkOrderStatus.Completed, after.Status);   // 1 of 1 good at the last step (M2 rule R10)
        var history = await api.WithDbAsync(db => db.AuditLogs
            .Where(a => a.TableName == "WorkOrders" && a.RecordKey == wo.Id.ToString() && a.ColumnName == "Status")
            .OrderBy(a => a.Id).Select(a => a.OldValue + ">" + a.NewValue + " by " + a.ChangedBy).ToListAsync());
        Assert.Equal(["Draft>Released by planner01", "Released>InProgress by EMP00123", "InProgress>Completed by EMP00123"], history);
    }

    [Fact]
    public async Task Skipping_a_step_is_blocked_and_writes_nothing()
    {
        var (wo, serials) = await ReleasedAsync(2);
        await OpenSessionAsync("L1-TEST-01", wo.Id);

        var answer = await ScanAsync(await api.LoginOperatorAsync(), "L1-TEST-01", serials[0]);

        Assert.Equal(("Blocked", "SkippedStep"), (answer.Result, answer.ReasonCode));
        Assert.Contains("10 SMT", answer.Message);
        Assert.Equal(0, await ScanLogCountAsync(serials[0]));
        var serial = await api.WithDbAsync(db => db.Serials.SingleAsync(s => s.SerialNo == serials[0]));
        Assert.Equal((SerialStatus.Created, 0), (serial.Status, serial.CurrentSeq));
        Assert.Equal(WorkOrderStatus.Released, await WoStatusAsync(wo.Id));   // a blocked scan does not start the order
    }

    [Fact]
    public async Task Double_trigger_is_answered_as_duplicate_and_logged_once()
    {
        var (wo, serials) = await ReleasedAsync(2);
        var op = await api.LoginOperatorAsync();
        await OpenSessionAsync("L1-SMT-01", wo.Id);

        var first = await ScanAsync(op, "L1-SMT-01", serials[0]);
        var second = await ScanAsync(op, "L1-SMT-01", serials[0]);

        Assert.Equal(("Ok", false), (first.Result, first.Duplicate));
        Assert.Equal(("Ok", true, (int?)20), (second.Result, second.Duplicate, second.NextStep));
        Assert.Equal(1, await ScanLogCountAsync(serials[0]));
        Assert.Equal(1, await QtyGoodAsync(wo.Id, 10));
    }

    // Two requests for the same board at the same moment: neither has seen the other, both pass route check;
    // the serial's RowVersion lets one write, the other re-reads and answers "duplicate". One row, one count.
    [Fact]
    public async Task Two_simultaneous_scans_of_one_board_record_it_once()
    {
        var (wo, serials) = await ReleasedAsync(1);
        await OpenSessionAsync("L1-SMT-01", wo.Id);
        var (a, b) = (await api.LoginOperatorAsync(), await api.LoginOperatorAsync());

        var answers = await TogetherAsync(() => ScanAsync(a, "L1-SMT-01", serials[0]), () => ScanAsync(b, "L1-SMT-01", serials[0]));

        Assert.All(answers, x => Assert.Equal("Ok", x.Result));
        Assert.Single(answers, x => x.Duplicate);
        Assert.Equal(1, await ScanLogCountAsync(serials[0]));
        Assert.Equal(1, await QtyGoodAsync(wo.Id, 10));
    }

    // First scans of two boards at once on a Released order: both start it; one Start wins, the other retries.
    [Fact]
    public async Task Two_first_scans_at_once_both_succeed_and_start_the_order_once()
    {
        var (wo, serials) = await ReleasedAsync(2);
        await OpenSessionAsync("L1-SMT-01", wo.Id);
        var (a, b) = (await api.LoginOperatorAsync(), await api.LoginOperatorAsync());

        var answers = await TogetherAsync(() => ScanAsync(a, "L1-SMT-01", serials[0]), () => ScanAsync(b, "L1-SMT-01", serials[1]));

        Assert.All(answers, x => Assert.Equal(("Ok", false), (x.Result, x.Duplicate)));
        Assert.Equal(WorkOrderStatus.InProgress, await WoStatusAsync(wo.Id));
        Assert.Equal(2, await QtyGoodAsync(wo.Id, 10));
    }

    [Fact]
    public async Task Good_count_of_a_step_equals_its_ok_scans()
    {
        var (wo, serials) = await ReleasedAsync(5);
        var op = await api.LoginOperatorAsync();
        await OpenSessionAsync("L1-SMT-01", wo.Id);

        foreach (var s in serials) await ScanAsync(op, "L1-SMT-01", s);

        var logged = await api.WithDbAsync(db => db.ScanLogs.CountAsync(l => l.WorkOrderId == wo.Id && l.Seq == 10 && l.Result == ScanResult.Ok));
        Assert.Equal((5, 5), (logged, await QtyGoodAsync(wo.Id, 10)));
    }

    [Fact]
    public async Task Ng_at_test_sends_the_board_to_repair()
    {
        var (wo, serials) = await ReleasedAsync(1);
        var op = await api.LoginOperatorAsync();
        var board = serials[0];
        foreach (var station in new[] { "L1-SMT-01", "L1-ASM-01" }) { await OpenSessionAsync(station, wo.Id); await ScanAsync(op, station, board); }
        await OpenSessionAsync("L1-TEST-01", wo.Id);

        var ng = await ScanAsync(op, "L1-TEST-01", board, "NG", "SLD-BRG");
        await Task.Delay(TimeSpan.FromSeconds(1));
        var retest = await ScanAsync(op, "L1-TEST-01", board);   // OK this time: a different result, not a duplicate
        await OpenSessionAsync("L1-REPAIR", wo.Id);
        var checkIn = await ScanAsync(op, "L1-REPAIR", board);

        Assert.Equal(("Ng", (int?)null), (ng.Result, ng.NextStep));
        Assert.Equal(("Blocked", "MustGoRepair"), (retest.Result, retest.ReasonCode));
        Assert.Equal("Ok", checkIn.Result);
        var serial = await api.WithDbAsync(db => db.Serials.SingleAsync(s => s.SerialNo == board));
        Assert.Equal((SerialStatus.Repair, 20), (serial.Status, serial.CurrentSeq));
        Assert.Equal(1, await api.WithDbAsync(db => db.WorkOrderOperations.Where(o => o.WorkOrderId == wo.Id && o.Seq == 30).Select(o => o.QtyNg).SingleAsync()));
        var logs = await api.WithDbAsync(db => db.ScanLogs.Where(l => l.SerialNo == board).OrderBy(l => l.Id).Select(l => new { l.Seq, l.Result, l.DefectCode }).ToListAsync());
        Assert.Equal((30, ScanResult.Ng, "SLD-BRG"), (logs[2].Seq, logs[2].Result, logs[2].DefectCode));
        Assert.Equal((0, ScanResult.Ok), (logs[3].Seq, logs[3].Result));   // Repair check-in: off-routing, Seq 0
    }

    [Theory]
    [InlineData("L1-TEST-01", "NG", null, "DefectCode")]       // NG without a code
    [InlineData("L1-TEST-01", "NG", "NOPE", "DefectCode")]     // unknown code
    [InlineData("L1-TEST-01", "OK", "SLD-BRG", "DefectCode")]  // OK with a code
    [InlineData("L1-TEST-01", "MAYBE", null, "Result")]
    [InlineData("L1-REPAIR", "NG", "SLD-BRG", "Result")]       // Repair scans are check-ins
    public async Task Malformed_scan_is_400_on_the_field(string station, string result, string? defect, string field)
    {
        var (wo, serials) = await ReleasedAsync(1);
        await OpenSessionAsync(station, wo.Id);

        var response = await (await api.LoginOperatorAsync()).PostAsJsonAsync($"/api/stations/{await StationIdAsync(station)}/scan",
            new ScanRequest(serials[0], result, defect));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Station_without_an_open_session_refuses_scans()
    {
        var (_, serials) = await ReleasedAsync(1);
        await CloseSessionsAsync("L1-PACK-01");

        var answer = await ScanAsync(await api.LoginOperatorAsync(), "L1-PACK-01", serials[0]);

        Assert.Equal(("Blocked", "NoOpenSession"), (answer.Result, answer.ReasonCode));
    }

    [Fact]
    public async Task Board_of_another_order_is_blocked()
    {
        var (running, _) = await ReleasedAsync(1);
        var (_, otherSerials) = await ReleasedAsync(1);
        await OpenSessionAsync("L1-SMT-01", running.Id);

        var answer = await ScanAsync(await api.LoginOperatorAsync(), "L1-SMT-01", otherSerials[0]);

        Assert.Equal("WrongWo", answer.ReasonCode);
    }

    [Theory]
    [InlineData("PCBA2610170000048")]   // check digit wrong: refused before any lookup
    [InlineData("ZZZZ2610179999995")]   // valid format, no such board
    public async Task Unknown_serial_is_blocked(string serialNo)
    {
        var (wo, _) = await ReleasedAsync(1);
        await OpenSessionAsync("L1-SMT-01", wo.Id);
        Assert.Equal("ZZZZ2610179999995", SerialFormat.Build("ZZZZ", new DateOnly(2026, 10, 17), 999999));   // the test data itself is a valid serial

        var answer = await ScanAsync(await api.LoginOperatorAsync(), "L1-SMT-01", serialNo);

        Assert.Equal(("Blocked", "SerialNotFound"), (answer.Result, answer.ReasonCode));
    }

    [Fact]
    public async Task Lower_case_and_spaces_from_the_scanner_are_accepted()
    {
        var (wo, serials) = await ReleasedAsync(1);
        await OpenSessionAsync("L1-SMT-01", wo.Id);

        var answer = await ScanAsync(await api.LoginOperatorAsync(), "L1-SMT-01", $"  {serials[0].ToLowerInvariant()} ");

        Assert.Equal("Ok", answer.Result);
    }

    [Fact]
    public async Task Unknown_station_is_404_and_a_planner_cannot_scan()
    {
        var notFound = await (await api.LoginOperatorAsync()).PostAsJsonAsync("/api/stations/999999/scan", new ScanRequest("PCBA2610170000049"));
        var planner = await (await api.LoginAsync("planner01")).PostAsJsonAsync($"/api/stations/{await StationIdAsync("L1-SMT-01")}/scan",
            new ScanRequest("PCBA2610170000049"));

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, planner.StatusCode);
    }

    // ---------- helpers ----------

    internal async Task<(WorkOrderDetailDto Wo, List<string> Serials)> ReleasedAsync(int qty, bool requireFai = false)
    {
        var planner = await api.LoginAsync("planner01");
        var pcbA = await api.WithDbAsync(db => db.Items.Where(i => i.Code == "PCB-A").Select(i => i.Id).SingleAsync());
        var draft = await WorkOrderApiTests.CreateAsync(planner,
            new CreateWorkOrderRequest(pcbA, LineId: 1, qty, Start, Start.AddHours(4), RequireFai: requireFai));
        var wo = await ReleaseWorkOrderTests.ReleaseOkAsync(planner, draft);
        var serials = await api.WithDbAsync(db => db.Serials.Where(s => s.WorkOrderId == wo.Id).OrderBy(s => s.SerialNo).Select(s => s.SerialNo).ToListAsync());
        return (wo, serials);
    }

    internal Task<int> StationIdAsync(string code) => api.WithDbAsync(db => db.Stations.Where(s => s.Code == code).Select(s => s.Id).SingleAsync());

    // One open session per station: close whatever another test left open, then open ours.
    internal async Task OpenSessionAsync(string stationCode, int woId)
    {
        var stationId = await StationIdAsync(stationCode);
        await api.WithDbAsync(async db =>
        {
            foreach (var open in await db.StationSessions.Where(s => s.StationId == stationId && s.ClosedAt == null).ToListAsync())
                open.Close(DateTime.UtcNow);
            await db.SaveChangesAsync();
            db.StationSessions.Add(new StationSession(stationId, woId, "EMP00123", DateTime.UtcNow));
            return await db.SaveChangesAsync();
        });
    }

    private async Task CloseSessionsAsync(string stationCode)
    {
        var stationId = await StationIdAsync(stationCode);
        await api.WithDbAsync(async db =>
        {
            foreach (var open in await db.StationSessions.Where(s => s.StationId == stationId && s.ClosedAt == null).ToListAsync())
                open.Close(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });
    }

    internal async Task<ScanResponse> ScanAsync(HttpClient client, string stationCode, string serialNo, string result = "OK", string? defect = null)
    {
        var response = await client.PostAsJsonAsync($"/api/stations/{await StationIdAsync(stationCode)}/scan", new ScanRequest(serialNo, result, defect));
        Assert.True(response.IsSuccessStatusCode, $"{stationCode}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ScanResponse>())!;
    }

    internal static async Task<T[]> TogetherAsync<T>(params Func<Task<T>>[] calls)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = calls.Select(call => Task.Run(async () => { await gate.Task; return await call(); })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private Task<int> ScanLogCountAsync(string serialNo) => api.WithDbAsync(db => db.ScanLogs.CountAsync(l => l.SerialNo == serialNo));

    private Task<int> QtyGoodAsync(int woId, int seq)
        => api.WithDbAsync(db => db.WorkOrderOperations.Where(o => o.WorkOrderId == woId && o.Seq == seq).Select(o => o.QtyGood).SingleAsync());

    private Task<WorkOrderStatus> WoStatusAsync(int woId) => api.WithDbAsync(db => db.WorkOrders.Where(w => w.Id == woId).Select(w => w.Status).SingleAsync());
}
