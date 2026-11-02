using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Material;
using MiniMes.Domain.Material;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Material;

// Spec M6 step 4 over HTTP, the situations of 1.3 A: load at the SMT station of L1 (WC-SMT) for a WO whose snapshot
// needs a capacitor at SMT (seq 10) and an MCU at ASSY (seq 20). Each test has its own items, so loads of other
// tests on the same station never collide (R6 is per station and item).
[Collection(ApiCollection.Name)]
public class LoadMaterialTests(ApiFixture api)
{
    private static readonly DateTime Received = new(2026, 9, 15, 0, 30, 0, DateTimeKind.Utc);

    private sealed record Setup(int StationId, int CapId, int McuId, int OtherId, HttpClient Operator);

    private async Task<Setup> SetupAsync()
    {
        var ids = await api.WithDbAsync(async db =>
        {
            Item NewItem(string prefix) => Item.Create(ApiFixture.Unique(prefix), prefix, ItemType.Raw, Units.Pieces, false, true);
            var (cap, mcu, other) = (NewItem("CAP"), NewItem("MCU"), NewItem("RES-10K"));
            db.Items.AddRange(cap, mcu, other);
            await db.SaveChangesAsync();
            var station = await db.Stations.SingleAsync(s => s.Code == "L1-SMT-01");
            var smt = await db.WorkCenters.SingleAsync(w => w.Code == "WC-SMT");
            var assy = await db.WorkCenters.SingleAsync(w => w.Code == "WC-ASSY");
            var pcbA = await db.Items.SingleAsync(i => i.Code == "PCB-A");
            var wo = WorkOrder.Create(ApiFixture.Unique("WO-LOAD"), pcbA.Id, station.LineId, 10, Received, Received.AddHours(8), requireFai: false);
            wo.Release(1, 1, [new MaterialSnapshot(cap.Id, 10, 4m), new MaterialSnapshot(mcu.Id, 20, 1m)],
                [new RoutingStepSnapshot(10, "SMT", smt.Id, 20m, false, true, null), new RoutingStepSnapshot(20, "ASSY", assy.Id, 60m, false, true, null)],
                Received);
            db.WorkOrders.Add(wo);
            await db.SaveChangesAsync();
            return (station.Id, cap.Id, mcu.Id, other.Id, wo.Id);
        });
        var op = await api.LoginOperatorAsync(stationCode: "L1-SMT-01");
        (await op.PostAsJsonAsync($"/api/stations/{ids.Item1}/sessions", new OpenSessionRequest(ids.Item5))).EnsureSuccessStatusCode();
        return new Setup(ids.Item1, ids.Item2, ids.Item3, ids.Item4, op);
    }

    // A lot straight in the table: the state each case needs (IQC passed or not, received when, expiring when).
    private Task<string> LotAsync(int itemId, DateTime? receivedAt = null, DateTime? expiry = null, bool pendingIqc = false, decimal qty = 10_000)
        => api.WithDbAsync(async db =>
        {
            var lot = new MaterialLot(ApiFixture.Unique("L"), itemId, qty, "SUP-T", receivedAt ?? Received, expiry, pendingIqc);
            db.MaterialLots.Add(lot);
            await db.SaveChangesAsync();
            return lot.LotNo;
        });

    private static string Url(Setup s) => $"/api/stations/{s.StationId}/material-loads";

