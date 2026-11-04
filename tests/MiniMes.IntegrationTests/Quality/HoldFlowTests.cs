using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MiniMes.Application.Material;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Material;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;
using MiniMes.IntegrationTests.Execution;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Quality;

// Spec M7 step 10 / DoD 5.1: "bad reel -> trace -> Hold -> the held boards are stopped at the station". One chain,
// every link through the real path: the reel is loaded at SMT through the M6 API with the material gate on, the scans
// write the SerialComponents (no row is written by hand), the hold goes through the M7 API, the stations answer
// through the M3 scan. Then Release gives every board its own status back and the line goes on.
// The gate is shared by the test host (M3-M5 tests load no reels): turned on here, back off in Dispose, like
// ConsumptionTests (tests of one collection never run together).
[Collection(ApiCollection.Name)]
public sealed class HoldFlowTests : IDisposable
{
    private static readonly DateTime Received = new(2026, 11, 4, 0, 30, 0, DateTimeKind.Utc);

    private readonly ApiFixture api;
    private readonly ScanTests scan;
    private readonly HoldApiTests holds;
    private readonly MaterialOptions material;

    public HoldFlowTests(ApiFixture api)
    {
        this.api = api;
        scan = new ScanTests(api);
        holds = new HoldApiTests(api);
        material = api.Factory.Services.GetRequiredService<IOptions<MaterialOptions>>().Value;
        material.EnforceLoadedMaterial = true;
    }

    public void Dispose() => material.EnforceLoadedMaterial = false;

