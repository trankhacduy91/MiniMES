using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Application.Oee;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Machines;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Domain.Oee;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Time;
using MiniMes.Infrastructure.Oee;
using MiniMes.Infrastructure.Persistence;
using MiniMes.IntegrationTests.Infrastructure;
using static MiniMes.IntegrationTests.Oee.OeeTestData;

namespace MiniMes.IntegrationTests.Oee;

// Spec M5 step 4: the sample shift of 1.2.2 rebuilt from rows in MachineEvents / MachineStateHistory / ScanLogs,
// read back by the real reader against SQL Server, must give the hand-calculated 79.84 %.
// Test data: OeeTestData (own machine, own day). "Now" comes from a FakeTimeProvider.
[Collection(ApiCollection.Name)]
public class OeeInputReaderTests(ApiFixture api)
{
    private static ShiftWindow S1(Func<int, int, int, DateTime> at)
        => new(DateOnly.FromDateTime(at(6, 0, 0).AddHours(7)), "S1", at(6, 0, 0), at(14, 0, 0),
            [new(at(8, 0, 0), at(8, 15, 0)), new(at(10, 0, 0), at(10, 45, 0))]);   // 60 min of breaks, as in 1.2.2

    private Task<OeeReading> ReadAsync(Plant plant, ShiftWindow shift, Interval window, DateTime now)
        => api.WithDbAsync(db => new OeeInputReader(db, new FakeTimeProvider(new DateTimeOffset(now)), Options.Create(new OeeOptions()))
            .ReadAsync(plant.MachineId, shift, window, CancellationToken.None));

    [Fact]
    public async Task Sample_shift_from_database_rows_gives_79_84_percent()
    {
        var at = Day(3);
        var plant = await NewMachineAsync(api);
        var boards = await WorkOrderAsync(api, plant, 20m, 1000, at(5, 0, 0));
        await api.WithDbAsync(async db =>
        {
            // 1,000 boards: a baseline before the shift (not counted), 200 messages of +5, one at 14:00 (next shift).
            Counters(db, plant, [(at(5, 55, 0), 50_000),
                .. Enumerable.Range(1, 200).Select(i => (at(6, 16, 0).AddSeconds(i * 138), 50_000L + 5 * i)),
                (at(14, 0, 0), 51_005)]);
            // 06:00-06:15 meeting (planned), three real stops (the QC wait not classified yet), six 20 s jams, the
            // machine Idle through both breaks.
            await StatesAsync(db, plant, [
                (at(6, 0, 0), MachineState.PlannedStop, "MEETING"), (at(6, 15, 0), MachineState.Running, null),
                (at(7, 10, 0), MachineState.Down, "FDR-JAM"), (at(7, 35, 0), MachineState.Running, null),
                .. Enumerable.Range(0, 6).SelectMany(i => new (DateTime, MachineState, string?)[]
                    { (at(7, 50 + i, 0), MachineState.Down, null), (at(7, 50 + i, 20), MachineState.Running, null) }),
                (at(8, 0, 0), MachineState.Idle, null), (at(8, 15, 0), MachineState.Running, null),
                (at(9, 20, 0), MachineState.Down, "MAT-WAIT"), (at(9, 32, 0), MachineState.Running, null),
                (at(10, 0, 0), MachineState.Idle, null), (at(10, 45, 0), MachineState.Running, null),
                (at(12, 40, 0), MachineState.Down, null), (at(12, 48, 0), MachineState.Running, null),
                (at(14, 0, 0), MachineState.Idle, null)]);
            // 970 OK + 30 NG scans through the shift.
            Scans(db, plant, boards.Take(970), ScanResult.Ok, at(6, 16, 0), TimeSpan.FromSeconds(27));
            Scans(db, plant, boards.Skip(970), ScanResult.Ng, at(11, 0, 0), TimeSpan.FromSeconds(60));
            return await db.SaveChangesAsync();
        });

        var shift = S1(at);
        var reading = await ReadAsync(plant, shift, shift.Span, now: at(14, 10, 0));
        var r = OeeCalculator.Calculate(reading.Input);

        Assert.Equal((405.0, 360.0, 1000L, 970L), (r.PlannedTime.TotalMinutes, r.RunTime.TotalMinutes, r.TotalCount, r.GoodCount));
        Assert.Equal(20_000, r.IdealSeconds);
        Assert.Equal(0.7984, r.Oee!.Value, 4);
        Assert.Equal(1000, reading.ScanCount);
    }

    // Spec 1.5 #4: the PLC restarts mid-shift and its counter starts again from 0.
    [Fact]
    public async Task Counter_reset_mid_shift_counts_what_was_made_after_the_reset()
    {
        var at = Day(4);
        var plant = await NewMachineAsync(api);
        var boards = await WorkOrderAsync(api, plant, 20m, 20, at(5, 0, 0));
        await api.WithDbAsync(async db =>
        {
            Counters(db, plant, (at(6, 50, 0), 100), (at(7, 10, 0), 105), (at(7, 20, 0), 3), (at(7, 40, 0), 10));
            await StatesAsync(db, plant, (at(6, 0, 0), MachineState.Running, null));
            Scans(db, plant, boards.Take(15), ScanResult.Ok, at(7, 5, 0), TimeSpan.FromMinutes(2));
            return await db.SaveChangesAsync();
        });

        var shift = S1(at);
        var reading = await ReadAsync(plant, shift, new Interval(at(7, 0, 0), at(8, 0, 0)), now: at(9, 0, 0));

        Assert.Equal(15, reading.Input.Segments.Sum(s => s.TotalCount));   // 5 before the reset + 3 + 7; not 10 - 100
    }

    // A stop still going on (EndAt NULL) lasts until "now"; the break inside it is not downtime.
    [Fact]
    public async Task Open_down_segment_runs_until_now()
    {
        var at = Day(5);
        var plant = await NewMachineAsync(api);
        var boards = await WorkOrderAsync(api, plant, 20m, 5, at(5, 0, 0));
        await api.WithDbAsync(async db =>
        {
            await StatesAsync(db, plant, (at(6, 0, 0), MachineState.Running, null), (at(7, 30, 0), MachineState.Down, null));
            Scans(db, plant, boards, ScanResult.Ok, at(6, 30, 0), TimeSpan.FromMinutes(5));
            return await db.SaveChangesAsync();
        });

        var now = at(8, 30, 0);
        var shift = S1(at);
        var reading = await ReadAsync(plant, shift, shift.Until(now), now);
        var r = OeeCalculator.Calculate(reading.Input);

        Assert.Equal(135, r.PlannedTime.TotalMinutes);                          // 150 - the 15-minute break
        Assert.Equal(45, reading.Input.UnplannedDowntime.TotalMinutes);         // 07:30-08:00 (30) + 08:15-08:30 (15)
    }

    // Decision of step 4: Idle on a shift with no scan at the machine's station = nothing planned, not a loss.
    [Fact]
    public async Task Idle_without_any_work_in_the_shift_is_not_judged()
    {
        var at = Day(6);
        var plant = await NewMachineAsync(api);
        await api.WithDbAsync(async db =>
        {
            await StatesAsync(db, plant, (at(6, 0, 0), MachineState.Idle, null), (at(14, 0, 0), MachineState.Offline, null));
            return await db.SaveChangesAsync();
        });

        var shift = S1(at);
        var r = OeeCalculator.Calculate((await ReadAsync(plant, shift, shift.Span, at(15, 0, 0))).Input);

        Assert.Equal(TimeSpan.Zero, r.PlannedTime);
        Assert.Null(r.Oee);
    }
}
