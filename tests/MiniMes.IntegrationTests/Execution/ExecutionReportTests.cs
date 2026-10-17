using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Execution;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Planning;
using MiniMes.IntegrationTests.Infrastructure;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.IntegrationTests.Execution;

// Spec M3 step 9: FPY and WIP compared with numbers worked out by hand. The history is written straight into the
// tables through the Domain methods, with explicit times: the report is under test here, not the scan
// (and a board failing the same test twice within seconds would otherwise be a duplicate).
[Collection(ApiCollection.Name)]
public class ExecutionReportTests(ApiFixture api)
{
    private readonly ScanTests scan = new(api);
    private static readonly int[] Routing = [10, 20, 30, 40];

    // 11 boards of a 4-step routing (10 SMT, 20 ASSY, 30 TEST, 40 PACK):
    //   s0-s4   pass everything                          -> Completed
    //   s5, s6  pass TEST first time, wait for PACK      -> InProcess, CurrentSeq 30
    //   s7      NG at TEST, repaired, pass, PACK         -> Completed     (NG first time)
    //   s8      NG at TEST, checked in at Repair         -> Repair, 20    (NG first time)
    //   s9      NG, repaired, NG again (2 NG)            -> Ng, 20        (NG first time)
    //   s10     SMT, then scrapped (dropped board)       -> Scrapped
    // By hand: SMT 11/11, ASSY 10/10, TEST 7/10 = 70 %, PACK 6/6; RTY 70 %; final yield 6/(6+1) = 85.71 %.
    // Counting scans at TEST would give 8 OK / 12 scans = 66.67 %: the trap of spec 1.2.
    // WIP = 2 + 1 + 1 = 4 = 11 started - 6 completed - 1 scrapped.
    private async Task<int> SeedHistoryAsync()
    {
        var (wo, serials) = await scan.ReleasedAsync(11);
        var ids = await api.WithDbAsync(db => db.Stations.Where(s => s.Code.StartsWith("L1-"))
            .ToDictionaryAsync(s => s.Code, s => s.Id));
        await api.WithDbAsync(async db =>
        {
            var boards = await db.Serials.Where(s => s.WorkOrderId == wo.Id).OrderBy(s => s.SerialNo).ToListAsync();
            var at = new DateTime(2026, 11, 2, 1, 0, 0, DateTimeKind.Utc);
            void Step(Serial b, int seq, ScanResult r = ScanResult.Ok)
            {
                var station = seq switch { 10 => "L1-SMT-01", 20 => "L1-ASM-01", 30 => "L1-TEST-01", _ => "L1-PACK-01" };
                b.ApplyScan(seq, seq == 40, r);
                db.ScanLogs.Add(new ScanLog(b, ids[station], seq, r, r == ScanResult.Ng ? "SLD-BRG" : null, "EMP00123", ScanSource.Kiosk, at = at.AddSeconds(30)));
            }
            void CheckIn(Serial b)
            {
                b.CheckInRepair();
                db.ScanLogs.Add(new ScanLog(b, ids["L1-REPAIR"], 0, ScanResult.Ok, null, "EMP00123", ScanSource.Kiosk, at = at.AddSeconds(30)));
            }

            foreach (var b in boards.Take(10)) { Step(b, 10); Step(b, 20); }
            foreach (var b in boards.Take(7)) Step(b, 30);
            foreach (var b in boards.Take(5)) Step(b, 40);
            Step(boards[7], 30, ScanResult.Ng); CheckIn(boards[7]); boards[7].CompleteRepair(null, Routing); Step(boards[7], 30); Step(boards[7], 40);
            Step(boards[8], 30, ScanResult.Ng); CheckIn(boards[8]);
            Step(boards[9], 30, ScanResult.Ng); CheckIn(boards[9]); boards[9].CompleteRepair(null, Routing); Step(boards[9], 30, ScanResult.Ng);
            Step(boards[10], 10); boards[10].Scrap("Dropped on the floor");
            return await db.SaveChangesAsync();
        });
        return wo.Id;
    }

    [Fact]
    public async Task Fpy_counts_the_first_scan_of_each_serial_at_each_step()
    {
        var woId = await SeedHistoryAsync();

        var report = (await (await api.LoginAsync("sup01")).GetFromJsonAsync<FpyReportDto>($"/api/reports/fpy?workOrderId={woId}"))!;

        Assert.Equal(
            [new FpyStepDto(10, "SMT", 11, 11, 100m), new FpyStepDto(20, "ASSY", 10, 10, 100m),
             new FpyStepDto(30, "TEST", 10, 7, 70m), new FpyStepDto(40, "PACK", 6, 6, 100m)],
            report.Steps);
        Assert.Equal((70m, 6, 1, 85.71m), (report.RtyPct, report.Completed, report.Scrapped, report.FinalYieldPct));
    }

    [Fact]
    public async Task Wip_adds_up_to_started_and_not_finished()
    {
        var woId = await SeedHistoryAsync();

        var report = (await (await api.LoginOperatorAsync()).GetFromJsonAsync<WipReportDto>($"/api/reports/wip?workOrderId={woId}"))!;

        Assert.Equal(
            [new WipRowDto(20, "ASSY", "TEST", "Ng", 1), new WipRowDto(20, "ASSY", "TEST", "Repair", 1), new WipRowDto(30, "TEST", "PACK", "InProcess", 2)],
            report.Rows.OrderBy(r => r.CurrentSeq).ThenBy(r => r.Status));
        var byStatus = await api.WithDbAsync(db => db.Serials.Where(s => s.WorkOrderId == woId)
            .GroupBy(s => s.Status).ToDictionaryAsync(g => g.Key, g => g.Count()));
        Assert.Equal(4, report.Total);
        Assert.Equal(11 - byStatus.GetValueOrDefault(SerialStatus.Created) - byStatus[SerialStatus.Completed] - byStatus[SerialStatus.Scrapped], report.Total);
    }

    // A WO nothing has run on yet: every step listed with 0 and no percentage, empty WIP.
    [Fact]
    public async Task New_order_has_empty_reports()
    {
        var (wo, _) = await scan.ReleasedAsync(3);
        var client = await api.LoginOperatorAsync();

        var fpy = (await client.GetFromJsonAsync<FpyReportDto>($"/api/reports/fpy?workOrderId={wo.Id}"))!;
        var wip = (await client.GetFromJsonAsync<WipReportDto>($"/api/reports/wip?workOrderId={wo.Id}"))!;

        Assert.All(fpy.Steps, s => Assert.Equal((0, (decimal?)null), (s.Entered, s.FpyPct)));
        Assert.Equal(4, fpy.Steps.Count);
        Assert.Equal(((decimal?)null, (decimal?)null), (fpy.RtyPct, fpy.FinalYieldPct));
        Assert.Equal((0, 0), (wip.Rows.Count, wip.Total));
    }

    [Fact]
    public async Task Unknown_order_is_404_and_a_token_is_required()
    {
        var client = await api.LoginOperatorAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/reports/fpy?workOrderId=2147483647")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/reports/wip?workOrderId=2147483647")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous().GetAsync("/api/reports/fpy?workOrderId=1")).StatusCode);
    }
}
