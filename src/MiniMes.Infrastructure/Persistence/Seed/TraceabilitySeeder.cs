using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Material;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.Planning;

namespace MiniMes.Infrastructure.Persistence.Seed;

// Spec M6 step 6: the standard trace data of section 3 (the test oracle of step 7 and the demo of step 8).
// One WO of 200 PCB-A boards made at L1-SMT-01, one capacitor and one resistor per board:
//   L-CAP-A  CAP-100NF  supplier lot SUP-S1              boards 1-120
//   L-CAP-P  CAP-100NF  SUP-S1, the reel that was split   not used directly
//   L-CAP-B  CAP-100NF  child of L-CAP-P (LotGenealogy)    boards 121-200
//   L-RES-A  RES-100K   SUP-R1                           all 200
// Written as the scans would have written them: ScanLog per board, MaterialLoads (who loaded what, when), then
// SerialComponents pointing at the scans. History only: the WO is closed and every load taken off, so nothing shows
// on the dispatch list or the station's material checklist.
// The trace tables are insert-only (triggers), so a scenario cannot be deleted and rebuilt: a tag makes each test's
// scenario its own (codes "L-CAP-A-{tag}", serials "TRACE-{tag}-0001"); Development seeds the untagged one once.
public class TraceabilitySeeder(AppDbContext db)
{
    public const int Boards = 200, FirstLotBoards = 120;
    public static readonly DateTime MadeFrom = new(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc);   // 08:00 plant time
    public static readonly TimeSpan Cycle = TimeSpan.FromSeconds(30);

    public sealed record Scenario(string Tag, int WorkOrderId, IReadOnlyList<string> Serials, IReadOnlyDictionary<string, int> LotIds)
    {
        public string Code(string baseCode) => Tag.Length == 0 ? baseCode : $"{baseCode}-{Tag}";
        public string SerialNo(int board) => Serials[board - 1];   // 1-based, like the spec's "serial 150"
    }

    public static string Code(string baseCode, string tag) => tag.Length == 0 ? baseCode : $"{baseCode}-{tag}";

