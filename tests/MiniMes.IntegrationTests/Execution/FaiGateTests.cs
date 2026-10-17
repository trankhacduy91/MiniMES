using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Factory;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Execution;

// Spec M3 step 3b [v2]: while a WO's first article is pending, exactly one serial (the sample) runs the line,
// until QC records the result (temporary endpoint POST /api/work-orders/{id}/fai/result, replaced in M7).
[Collection(ApiCollection.Name)]
public class FaiGateTests(ApiFixture api)
{
    private readonly ScanTests scan = new(api);
    private const string SecondSmt = "L1-SMT-02";   // test-only station on WC-SMT, for two SMT stations at once

    [Fact]
    public async Task Only_the_sample_runs_until_qc_passes_it()
    {
        var (wo, serials) = await scan.ReleasedAsync(3, requireFai: true);
        var op = await api.LoginOperatorAsync();
        await scan.OpenSessionAsync("L1-SMT-01", wo.Id);

        var sample = await scan.ScanAsync(op, "L1-SMT-01", serials[0]);
        var second = await scan.ScanAsync(op, "L1-SMT-01", serials[1]);

        Assert.Equal("Ok", sample.Result);
        Assert.Equal(("Blocked", "FaiNotApproved"), (second.Result, second.ReasonCode));
        Assert.Equal((FaiStatus.Pending, serials[0]), await FaiAsync(wo.Id));
        Assert.Equal(0, await api.WithDbAsync(db => db.ScanLogs.CountAsync(l => l.SerialNo == serials[1])));

        // The sample must go through the whole line before QC can judge it.
        foreach (var station in new[] { "L1-ASM-01", "L1-TEST-01", "L1-PACK-01" })
        {
            await scan.OpenSessionAsync(station, wo.Id);
            Assert.Equal("Ok", (await scan.ScanAsync(op, station, serials[0])).Result);
        }
        var judged = await FaiResultAsync(await api.LoginAsync("qc01"), wo.Id, passed: true);

        Assert.Equal(HttpStatusCode.OK, judged.StatusCode);
        Assert.Equal("Passed", (await judged.Content.ReadFromJsonAsync<WorkOrderDetailDto>())!.FaiStatus);
        Assert.Equal("Ok", (await scan.ScanAsync(op, "L1-SMT-01", serials[1])).Result);
        Assert.Equal("Ok", (await scan.ScanAsync(op, "L1-SMT-01", serials[2])).Result);   // the whole lot now
    }

    // FAI failed (M2 RecordFaiResult(false)): the WO stays Pending and the next board scanned at SMT is the new sample.
    [Fact]
    public async Task A_failed_sample_lets_the_next_board_become_the_sample()
    {
        var (wo, serials) = await scan.ReleasedAsync(3, requireFai: true);
        var op = await api.LoginOperatorAsync();
        await scan.OpenSessionAsync("L1-SMT-01", wo.Id);
        await scan.ScanAsync(op, "L1-SMT-01", serials[0]);

        var judged = await FaiResultAsync(await api.LoginAsync("qc01"), wo.Id, passed: false);
        var next = await scan.ScanAsync(op, "L1-SMT-01", serials[1]);
        var third = await scan.ScanAsync(op, "L1-SMT-01", serials[2]);

        Assert.Equal(HttpStatusCode.OK, judged.StatusCode);
        Assert.Equal("Ok", next.Result);
        Assert.Equal((FaiStatus.Pending, serials[1]), await FaiAsync(wo.Id));
        Assert.Equal("FaiNotApproved", third.ReasonCode);
    }

    // [v2] Rule 6 race: two SMT stations scan two different boards the moment the WO is released. The conditional
    // UPDATE (... WHERE FaiSerialNo IS NULL) lets exactly one become the sample. Several rounds to hit the overlap.
    [Fact]
    public async Task Two_smt_stations_at_once_make_exactly_one_sample()
    {
        await EnsureSecondSmtStationAsync();
        var op = await api.LoginOperatorAsync();
        for (var round = 0; round < 5; round++)
        {
            var (wo, serials) = await scan.ReleasedAsync(2, requireFai: true);
            await scan.OpenSessionAsync("L1-SMT-01", wo.Id);
            await scan.OpenSessionAsync(SecondSmt, wo.Id);

            var answers = await ScanTests.TogetherAsync(
                () => scan.ScanAsync(op, "L1-SMT-01", serials[0]),
                () => scan.ScanAsync(op, SecondSmt, serials[1]));

            var winner = Assert.Single(answers, a => a.Result == "Ok");
            Assert.Single(answers, a => a.ReasonCode == "FaiNotApproved");
            var (status, sampleNo) = await FaiAsync(wo.Id);
            Assert.Equal(FaiStatus.Pending, status);
            Assert.Equal(serials[answers[0] == winner ? 0 : 1], sampleNo);
            Assert.Equal(1, await api.WithDbAsync(db => db.ScanLogs.CountAsync(l => l.WorkOrderId == wo.Id)));
            Assert.Equal(1, await api.WithDbAsync(db => db.WorkOrderOperations
                .Where(o => o.WorkOrderId == wo.Id && o.Seq == 10).Select(o => o.QtyGood).SingleAsync()));
        }
    }

    [Fact]
    public async Task Fai_result_needs_a_sample_and_the_qc_role()
    {
        var (wo, _) = await scan.ReleasedAsync(1, requireFai: true);
        var (noFai, _) = await scan.ReleasedAsync(1);

        var noSample = await FaiResultAsync(await api.LoginAsync("qc01"), wo.Id, passed: true);
        var notPending = await FaiResultAsync(await api.LoginAsync("qc01"), noFai.Id, passed: true);
        var planner = await FaiResultAsync(await api.LoginAsync("planner01"), wo.Id, passed: true);
        var unknown = await FaiResultAsync(await api.LoginAsync("qc01"), int.MaxValue, passed: true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noSample.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notPending.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, planner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ---------- helpers ----------

    private static Task<HttpResponseMessage> FaiResultAsync(HttpClient client, int woId, bool passed)
        => client.PostAsJsonAsync($"/api/work-orders/{woId}/fai/result", new FaiResultRequest(passed));

    private Task<(FaiStatus?, string?)> FaiAsync(int woId)
        => api.WithDbAsync(async db => await db.WorkOrders.Where(w => w.Id == woId)
            .Select(w => new ValueTuple<FaiStatus?, string?>(w.FaiStatus, w.FaiSerialNo)).SingleAsync());

    private Task<int> EnsureSecondSmtStationAsync() => api.WithDbAsync(async db =>
    {
        if (await db.Stations.AnyAsync(s => s.Code == SecondSmt)) return 0;
        var line = await db.Lines.Include(l => l.Stations).SingleAsync(l => l.Code == "L1");
        var wc = await db.WorkCenters.SingleAsync(w => w.Code == "WC-SMT");
        line.AddStation(SecondSmt, "SMT 2", StationType.Smt).AssignWorkCenter(wc.Id, wc.LineId);
        return await db.SaveChangesAsync();
    });
}
