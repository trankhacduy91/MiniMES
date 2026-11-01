using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiniMes.Application.Common;
using MiniMes.Application.Oee;
using MiniMes.Application.Realtime;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Machines;
using MiniMes.Domain.Planning;
using MiniMes.Infrastructure.Oee;

namespace MiniMes.Infrastructure.Persistence.Seed;

// Development only (spec M5 step 8 check: "choose the sample shift, see 79.8 %"). The sample shift of spec 1.2.2 on
// L1-SMT-01, shift S1 of Thursday 01/10/2026, written as raw rows (counter messages, state segments, scans), then
// closed by the real snapshot service, so the dashboard shows it like any other shift. The seeded S1 breaks are
// 10:00-10:15 and 11:30-12:15 (60 minutes, like the spec's), so the stops are placed around them.
// Once: skipped when its work order exists.
public class OeeDemoSeeder(AppDbContext db, PlantTimeZone plant, IRealtimeNotifier notifier, ILoggerFactory logs)
{
    public const string WorkOrderNo = "WO-OEE-DEMO";
    public static readonly DateOnly ShiftDate = new(2026, 10, 1);

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await db.WorkOrders.AnyAsync(w => w.WoNo == WorkOrderNo, ct)) return;
        var machine = await db.Machines.SingleOrDefaultAsync(m => m.Code == "L1-SMT-01", ct);
        var station = await db.Stations.SingleOrDefaultAsync(s => s.Code == "L1-SMT-01", ct);
        var item = await db.Items.SingleOrDefaultAsync(i => i.Code == "PCB-A", ct);
        if (machine is null || station is null || item is null) return;
        // The machine already has a history on that day (a database used for something else): leave it alone.
        DateTime At(int h, int m, int s = 0) => TimeZoneInfo.ConvertTimeToUtc(ShiftDate.ToDateTime(new TimeOnly(h, m, s)), plant.Zone);
        var (shiftStart, shiftEnd) = (At(6, 0), At(14, 0));
        if (await db.MachineStateHistory.AnyAsync(x => x.MachineId == machine.Id && x.StartAt < shiftEnd && (x.EndAt == null || x.EndAt > shiftStart), ct))
            return;

        // A WO whose SMT step has the ideal cycle time of the example: 20 s (the routing master says 30 s).
        var wo = WorkOrder.Create(WorkOrderNo, item.Id, station.LineId, 1000, At(6, 0), At(14, 0), requireFai: false);
        wo.Release(1, 1, [new MaterialSnapshot(item.Id, 10, 1m)],
            [new RoutingStepSnapshot(10, "SMT", machine.WorkCenterId, 20m, false, true, null)], At(5, 0));
        db.WorkOrders.Add(wo);
        await db.SaveChangesAsync(ct);
        var boards = Enumerable.Range(1, 1000).Select(i => new Serial($"OEEDEMO{i:D5}", wo.Id, item.Id)).ToList();
        db.Serials.AddRange(boards);
        await db.SaveChangesAsync(ct);

        // 1,000 boards counted: a baseline before the shift, then 200 messages of +5 (CounterMath as in the ingest).
        long? last = null;
        foreach (var (at, counter) in new[] { (At(5, 55), 50_000L) }
                     .Concat(Enumerable.Range(1, 200).Select(i => (At(6, 16).AddSeconds(i * 138), 50_000L + 5 * i))))
        {
            db.MachineEvents.Add(MachineEvent.Count(Guid.NewGuid(), machine.Id, counter, CounterMath.Compute(last, counter), null, null, at, at));
            last = counter;
        }

        // 15 min meeting, FDR-JAM 25 min, MAT-WAIT 12 min, an 8-minute QC wait nobody classified, six 20 s jams,
        // Idle through both breaks: 405 planned, 360 run.
        var reasons = await db.DowntimeReasons.ToDictionaryAsync(r => r.Code, r => r.Id, ct);
        (DateTime Start, MachineState State, string? Reason)[] segments =
        [
            (At(6, 0), MachineState.PlannedStop, "MEETING"), (At(6, 15), MachineState.Running, null),
            (At(7, 10), MachineState.Down, "FDR-JAM"), (At(7, 35), MachineState.Running, null),
            .. Enumerable.Range(0, 6).SelectMany(i => new (DateTime, MachineState, string?)[]
                { (At(7, 50 + i), MachineState.Down, null), (At(7, 50 + i, 20), MachineState.Running, null) }),
            (At(9, 20), MachineState.Down, "MAT-WAIT"), (At(9, 32), MachineState.Running, null),
            (At(10, 0), MachineState.Idle, null), (At(10, 15), MachineState.Running, null),
            (At(11, 30), MachineState.Idle, null), (At(12, 15), MachineState.Running, null),
            (At(12, 40), MachineState.Down, null), (At(12, 48), MachineState.Running, null),
        ];
        for (var i = 0; i < segments.Length; i++)
        {
            var (start, state, reason) = segments[i];
            var h = new MachineStateHistory(machine.Id, state, start);
            if (reason is not null && reasons.TryGetValue(reason, out var id)) h.AssignReason(id);
            h.Close(i < segments.Length - 1 ? segments[i + 1].Start : At(14, 0));   // closed: no open segment left behind
            db.MachineStateHistory.Add(h);
        }

        // 970 OK + 30 NG scans by serial (the source of Good, spec 1.2.10).
        for (var i = 0; i < 1000; i++)
        {
            var ng = i >= 970;
            var at = ng ? At(11, 0).AddMinutes(i - 970) : At(6, 16).AddSeconds(i * 27);
            db.ScanLogs.Add(new ScanLog(boards[i], station.Id, 10, ng ? ScanResult.Ng : ScanResult.Ok, ng ? "SLD-BRG" : null,
                "EMP00123", ScanSource.Machine, at));
        }
        await db.SaveChangesAsync(ct);

        // Close the hours and the shift as the job would have at 14:05 that day.
        var time = new FixedTime(At(14, 5));
        var options = Options.Create(new OeeOptions { CatchUpDays = 0 });
        await new OeeSnapshotService(db, new OeeInputReader(db, time, options), plant, time, options, notifier,
            logs.CreateLogger<OeeSnapshotService>()).CloseDueBucketsAsync(ct, [machine.Id]);
    }
}