    // Returns the existing scenario when this tag was seeded before (a restart of the API).
    public async Task<Scenario> SeedAsync(string tag = "", CancellationToken ct = default)
    {
        var woNo = Code("WO-TRACE", tag);
        if (await db.WorkOrders.AsNoTracking().SingleOrDefaultAsync(w => w.WoNo == woNo, ct) is { } existing)
            return await ReadAsync(tag, existing.Id, ct);

        var station = await db.Stations.AsNoTracking().SingleAsync(s => s.Code == "L1-SMT-01", ct);
        var pcbA = await db.Items.AsNoTracking().SingleAsync(i => i.Code == "PCB-A", ct);
        var cap = await ItemAsync("CAP-100NF", "Capacitor 100nF 0402", ct);
        var res = await ItemAsync("RES-100K", "Resistor 100k 0402", ct);
        const string by = "EMP00123";
        DateTime BoardAt(int board) => MadeFrom + Cycle * board;
        var end = BoardAt(Boards) + Cycle;

        // Received a few days before, IQC passed (history: no IqcRecord needed for tracing). L-CAP-B is 2,000 parts
        // split off L-CAP-P on 19/10 (e.g. a partial reel for the other line); it keeps its parent's supplier lot.
        var received = MadeFrom.AddDays(-5);
        var lots = new Dictionary<string, MaterialLot>
        {
            ["L-CAP-A"] = new(Code("L-CAP-A", tag), cap.Id, 5_000, Code("SUP-S1", tag), received, null, needsIqc: false),
            ["L-CAP-P"] = new(Code("L-CAP-P", tag), cap.Id, 10_000, Code("SUP-S1", tag), received, null, needsIqc: false),
            ["L-CAP-B"] = new(Code("L-CAP-B", tag), cap.Id, 2_000, Code("SUP-S1", tag), MadeFrom.AddDays(-1), null, needsIqc: false),
            ["L-RES-A"] = new(Code("L-RES-A", tag), res.Id, 10_000, Code("SUP-R1", tag), received, null, needsIqc: false),
        };
        db.MaterialLots.AddRange(lots.Values);

        var wo = WorkOrder.Create(woNo, pcbA.Id, station.LineId, Boards, MadeFrom, end, requireFai: false);
        wo.Release(1, 1, [new MaterialSnapshot(cap.Id, 10, 1m), new MaterialSnapshot(res.Id, 10, 1m)],
            [new RoutingStepSnapshot(10, "SMT", station.WorkCenterId!.Value, 30m, false, true, null)], MadeFrom.AddHours(-1));
        wo.Start(BoardAt(1));
        db.WorkOrders.Add(wo);
        await db.SaveChangesAsync(ct);

        db.LotGenealogy.Add(new LotGenealogy(lots["L-CAP-P"].Id, lots["L-CAP-B"].Id, 2_000, MadeFrom.AddDays(-1)));
        lots["L-CAP-P"].CorrectRemaining(8_000);

        // Reels on the station: CAP-A and RES-A before the first board; CAP-A is replaced by CAP-B between
        // boards 120 and 121; everything taken off after the last board.
        var change = BoardAt(FirstLotBoards) + Cycle / 2;
        var loadCapA = new MaterialLoad(station.Id, cap.Id, lots["L-CAP-A"].Id, wo.Id, "12", by, MadeFrom.AddMinutes(-10));
        var loadRes = new MaterialLoad(station.Id, res.Id, lots["L-RES-A"].Id, wo.Id, "13", by, MadeFrom.AddMinutes(-10));
        var loadCapB = new MaterialLoad(station.Id, cap.Id, lots["L-CAP-B"].Id, wo.Id, "12", by, change);
        loadCapA.Unload(by, change);
        loadCapB.Unload(by, end);
        loadRes.Unload(by, end);
        db.MaterialLoads.AddRange(loadCapA, loadRes, loadCapB);

        var serials = Enumerable.Range(1, Boards).Select(i => new Serial(Code("TRACE", tag) + $"-{i:D4}", wo.Id, pcbA.Id)).ToList();
        db.Serials.AddRange(serials);
        await db.SaveChangesAsync(ct);

        var scans = serials.Select((s, i) =>
        {
            s.ApplyScan(10, isLastStep: true, ScanResult.Ok);
            return new ScanLog(s, station.Id, 10, ScanResult.Ok, null, by, ScanSource.Machine, BoardAt(i + 1));
        }).ToList();
        db.ScanLogs.AddRange(scans);
        await db.SaveChangesAsync(ct);

        for (var i = 0; i < Boards; i++)
        {
            var capLot = i < FirstLotBoards ? lots["L-CAP-A"] : lots["L-CAP-B"];
            db.SerialComponents.Add(new SerialComponent(serials[i].Id, capLot.Id, cap.Id, 1m, station.Id, scans[i].Id, scans[i].ScannedAt));
            db.SerialComponents.Add(new SerialComponent(serials[i].Id, lots["L-RES-A"].Id, res.Id, 1m, station.Id, scans[i].Id, scans[i].ScannedAt));
        }
        lots["L-CAP-A"].CorrectRemaining(5_000 - FirstLotBoards);
        lots["L-CAP-B"].CorrectRemaining(2_000 - (Boards - FirstLotBoards));
        lots["L-RES-A"].CorrectRemaining(10_000 - Boards);
        wo.TryAutoComplete(Boards, end);   // 200 of 200 good at the last (only) step
        wo.Close();
        await db.SaveChangesAsync(ct);
        await db.WorkOrderOperations.Where(o => o.WorkOrderId == wo.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.QtyGood, Boards), ct);

        return await ReadAsync(tag, wo.Id, ct);
    }

    private async Task<Item> ItemAsync(string code, string name, CancellationToken ct)
    {
        if (await db.Items.SingleOrDefaultAsync(i => i.Code == code, ct) is { } item) return item;
        item = Item.Create(code, name, ItemType.Raw, Units.Pieces, isSerialized: false, isLotControlled: true);
        db.Items.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    private async Task<Scenario> ReadAsync(string tag, int woId, CancellationToken ct)
    {
        var serials = await db.Serials.AsNoTracking().Where(s => s.WorkOrderId == woId).OrderBy(s => s.SerialNo)
            .Select(s => s.SerialNo).ToListAsync(ct);
        var names = new[] { "L-CAP-A", "L-CAP-P", "L-CAP-B", "L-RES-A" };
        var codes = names.Select(n => Code(n, tag)).ToList();
        var ids = await db.MaterialLots.AsNoTracking().Where(l => codes.Contains(l.LotNo)).ToDictionaryAsync(l => l.LotNo, l => l.Id, ct);
        return new Scenario(tag, woId, serials, names.ToDictionary(n => n, n => ids[Code(n, tag)]));
    }
}