    [Fact]
    public async Task A_bad_reel_is_traced_held_stopped_at_every_gate_and_released()
    {
        // --- A reel of the delivery SUP-x loaded at SMT (M6), four boards: three built with it, one still to come.
        var s = await holds.SetupAsync(4);
        var (reel, spare) = await DeliveryAsync(s.PartId);
        Assert.Equal("Accepted", (await holds.LoadAsync(s, reel)).Outcome);
        var (done, atPack, atAssy, next) = (s.Boards[0], s.Boards[1], s.Boards[2], s.Boards[3]);
        await holds.ThroughAsync(s, done, "L1-SMT-01", "L1-ASM-01", "L1-TEST-01", "L1-PACK-01");   // Completed
        await holds.ThroughAsync(s, atPack, "L1-SMT-01", "L1-ASM-01", "L1-TEST-01");               // waits for PACK
        await holds.ThroughAsync(s, atAssy, "L1-SMT-01");                                           // waits for ASSY
        var traced = await api.WithDbAsync(db => db.SerialComponents.CountAsync(c => c.LotId == db.MaterialLots.Single(l => l.LotNo == reel).Id));
        Assert.Equal(3, traced);   // written by the scans (M6 consumption), not by the test

        // --- The supplier reports the reel: QC traces it forward and holds all (M7).
        var qc = await api.LoginAsync("qc01");
        var forward = (await qc.GetFromJsonAsync<Contracts.Traceability.ForwardTraceResultDto>($"/api/trace/forward?lotNo={reel}"))!;
        Assert.Equal(3, forward.Serials.Count);
        var request = new HoldTargetsRequest("Lot", LotNo: reel, Reason: "Supplier recall: wrong resistor value on the reel");
        var preview = await HoldApiTests.OkAsync<HoldPreviewDto>(qc.PostAsJsonAsync("/api/quality/holds/preview", request));
        Assert.Equal(3, preview.SerialCount);
        Assert.Equal(new[] { reel, spare }.Order(), preview.Lots);
        var held = await HoldApiTests.OkAsync<HoldResultDto>(qc.PostAsJsonAsync("/api/quality/holds", request));
        Assert.Equal((3, 2), (held.Serials, held.Lots));

        // --- Every gate reads the same Status: route check at the next station (Pack too), the next board at SMT (its
        // reel is held), and loading the other reel of the delivery. (`done` is not scanned again here: within the 5 s
        // duplicate window M3 rule 5 re-acknowledges its last accepted scan and writes nothing, by design.)
        var blocked = new[]
        {
            await scan.ScanAsync(s.Op, "L1-ASM-01", atAssy),
            await scan.ScanAsync(s.Op, "L1-PACK-01", atPack),
        };
        Assert.All(blocked, b => Assert.Equal(("Blocked", "SerialHold", "Serial is on HOLD."), (b.Result, b.ReasonCode, b.Message)));
        var newBoard = await scan.ScanAsync(s.Op, "L1-SMT-01", next);
        Assert.Equal(("Blocked", "MaterialNotLoaded"), (newBoard.Result, newBoard.ReasonCode));
        Assert.Contains("on hold", newBoard.Message);
        var reload = await holds.LoadAsync(s, spare);
        Assert.Equal(("Rejected", "LotOnHold"), (reload.Outcome, reload.Code));
        Assert.Equal(0, await api.WithDbAsync(db => db.ScanLogs.CountAsync(l => l.SerialNo == next)));   // a block writes nothing

        // --- The supplier clears it (8D: label error only); a supervisor releases with a note.
        var released = await HoldApiTests.ReleaseAsync(await api.LoginAsync("sup01"), held.BatchId, "Supplier 8D: label printed wrong, value correct");
        Assert.Equal((5, 5, 0), (released.Released, released.Restored, released.StillHeld));
        Assert.Equal((SerialStatus.Completed, (SerialStatus?)null), await holds.SerialAsync(done));   // not "InProcess"
        Assert.Equal((SerialStatus.InProcess, (SerialStatus?)null), await holds.SerialAsync(atPack));
        Assert.Equal((MaterialLotStatus.Available, (MaterialLotStatus?)null), await holds.LotAsync(reel));
        Assert.Equal(new[] { 40, 30, 10 }, await api.WithDbAsync(db => db.Serials.Where(x => new[] { done, atPack, atAssy }.Contains(x.SerialNo))
            .OrderByDescending(x => x.CurrentSeq).Select(x => x.CurrentSeq).ToArrayAsync()));

        // --- The line goes on from where each board stopped.
        Assert.Equal("Ok", (await scan.ScanAsync(s.Op, "L1-ASM-01", atAssy)).Result);
        Assert.Equal("Ok", (await scan.ScanAsync(s.Op, "L1-PACK-01", atPack)).Result);
        Assert.Equal("Ok", (await scan.ScanAsync(s.Op, "L1-SMT-01", next)).Result);
        Assert.Equal(4, await api.WithDbAsync(db => db.SerialComponents.CountAsync(c => c.LotId == db.MaterialLots.Single(l => l.LotNo == reel).Id)));

        // --- The evidence: who held and released what, and why.
        var rows = await api.WithDbAsync(db => db.HoldRecords.Where(h => h.BatchId == held.BatchId).ToListAsync());
        Assert.All(rows, r => Assert.Equal(("qc01", "sup01", HoldScope.Lot, reel), (r.HeldBy, r.ReleasedBy, r.Scope, r.ScopeRef)));
        Assert.Equal(2, await api.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.TableName == "HoldRecords" && a.RecordKey == held.BatchId.ToString())));
    }

    // Spec 2.6 point 5: a hold of the lot and a scan of a board made from it, at the same moment. Whatever the order,
    // the board ends Held; if the scan got in first, the hold remembers the status after it (no lost update), and if
    // the hold got in first, the scan is blocked with SerialHold (a business answer, never a 422 - docs/incidents.md).
    [Fact]
    public async Task Hold_and_scan_at_the_same_time_never_lose_the_hold()
    {
        material.EnforceLoadedMaterial = false;   // ASSY consumes nothing here; the race is about the board
        var qc = await api.LoginAsync("qc01");
        for (var round = 0; round < 5; round++)
        {
            var s = await holds.SetupAsync(1);
            await holds.ThroughAsync(s, s.Boards[0], "L1-SMT-01");
            var lots = await holds.LotUsedByAsync(s, s.Boards[0]);
            Guid batch = default;
            ScanResponse? scanned = null;
            await ScanTests.TogetherAsync(
                async () => { batch = (await HoldApiTests.OkAsync<HoldResultDto>(qc.PostAsJsonAsync("/api/quality/holds",
                    new HoldTargetsRequest("Lot", LotNo: lots.Used, Reason: "Race")))).BatchId; return 0; },
                async () => { scanned = await scan.ScanAsync(s.Op, "L1-ASM-01", s.Boards[0]); return 0; });

            var row = await api.WithDbAsync(db => db.Serials.Where(x => x.SerialNo == s.Boards[0])
                .Select(x => new { x.Status, x.StatusBeforeHold, x.CurrentSeq }).SingleAsync());
            Assert.Equal((SerialStatus.Hold, SerialStatus.InProcess), (row.Status, row.StatusBeforeHold));
            if (scanned!.Result == "Ok") Assert.Equal(20, row.CurrentSeq);
            else Assert.Equal(("Blocked", "SerialHold", 10), (scanned.Result, scanned.ReasonCode, row.CurrentSeq));
            await HoldApiTests.ReleaseAsync(qc, batch, "race round done");
        }
    }

    // Two reels of one delivery (same supplier lot), Available: the one loaded and its spare.
    private Task<(string Reel, string Spare)> DeliveryAsync(int partId) => api.WithDbAsync(async db =>
    {
        var supplierLot = ApiFixture.Unique("SUP");
        var reel = new MaterialLot(ApiFixture.Unique("LF"), partId, 1_000, supplierLot, Received, null, false);
        var spare = new MaterialLot(ApiFixture.Unique("LF"), partId, 1_000, supplierLot, Received, null, false);
        db.MaterialLots.AddRange(reel, spare);
        await db.SaveChangesAsync();
        return (reel.LotNo, spare.LotNo);
    });
}
