using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Machines;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Domain.Planning;
using MiniMes.Infrastructure.Persistence;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Oee;

// Rows of M2/M3/M4 written straight into the tables with explicit times, for the OEE tests. Each test gets its own
// machine (on WC-SMT, scanned at station L1-SMT-01) and its own day in 2030, so the rows of other tests in the
// shared database never fall in its window.
public static class OeeTestData
{
    public sealed record Plant(int MachineId, int StationId, int WorkCenterId);

    // Plant time (UTC+7) on the test's day -> UTC.
    public static Func<int, int, int, DateTime> Day(int day)
        => (h, m, s) => new DateTime(2030, 10, day, h, m, s, DateTimeKind.Utc).AddHours(-7);

    public static async Task<Plant> NewMachineAsync(ApiFixture api) => await api.WithDbAsync(async db =>
    {
        var wc = await db.WorkCenters.SingleAsync(w => w.Code == "WC-SMT");
        var station = await db.Stations.SingleAsync(s => s.Code == "L1-SMT-01");
        var code = ApiFixture.Unique("OEE");
        var machine = new Machine(wc.Id, code, "OEE test machine", "PickAndPlace", code, MachineDataSource.Mqtt);
        db.Machines.Add(machine);
        await db.SaveChangesAsync();
        return new Plant(machine.Id, station.Id, wc.Id);
    });

    // A released WO whose SMT step (WC-SMT) has the given ideal cycle time, with `boards` serials.
    public static async Task<List<Serial>> WorkOrderAsync(ApiFixture api, Plant plant, decimal idealCycleSec, int boards, DateTime at) => await api.WithDbAsync(async db =>
    {
        var item = await db.Items.SingleAsync(i => i.Code == "PCB-A");
        var lineId = await db.Lines.Where(l => l.Code == "L1").Select(l => l.Id).SingleAsync();
        var wo = WorkOrder.Create(ApiFixture.Unique("WO-OEE"), item.Id, lineId, boards, at, at.AddHours(8), requireFai: false);
        wo.Release(1, 1, [new MaterialSnapshot(item.Id, 10, 1m)],
            [new RoutingStepSnapshot(10, "SMT", plant.WorkCenterId, idealCycleSec, false, true, null)], at);
        db.WorkOrders.Add(wo);
        await db.SaveChangesAsync();
        var serials = Enumerable.Range(1, boards).Select(i => new Serial($"{wo.WoNo}-{i:D4}", wo.Id, item.Id)).ToList();
        db.Serials.AddRange(serials);
        await db.SaveChangesAsync();
        return serials;
    });

    public static void Scans(AppDbContext db, Plant plant, IEnumerable<Serial> boards, ScanResult result, DateTime first, TimeSpan every)
    {
        var at = first;
        foreach (var b in boards)
        {
            db.ScanLogs.Add(new ScanLog(b, plant.StationId, 10, result, result == ScanResult.Ng ? "SLD-BRG" : null,
                "EMP00123", ScanSource.Machine, at));
            at += every;
        }
    }

    // Cumulative counter messages; the delta goes through CounterMath like the M4 ingest does.
    public static void Counters(AppDbContext db, Plant plant, params (DateTime At, long Counter)[] messages)
    {
        long? last = null;
        foreach (var (at, counter) in messages)
        {
            db.MachineEvents.Add(MachineEvent.Count(Guid.NewGuid(), plant.MachineId, counter, CounterMath.Compute(last, counter), null, null, at, at));
            last = counter;
        }
    }

    // State segments back to back: (start, state, reason code or null). The last one stays open.
    public static async Task StatesAsync(AppDbContext db, Plant plant, params (DateTime Start, MachineState State, string? Reason)[] segments)
    {
        var reasons = await db.DowntimeReasons.ToDictionaryAsync(r => r.Code, r => r.Id);
        for (var i = 0; i < segments.Length; i++)
        {
            var (start, state, reason) = segments[i];
            var h = new MachineStateHistory(plant.MachineId, state, start);
            if (reason is not null) h.AssignReason(reasons[reason]);
            if (i < segments.Length - 1) h.Close(segments[i + 1].Start);
            db.MachineStateHistory.Add(h);
        }
    }
}