    private static async Task<LoadMaterialResponse> LoadAsync(Setup s, string lotNo, string? badge = null, string? reason = null)
    {
        var response = await s.Operator.PostAsJsonAsync(Url(s), new LoadMaterialRequest(lotNo, "12", badge, reason));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoadMaterialResponse>())!;
    }

    private Task<List<(VerifyOutcome, VerifyCode, string?)>> LogAsync(string lotNo) => api.WithDbAsync(async db =>
        (await db.MaterialVerificationLogs.Where(l => l.ScannedLotNo == lotNo).OrderBy(l => l.Id)
            .Select(l => new { l.Outcome, l.Code, l.OverrideBy }).ToListAsync()).Select(l => (l.Outcome, l.Code, l.OverrideBy)).ToList());

    private Task<int> OpenLoadsAsync(int itemId) => api.WithDbAsync(db => db.MaterialLoads.CountAsync(l => l.ItemId == itemId && l.UnloadedAt == null));

    [Fact]
    public async Task Right_reel_goes_on_and_shows_in_the_checklist()
    {
        var s = await SetupAsync();
        var lot = await LotAsync(s.CapId);

        var answer = await LoadAsync(s, lot.ToLowerInvariant());   // scanners differ in case

        Assert.Equal(("Accepted", "Ok", lot, "12", "EMP00123"), (answer.Outcome, answer.Code, answer.Loaded!.LotNo, answer.Loaded.SlotNo, answer.Loaded.LoadedBy));
        var checklist = (await s.Operator.GetFromJsonAsync<List<StationMaterialDto>>(Url(s)))!;
        var cap = checklist.Single(r => r.ItemId == s.CapId);
        Assert.Equal((true, 4m, lot, 10_000m), (cap.Required, cap.QtyPerUnit, cap.LotNo, cap.RemainingQty));
        Assert.DoesNotContain(checklist, r => r.ItemId == s.McuId);   // the MCU is needed at ASSY, not here
        Assert.Equal([(VerifyOutcome.Accepted, VerifyCode.Ok, null)], await LogAsync(lot));
    }

    // 1.3 A #4 and friends: red, nothing loaded, the attempt logged (R13). No override for quality.
    public static TheoryData<string, string> Rejections() => new()
    {
        { "wrong item", nameof(VerifyCode.ItemNotInBom) },
        { "item of another step", nameof(VerifyCode.ItemNotForStation) },
        { "pending IQC", nameof(VerifyCode.LotNotIqcPassed) },
        { "expired", nameof(VerifyCode.LotExpired) },
        { "unknown label", nameof(VerifyCode.LotNotFound) },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task Wrong_reels_are_refused_and_logged(string scenario, string code)
    {
        var s = await SetupAsync();
        var lot = scenario switch
        {
            "wrong item" => await LotAsync(s.OtherId),
            "item of another step" => await LotAsync(s.McuId),
            "pending IQC" => await LotAsync(s.CapId, pendingIqc: true),
            "expired" => await LotAsync(s.CapId, expiry: DateTime.UtcNow.AddMinutes(1)),
            _ => ApiFixture.Unique("NO-SUCH-LOT"),
        };
        if (scenario == "expired") await api.WithDbAsync(db => db.MaterialLots.Where(l => l.LotNo == lot)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.ExpiryAt, DateTime.UtcNow.AddMinutes(-1))));

        var answer = await LoadAsync(s, lot, badge: "sup01", reason: "line is waiting");   // a supervisor cannot override these

        Assert.Equal(("Rejected", code), (answer.Outcome, answer.Code));
        Assert.Null(answer.Loaded);
        Assert.Equal(0, await OpenLoadsAsync(s.CapId) + await OpenLoadsAsync(s.McuId) + await OpenLoadsAsync(s.OtherId));
        Assert.Equal(VerifyOutcome.Rejected, Assert.Single(await LogAsync(lot)).Item1);
    }

    // 1.3 A #3: an older reel with stock -> yellow; the supervisor approves with a reason -> loaded, and both the load
    // and the log say who approved it.
    [Fact]
    public async Task Newer_reel_needs_a_supervisor_when_an_older_one_has_stock()
    {
        var s = await SetupAsync();
        var older = await LotAsync(s.CapId, receivedAt: Received.AddDays(-14));
        var newer = await LotAsync(s.CapId);

        var warning = await LoadAsync(s, newer);
        Assert.Equal(("Warning", "FifoOlderLotAvailable", older, (StationMaterialDto?)null), (warning.Outcome, warning.Code, warning.SuggestedLotNo, warning.Loaded));

        var wrongBadge = await s.Operator.PostAsJsonAsync(Url(s), new LoadMaterialRequest(newer, null, "EMP00123", "older reel is suspect"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongBadge.StatusCode);   // an operator cannot approve

        var approved = await LoadAsync(s, newer, badge: "SUP01", reason: "older reel is suspect");
        Assert.Equal(("Accepted", "sup01"), (approved.Outcome, approved.Loaded!.FifoOverrideBy));
        Assert.Equal([(VerifyOutcome.Warning, VerifyCode.FifoOlderLotAvailable, null), (VerifyOutcome.Warning, VerifyCode.FifoOlderLotAvailable, "sup01")],
            await LogAsync(newer));
        var load = await api.WithDbAsync(db => db.MaterialLoads.SingleAsync(l => l.ItemId == s.CapId && l.UnloadedAt == null));
        Assert.Equal("older reel is suspect", load.FifoOverrideReason);
    }

    // R6 + R10: the old reel runs out, the operator takes it off with 0 left (Consumed), the next reel goes on;
    // loading a reel over one that still has parts closes the old load (one open load per item).
    [Fact]
    public async Task Reels_are_replaced_one_open_load_per_item()
    {
        var s = await SetupAsync();
        var first = await LotAsync(s.CapId, receivedAt: Received.AddDays(-14));
        var second = await LotAsync(s.CapId);
        var third = await LotAsync(s.CapId, receivedAt: Received.AddDays(1));
        Assert.Equal("Accepted", (await LoadAsync(s, first)).Outcome);

        var loadId = (await LoadAsync(s, first)).Loaded!.LoadId!.Value;   // the same reel again: nothing changes
        var checklist = (await (await s.Operator.PostAsJsonAsync($"{Url(s)}/{loadId}/unload", new UnloadMaterialRequest(0))).Content
            .ReadFromJsonAsync<List<StationMaterialDto>>())!;
        Assert.Null(checklist.Single(r => r.ItemId == s.CapId).LotNo);
        Assert.Equal((MaterialLotStatus.Consumed, 0m), await api.WithDbAsync(db =>
            db.MaterialLots.Where(l => l.LotNo == first).Select(l => ValueTuple.Create(l.Status, l.RemainingQty)).SingleAsync()));

        Assert.Equal("Accepted", (await LoadAsync(s, second)).Outcome);          // the older reel is empty now: no FIFO warning
        Assert.Equal("Accepted", (await LoadAsync(s, third, "sup01", "test")).Outcome);   // second still has parts: approved
        Assert.Equal(1, await OpenLoadsAsync(s.CapId));
        Assert.Equal(3, await api.WithDbAsync(db => db.MaterialLoads.CountAsync(l => l.ItemId == s.CapId)));   // first, second, third: the same reel twice is one load
    }

    [Fact]
    public async Task Unload_refuses_more_than_received_and_a_second_unload()
    {
        var s = await SetupAsync();
        var lot = await LotAsync(s.CapId, qty: 100);
        var loadId = (await LoadAsync(s, lot)).Loaded!.LoadId!.Value;

        var tooMuch = await s.Operator.PostAsJsonAsync($"{Url(s)}/{loadId}/unload", new UnloadMaterialRequest(101));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.StatusCode);

        (await s.Operator.PostAsJsonAsync($"{Url(s)}/{loadId}/unload", new UnloadMaterialRequest(37))).EnsureSuccessStatusCode();
        var again = await s.Operator.PostAsJsonAsync($"{Url(s)}/{loadId}/unload", new UnloadMaterialRequest(null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal((MaterialLotStatus.Available, 37m), await api.WithDbAsync(db =>
            db.MaterialLots.Where(l => l.LotNo == lot).Select(l => ValueTuple.Create(l.Status, l.RemainingQty)).SingleAsync()));
    }

    // Two kiosks put two reels of the same item on the station at the same instant: R6 in the database, one 409.
    [Fact]
    public async Task Two_loads_of_the_same_item_at_once_leave_one_reel_on()
    {
        var s = await SetupAsync();
        var (a, b) = (await LotAsync(s.CapId), await LotAsync(s.CapId));

        var responses = await Task.WhenAll(
            s.Operator.PostAsJsonAsync(Url(s), new LoadMaterialRequest(a)), s.Operator.PostAsJsonAsync(Url(s), new LoadMaterialRequest(b)));

        Assert.Contains(HttpStatusCode.OK, responses.Select(r => r.StatusCode));
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Equal(1, await OpenLoadsAsync(s.CapId));
    }

    [Fact]
    public async Task Loading_needs_an_open_work_order_and_an_operator()
    {
        var s = await SetupAsync();
        var lot = await LotAsync(s.CapId);
        var qc = await api.LoginAsync("qc01");
        Assert.Equal(HttpStatusCode.Forbidden, (await qc.PostAsJsonAsync(Url(s), new LoadMaterialRequest(lot))).StatusCode);

        await api.WithDbAsync(async db =>
        {
            foreach (var open in await db.StationSessions.Where(x => x.StationId == s.StationId && x.ClosedAt == null).ToListAsync())
                open.Close(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Operator.PostAsJsonAsync(Url(s), new LoadMaterialRequest(lot))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Operator.PostAsJsonAsync("/api/stations/999999/material-loads", new LoadMaterialRequest(lot))).StatusCode);
    }
}
