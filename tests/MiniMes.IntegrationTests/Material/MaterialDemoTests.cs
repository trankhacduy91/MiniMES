using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MiniMes.Application.Material;
using MiniMes.Contracts.Material;
using MiniMes.Domain.Material;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Execution;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Material;

// Module DoD, demo spec v2 steps 3-4 in one run over HTTP, with the people of the demo: the warehouse receives 3 reels
// (PendingIqc) -> loading one is refused -> the board cannot be scanned -> QC passes the supplier lot -> the reel goes
// on -> the scan is OK and records that reel. The pieces have their own tests; this one proves they fit together.
[Collection(ApiCollection.Name)]
public class MaterialDemoTests : IDisposable
{
    private static readonly DateTime Planned = new(2026, 11, 3, 0, 30, 0, DateTimeKind.Utc);
    private static int _nextSerial;

    private readonly ApiFixture api;
    private readonly ScanTests scan;
    private readonly MaterialOptions options;

    public MaterialDemoTests(ApiFixture api)
    {
        this.api = api;
        scan = new ScanTests(api);
        options = api.Factory.Services.GetRequiredService<IOptions<MaterialOptions>>().Value;
        options.EnforceLoadedMaterial = true;
    }

    public void Dispose()
    {
        options.EnforceLoadedMaterial = false;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Received_reels_wait_for_iqc_then_load_and_the_scan_records_them()
    {
        // A WO whose board takes 1 capacitor at SMT (seq 10); the capacitor needs IQC (SkipIqc = false).
        var (woId, capId, board) = await api.WithDbAsync(async db =>
        {
            var cap = Item.Create(ApiFixture.Unique("CAP"), "Demo capacitor", ItemType.Raw, Units.Pieces, false, true);
            db.Items.Add(cap);
            await db.SaveChangesAsync();
            var line = await db.Stations.Where(s => s.Code == "L1-SMT-01").Select(s => s.LineId).SingleAsync();
            var smt = await db.WorkCenters.SingleAsync(w => w.Code == "WC-SMT");
            var pcbA = await db.Items.SingleAsync(i => i.Code == "PCB-A");
            var wo = WorkOrder.Create(ApiFixture.Unique("WO-DEMO"), pcbA.Id, line, 10, Planned, Planned.AddHours(8), requireFai: false);
            wo.Release(1, 1, [new MaterialSnapshot(cap.Id, 10, 1m)], [new RoutingStepSnapshot(10, "SMT", smt.Id, 20m, false, true, null)], Planned);
            db.WorkOrders.Add(wo);
            await db.SaveChangesAsync();
            var serialNo = SerialFormat.Build("DEMO", new DateOnly(2026, 11, 3), Interlocked.Increment(ref _nextSerial));
            db.Serials.Add(new Serial(serialNo, wo.Id, pcbA.Id));
            await db.SaveChangesAsync();
            return (wo.Id, cap.Id, serialNo);
        });
        await scan.OpenSessionAsync("L1-SMT-01", woId);
        var stationId = await scan.StationIdAsync("L1-SMT-01");
        var (wh, qc, op) = (await api.LoginAsync("wh01"), await api.LoginAsync("qc01"), await api.LoginOperatorAsync(stationCode: "L1-SMT-01"));
        var supplierLot = ApiFixture.Unique("MUR");

        // Step 3: three reels of one delivery, each with its own label, all waiting for IQC.
        var received = await wh.PostAsJsonAsync("/api/material/lots", new ReceiveLotsRequest(capId, supplierLot, 4_000, 3, null));
        received.EnsureSuccessStatusCode();
        var lots = (await received.Content.ReadFromJsonAsync<ReceiveLotsResultDto>())!.Lots;
        Assert.Equal(3, lots.Count);
        var reel = lots[0].LotNo;
        Assert.All(await StatusesAsync(capId), s => Assert.Equal(MaterialLotStatus.PendingIqc, s));

        // Loading before IQC: red, no override, logged; the board is blocked because nothing is loaded.
        var early = await LoadAsync(op, stationId, reel);
        var blocked = await scan.ScanAsync(op, "L1-SMT-01", board);
        Assert.Equal(("Rejected", nameof(VerifyCode.LotNotIqcPassed)), (early.Outcome, early.Code));
        Assert.Equal(("Blocked", "MaterialNotLoaded"), (blocked.Result, blocked.ReasonCode));

        // QC passes the supplier lot: the three reels at once.
        (await qc.PostAsJsonAsync("/api/material/iqc", new RecordIqcRequest(capId, supplierLot, 5, 0, "Pass", null, null))).EnsureSuccessStatusCode();
        Assert.All(await StatusesAsync(capId), s => Assert.Equal(MaterialLotStatus.Available, s));

        // Step 4: the reel goes on (same delivery: no older reel, green), then the scan is OK and records it.
        var loaded = await LoadAsync(op, stationId, reel);
        var ok = await scan.ScanAsync(op, "L1-SMT-01", board);

        Assert.Equal(("Accepted", nameof(VerifyCode.Ok)), (loaded.Outcome, loaded.Code));
        Assert.Equal("Ok", ok.Result);
        var log = await api.WithDbAsync(db => db.MaterialVerificationLogs.Where(l => l.ScannedLotNo == reel).OrderBy(l => l.Id)
            .Select(l => l.Outcome).ToListAsync());
        Assert.Equal([VerifyOutcome.Rejected, VerifyOutcome.Accepted], log);
        var component = await api.WithDbAsync(db => (from c in db.SerialComponents
                                                      join s in db.Serials on c.SerialId equals s.Id
                                                      join l in db.MaterialLots on c.LotId equals l.Id
                                                      where s.SerialNo == board
                                                      select new { l.LotNo, l.RemainingQty, c.Qty }).SingleAsync());
        Assert.Equal((reel, 3_999m, 1m), (component.LotNo, component.RemainingQty, component.Qty));
    }

    private static async Task<LoadMaterialResponse> LoadAsync(HttpClient op, int stationId, string lotNo)
    {
        var response = await op.PostAsJsonAsync($"/api/stations/{stationId}/material-loads", new LoadMaterialRequest(lotNo, null, null, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoadMaterialResponse>())!;
    }

    private Task<List<MaterialLotStatus>> StatusesAsync(int itemId)
        => api.WithDbAsync(db => db.MaterialLots.Where(l => l.ItemId == itemId).Select(l => l.Status).ToListAsync());
}
