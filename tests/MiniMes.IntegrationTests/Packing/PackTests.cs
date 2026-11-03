using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Packing;
using MiniMes.Domain.Packing;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Execution;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Packing;

// Spec M3b step 3: POST /api/stations/{id}/pack on L1-PACK-01 (PCB-A: 10 per carton). Each test releases its own WO
// (routing 10 SMT, 20 ASSY, 30 TEST, 40 PACK); "passed Test" is set in SQL so a test does not scan 3 stations per board.
[Collection(ApiCollection.Name)]
public class PackTests(ApiFixture api)
{
    internal const string Pack = "L1-PACK-01";
    private readonly ScanTests scan = new(api);

    internal async Task<(int WoId, List<string> Serials)> PassedTestAsync(int qty)
    {
        var (wo, serials) = await scan.ReleasedAsync(qty);
        await api.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE dbo.Serials SET Status = 'InProcess', CurrentSeq = 30 WHERE WorkOrderId = {wo.Id}"));
        await scan.OpenSessionAsync(Pack, wo.Id);
        return (wo.Id, serials);
    }

    internal async Task<PackResponse> PackAsync(HttpClient client, string serialNo)
    {
        var response = await client.PostAsJsonAsync($"/api/stations/{await scan.StationIdAsync(Pack)}/pack", new PackRequest(serialNo));
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PackResponse>())!;
    }

    private sealed record CartonRow(int Id, string CartonNo, int Qty, CartonStatus Status, bool IsPartial, DateTime? ClosedAt, int LiveRows);

    private Task<List<CartonRow>> CartonsAsync(int woId) => api.WithDbAsync(db => db.Cartons
        .Where(c => c.WorkOrderId == woId).OrderBy(c => c.Id)
        .Select(c => new CartonRow(c.Id, c.CartonNo, c.Qty, c.Status, c.IsPartial, c.ClosedAt,
            db.CartonSerials.Count(cs => cs.CartonId == c.Id && cs.UnpackedAt == null)))
        .ToListAsync());

    [Fact]
    public async Task Twenty_five_boards_fill_two_cartons_and_leave_one_open_with_five()
    {
        var (woId, serials) = await PassedTestAsync(25);
        var op = await api.LoginOperatorAsync(stationCode: Pack);

        var answers = new List<PackResponse>();
        foreach (var s in serials) answers.Add(await PackAsync(op, s));

        Assert.All(answers, a => Assert.Equal(("Ok", false, false), (a.Result, a.Duplicate, a.Repacked)));
        Assert.Equal([9, 19], answers.Select((a, i) => (a, i)).Where(x => x.a.CartonClosed).Select(x => x.i));
        Assert.Equal(Enumerable.Range(0, 25).Select(i => i % 10 + 1), answers.Select(a => a.CartonQty));
        var cartons = await CartonsAsync(woId);
        Assert.Equal([(10, CartonStatus.Closed, 10), (10, CartonStatus.Closed, 10), (5, CartonStatus.Open, 5)],
            cartons.Select(c => (c.Qty, c.Status, c.LiveRows)));
        Assert.All(cartons, c => Assert.True(PackingNumberFormat.IsCartonNo(c.CartonNo), c.CartonNo));
        Assert.All(cartons.Take(2), c => Assert.Equal((false, true), (c.IsPartial, c.ClosedAt is not null)));
        Assert.Equal(answers.Select(a => a.CartonNo).Distinct(), cartons.Select(c => c.CartonNo));
        Assert.Equal(25, await api.WithDbAsync(db => db.Serials.CountAsync(s => s.WorkOrderId == woId && s.Status == SerialStatus.Completed)));
        Assert.Equal(WorkOrderStatus.Completed, await api.WithDbAsync(db => db.WorkOrders.Where(w => w.Id == woId).Select(w => w.Status).SingleAsync()));

        // The Packing screen: the open carton, newest board first.
        var status = await op.GetFromJsonAsync<PackingStatusDto>($"/api/stations/{await scan.StationIdAsync(Pack)}/packing-status?woId={woId}");
        Assert.Equal((woId, "PCB-A", (int?)10), (status!.WorkOrderId, status.ItemCode, status.QtyPerCarton));
        Assert.Equal((cartons[2].CartonNo, 5), (status.OpenCarton!.CartonNo, status.OpenCarton.Qty));
        Assert.Equal(serials[20..].AsEnumerable().Reverse(), status.OpenCarton.Serials.Select(s => s.SerialNo));
    }

    // P1: no shortcut into a carton for a board that has not passed Test. Nothing is written, not even a carton.
    [Fact]
    public async Task Board_that_skipped_test_is_blocked_and_goes_in_no_carton()
    {
        var (woId, serials) = await PassedTestAsync(2);
        await api.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE dbo.Serials SET CurrentSeq = 20 WHERE SerialNo = {serials[0]}"));

        var answer = await PackAsync(await api.LoginOperatorAsync(stationCode: Pack), serials[0]);

        Assert.Equal(("Blocked", "SkippedStep", (string?)null), (answer.Result, answer.ReasonCode, answer.CartonNo));
        Assert.Empty(await CartonsAsync(woId));
        Assert.Equal(0, await api.WithDbAsync(db => db.ScanLogs.CountAsync(l => l.SerialNo == serials[0])));
    }

    [Fact]
    public async Task Board_on_hold_is_blocked()
    {
        var (woId, serials) = await PassedTestAsync(1);
        await api.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE dbo.Serials SET Status = 'Hold' WHERE SerialNo = {serials[0]}"));

        var answer = await PackAsync(await api.LoginOperatorAsync(stationCode: Pack), serials[0]);

        Assert.Equal(("Blocked", "SerialHold"), (answer.Result, answer.ReasonCode));
        Assert.Empty(await CartonsAsync(woId));
    }

    // Spec 2.2 trap 1: the same board again (double trigger, or later) shows its carton; one scan, one row, Qty 1.
    [Fact]
    public async Task Packing_a_board_twice_shows_its_carton_and_counts_it_once()
    {
        var (woId, serials) = await PassedTestAsync(2);
        var op = await api.LoginOperatorAsync(stationCode: Pack);

        var first = await PackAsync(op, serials[0]);
        var again = await PackAsync(op, serials[0]);

        Assert.Equal(("Ok", false, 1), (first.Result, first.Duplicate, first.CartonQty));
        Assert.Equal(("Ok", true, first.CartonNo, 1), (again.Result, again.Duplicate, again.CartonNo, again.CartonQty));
        Assert.Equal([(1, CartonStatus.Open, 1)], (await CartonsAsync(woId)).Select(c => (c.Qty, c.Status, c.LiveRows)));
        Assert.Equal(1, await api.WithDbAsync(db => db.ScanLogs.CountAsync(l => l.SerialNo == serials[0])));
    }

    // Spec 2.3: carton at 9/10, two boards at once. Exactly one fills it; the other goes into a new carton (user
    // decision: retry, no "Busy"). No carton above 10. A few rounds, since one may be served in order by luck.
    [Fact]
    public async Task Two_boards_at_once_into_the_last_place_never_make_eleven()
    {
        var (a, b) = (await api.LoginOperatorAsync(stationCode: Pack), await api.LoginOperatorAsync(stationCode: Pack));
        for (var round = 0; round < 3; round++)
        {
            var (woId, serials) = await PassedTestAsync(11);
            foreach (var s in serials[..9]) await PackAsync(a, s);

            var answers = await ScanTests.TogetherAsync(() => PackAsync(a, serials[9]), () => PackAsync(b, serials[10]));

            Assert.All(answers, x => Assert.Equal("Ok", x.Result));
            Assert.Single(answers, x => x.CartonClosed);
            var cartons = await CartonsAsync(woId);
            Assert.Equal([(10, CartonStatus.Closed, 10), (1, CartonStatus.Open, 1)], cartons.Select(c => (c.Qty, c.Status, c.LiveRows)));
        }
    }

    // The test above is served in order by the database: both scans add 1 to the same PACK counter row, so the second
    // pack waits for the first to commit before it reads the carton. Boards that passed PACK already (repack) do not
    // touch the counter, so here the two conditional UPDATEs on the carton really race: one gets 0 rows and retries.
    [Fact]
    public async Task Two_repacks_at_once_into_the_last_place_never_make_eleven()
    {
        var (a, b) = (await api.LoginOperatorAsync(stationCode: Pack), await api.LoginOperatorAsync(stationCode: Pack));
        for (var round = 0; round < 5; round++)
        {
            var (woId, serials) = await PassedTestAsync(11);
            foreach (var s in serials[..9]) await PackAsync(a, s);
            await api.WithDbAsync(db => db.Database.ExecuteSqlAsync(
                $"UPDATE dbo.Serials SET Status = 'Completed', CurrentSeq = 40 WHERE SerialNo IN ({serials[9]}, {serials[10]})"));

            var answers = await ScanTests.TogetherAsync(() => PackAsync(a, serials[9]), () => PackAsync(b, serials[10]));

            Assert.All(answers, x => Assert.Equal(("Ok", true), (x.Result, x.Repacked)));
            Assert.Single(answers, x => x.CartonClosed);
            Assert.Equal([(10, CartonStatus.Closed, 10), (1, CartonStatus.Open, 1)],
                (await CartonsAsync(woId)).Select(c => (c.Qty, c.Status, c.LiveRows)));
        }
    }

    // P4: the first two boards of a WO at once open ONE carton (the second INSERT hits the filtered unique index).
    [Fact]
    public async Task Two_first_boards_at_once_share_one_new_carton()
    {
        var (a, b) = (await api.LoginOperatorAsync(stationCode: Pack), await api.LoginOperatorAsync(stationCode: Pack));
        for (var round = 0; round < 3; round++)
        {
            var (woId, serials) = await PassedTestAsync(2);

            var answers = await ScanTests.TogetherAsync(() => PackAsync(a, serials[0]), () => PackAsync(b, serials[1]));

            Assert.All(answers, x => Assert.Equal("Ok", x.Result));
            Assert.Equal([(2, CartonStatus.Open, 2)], (await CartonsAsync(woId)).Select(c => (c.Qty, c.Status, c.LiveRows)));
        }
    }

    // An unpacked board (Completed, no live carton) goes into a carton again without a second PACK scan (user
    // decision M3b step 3); the old row stays as history. Unpack itself is step 5: done in SQL here.
    [Fact]
    public async Task Unpacked_board_is_repacked_without_a_second_scan()
    {
        var (woId, serials) = await PassedTestAsync(2);
        var op = await api.LoginOperatorAsync(stationCode: Pack);
        var first = await PackAsync(op, serials[0]);
        await api.WithDbAsync(db => db.Database.ExecuteSqlAsync($"""
            UPDATE dbo.CartonSerials SET UnpackedAt = SYSUTCDATETIME(), UnpackedBy = 'sup01', UnpackReason = 'Wrong carton'
            WHERE SerialId = (SELECT Id FROM dbo.Serials WHERE SerialNo = {serials[0]});
            UPDATE dbo.Cartons SET Qty = Qty - 1 WHERE CartonNo = {first.CartonNo};
            """));

        var again = await PackAsync(op, serials[0]);

        Assert.Equal(("Ok", true, false, first.CartonNo, 1), (again.Result, again.Repacked, again.Duplicate, again.CartonNo, again.CartonQty));
        Assert.Equal(2, await api.WithDbAsync(db => db.CartonSerials.CountAsync(cs => db.Serials.Any(s => s.Id == cs.SerialId && s.SerialNo == serials[0]))));
        Assert.Equal(1, await api.WithDbAsync(db => db.ScanLogs.CountAsync(l => l.SerialNo == serials[0])));
        Assert.Equal(1, await api.WithDbAsync(db => db.WorkOrderOperations.Where(o => o.WorkOrderId == woId && o.Seq == 40).Select(o => o.QtyGood).SingleAsync()));
    }

    [Fact]
    public async Task Repacking_into_the_session_of_another_order_is_blocked()
    {
        var (_, serials) = await PassedTestAsync(1);
        var op = await api.LoginOperatorAsync(stationCode: Pack);
        await PackAsync(op, serials[0]);
        await api.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE dbo.CartonSerials SET UnpackedAt = SYSUTCDATETIME(), UnpackedBy = 'sup01', UnpackReason = 'Test' WHERE SerialId = (SELECT Id FROM dbo.Serials WHERE SerialNo = {serials[0]})"));
        await PassedTestAsync(1);   // the station now packs another WO

        var answer = await PackAsync(op, serials[0]);

        Assert.Equal(("Blocked", "WrongWo"), (answer.Result, answer.ReasonCode));
    }

    [Fact]
    public async Task Pack_needs_a_pack_station_and_an_operator()
    {
        var (_, serials) = await PassedTestAsync(1);
        var op = await api.LoginOperatorAsync(stationCode: Pack);

        var notPack = await op.PostAsJsonAsync($"/api/stations/{await scan.StationIdAsync("L1-TEST-01")}/pack", new PackRequest(serials[0]));
        var notFound = await op.PostAsJsonAsync("/api/stations/999999/pack", new PackRequest(serials[0]));
        var planner = await (await api.LoginAsync("planner01")).PostAsJsonAsync($"/api/stations/{await scan.StationIdAsync(Pack)}/pack", new PackRequest(serials[0]));
        var empty = await op.PostAsJsonAsync($"/api/stations/{await scan.StationIdAsync(Pack)}/pack", new PackRequest(""));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest),
            (notPack.StatusCode, notFound.StatusCode, planner.StatusCode, empty.StatusCode));
    }
}
