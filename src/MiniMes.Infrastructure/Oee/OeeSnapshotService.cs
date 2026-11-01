using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiniMes.Application.Common;
using MiniMes.Application.Oee;
using MiniMes.Application.Realtime;
using MiniMes.Contracts.Oee;
using MiniMes.Domain.MasterData.Calendar;
using MiniMes.Domain.Oee;
using MiniMes.Domain.Time;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Oee;

// Spec M5 2.4 / step 5. Not "at minute 0, close the last hour": every run asks "which buckets are due and not
// closed yet?" and closes them all. A server off for 3 hours closes the 3 missing hours on its next run; a second
// run finds nothing to do (rule 13). A bucket = (machine, shift, hour) or (machine, shift) for the total.
public sealed class OeeSnapshotService(AppDbContext db, IOeeInputReader reader, PlantTimeZone plant, TimeProvider time,
    IOptions<OeeOptions> options, IRealtimeNotifier notifier, ILogger<OeeSnapshotService> log)
{
    private sealed record Bucket(ShiftWindow Shift, int ShiftId, byte? Hour, Interval Window);

    /// <param name="machineIds">Only these machines (tests, a recompute after a reason change); null = all active.</param>
    /// <returns>Number of snapshots written.</returns>
    public async Task<int> CloseDueBucketsAsync(CancellationToken ct, IReadOnlyCollection<int>? machineIds = null)
    {
        var now = time.GetUtcNow().UtcDateTime;
        // Grace: wait for late data before closing; closing at 14:00:00 would miss a count sent at 13:59:59.
        var dueBefore = now - TimeSpan.FromMinutes(options.Value.SnapshotGraceMinutes);
        var today = plant.DateOf(now);
        var first = today.AddDays(-options.Value.CatchUpDays);

        var machines = (await OeeScope.MachinesAsync(db, null, null, ct))
            .Where(m => machineIds == null || machineIds.Contains(m.Id)).ToList();
        var ids = machines.Select(m => m.Id).ToList();
        var snapshots = await db.OeeSnapshots
            .Where(s => ids.Contains(s.MachineId) && ((s.ShiftDate >= first && s.ShiftDate <= today) || !s.IsFinal))
            .ToListAsync(ct);
        // Dates to look at: the catch-up range, plus any older shift marked for recalculation (rule 8).
        var dates = Enumerable.Range(0, today.DayNumber - first.DayNumber + 1).Select(first.AddDays)
            .Concat(snapshots.Where(s => !s.IsFinal).Select(s => s.ShiftDate))
            .Distinct().Order().ToList();

        var (calendar, shifts) = await OeeScope.CalendarAsync(db, plant, dates, ct);
        var buckets = dates.SelectMany(calendar.WindowsOn)
            .SelectMany(w => BucketsOf(w, shifts.Single(s => s.Code == w.ShiftCode)))
            .Where(b => b.Window.EndUtc <= dueBefore)
            .ToList();

        var byKey = snapshots.ToDictionary(s => (s.MachineId, s.ShiftDate, s.ShiftId, s.Hour));
        var written = 0;
        foreach (var machine in machines)
        {
            var machineId = machine.Id;
            var closed = new List<Bucket>();
            foreach (var b in buckets)
            {
                var key = (machineId, b.Shift.ShiftDate, b.ShiftId, b.Hour);
                if (byKey.TryGetValue(key, out var row) && row.IsFinal) continue;   // already closed, nothing changed

                // The shift total is read from the raw data like an hour, not added up from the hourly rows: it does
                // not depend on the order the rows are closed in (spec 2.4). A test checks both give the same OEE.
                var reading = await reader.ReadAsync(machineId, b.Shift, b.Window, ct);
                var result = OeeCalculator.Calculate(reading.Input);
                if (row is null)
                {
                    row = new OeeSnapshot(machineId, b.Shift.ShiftDate, b.ShiftId, b.Hour, b.Window.StartUtc, b.Window.EndUtc);
                    db.OeeSnapshots.Add(row);
                    byKey[key] = row;
                }
                row.Apply(result, reading.ScanCount, now, isFinal: true);
                closed.Add(b);
            }
            await db.SaveChangesAsync(ct);   // per machine: a failure loses one machine's run, the next tick retries
            written += closed.Count;
            // One push per machine and shift per run: at most once a minute, the job's own pace (no extra throttle).
            foreach (var shift in closed.GroupBy(b => (b.Shift.ShiftDate, b.Shift.ShiftCode)))
                await notifier.OeeSnapshotClosedAsync(new OeeSnapshotClosedDto(machineId, machine.Code, machine.Line,
                    shift.Key.ShiftDate, shift.Key.ShiftCode, shift.Select(b => (int?)b.Hour).ToList()), ct);
        }
        if (written > 0)
            log.LogInformation("OEE snapshots: {Written} written for {Machines} machine(s)", written, machines.Count);
        return written;
    }

    // Hours counted from the shift start (MVP: shifts start on the hour, so they match the wall clock), then the total.
    private static IEnumerable<Bucket> BucketsOf(ShiftWindow w, Shift shift)
    {
        var start = w.StartUtc;
        for (var i = 0; start < w.EndUtc; i++, start = start.AddHours(1))
        {
            var end = start.AddHours(1) < w.EndUtc ? start.AddHours(1) : w.EndUtc;
            yield return new Bucket(w, shift.Id, (byte)((shift.StartTime.Hour + i) % 24), new Interval(start, end));
        }
        yield return new Bucket(w, shift.Id, null, w.Span);
    }
}
