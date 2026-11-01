using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Application.Common;
using MiniMes.Application.Oee;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Machines;
using MiniMes.Domain.Oee;
using MiniMes.Infrastructure.Oee;
using MiniMes.IntegrationTests.Infrastructure;
using static MiniMes.IntegrationTests.Oee.OeeTestData;

namespace MiniMes.IntegrationTests.Oee;

// Spec M5 step 5: the snapshot job against SQL Server, driven by a FakeTimeProvider. The seeded shift S1 is used
// (06:00-14:00 Mon-Sat, breaks 10:00-10:15 and 11:30-12:15 = 60 minutes, like the sample of 1.2.2).
[Collection(ApiCollection.Name)]
public class OeeSnapshotTests(ApiFixture api)
{
    private static readonly PlantTimeZone Plant = new(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));
    private readonly RecordingNotifier Notifier = new();

    private Task<int> CloseAsync(OeeTestData.Plant plant, DateTime now) => api.WithDbAsync(db =>
    {
        var time = new FakeTimeProvider(new DateTimeOffset(now));
        var options = Options.Create(new OeeOptions());
        return new OeeSnapshotService(db, new OeeInputReader(db, time, options), Plant, time, options, Notifier,
            NullLogger<OeeSnapshotService>.Instance).CloseDueBucketsAsync(CancellationToken.None, [plant.MachineId]);
    });

    private Task<List<OeeSnapshot>> S1RowsAsync(OeeTestData.Plant plant, DateTime anyTimeInShift) => api.WithDbAsync(async db =>
    {
        var s1 = await db.Shifts.Where(s => s.Code == "S1").Select(s => s.Id).SingleAsync();
        var date = Plant.DateOf(anyTimeInShift);
        return await db.OeeSnapshots.AsNoTracking()
            .Where(x => x.MachineId == plant.MachineId && x.ShiftDate == date && x.ShiftId == s1)
            .OrderBy(x => x.WindowStartUtc).ThenBy(x => x.Hour == null).ToListAsync();
    });

    [Fact]
    public async Task Shift_closes_5_minutes_after_it_ends_and_equals_its_hours_added_up()
    {
        var at = Day(8);   // Tuesday 08/10/2030
        var plant = await SampleShiftAsync(api, at);

        await CloseAsync(plant, at(14, 4, 59));                       // grace not over yet
        var early = await S1RowsAsync(plant, at(8, 0, 0));
        Assert.Equal([6, 7, 8, 9, 10, 11, 12], early.Select(r => (int?)r.Hour));

        await CloseAsync(plant, at(14, 5, 0));
        var rows = await S1RowsAsync(plant, at(8, 0, 0));
        var total = Assert.Single(rows, r => r.IsShiftTotal);
        var hours = rows.Where(r => !r.IsShiftTotal).ToList();

        Assert.Equal(8, hours.Count);
        Assert.All(rows, r => Assert.True(r.IsFinal));
        Assert.Equal((405 * 60, 360 * 60, 1000L, 970L), (total.PlannedSeconds, total.RunSeconds, total.TotalCount, total.GoodCount));
        Assert.Equal(0.7984, total.Oee!.Value, 4);
        Assert.Equal((1000L, 1000L), (total.ScanCount, hours.Sum(h => h.ScanCount)));

        // Spec 2.4: the total is read from the raw data; adding up the hours (rule 12) must give the same numbers.
        var added = OeeCalculator.Combine(hours.Select(h => h.ToResult()));
        Assert.Equal((total.PlannedSeconds, total.RunSeconds, total.TotalCount, total.GoodCount),
            ((int)added.PlannedTime.TotalSeconds, (int)added.RunTime.TotalSeconds, added.TotalCount, added.GoodCount));
        Assert.Equal(total.IdealSeconds, added.IdealSeconds, 6);
        Assert.Equal(total.Oee!.Value, added.Oee!.Value, 9);
        Assert.NotEqual(total.Oee, hours.Average(h => h.Oee));          // the mean of the hourly OEEs is not the shift OEE
    }

    [Fact]
    public async Task Running_the_job_twice_does_not_duplicate_rows()
    {
        var at = Day(9);
        var plant = await SampleShiftAsync(api, at);

        var first = await CloseAsync(plant, at(14, 10, 0));
        var before = await S1RowsAsync(plant, at(8, 0, 0));
        var second = await CloseAsync(plant, at(14, 11, 0));
        var after = await S1RowsAsync(plant, at(8, 0, 0));

        Assert.True(first > 9);   // S1 and the catch-up days of S2/S3 of this new machine
        Assert.Equal(0, second);
        Assert.Equal(9, after.Count);
        Assert.Equal(before.Select(r => (r.Id, r.CalculatedAt)), after.Select(r => (r.Id, r.CalculatedAt)));
    }

    // The unique index is the last guard (two job runs racing): a second shift-total row for the same key fails,
    // although Hour is NULL (EF would filter NULLs out of a unique index by default).
    [Fact]
    public async Task Database_refuses_a_second_shift_total_row()
    {
        var at = Day(9);
        var plant = await NewMachineAsync(api);
        var s1 = await api.WithDbAsync(db => db.Shifts.Where(s => s.Code == "S1").Select(s => s.Id).SingleAsync());

        await Assert.ThrowsAnyAsync<Exception>(() => api.WithDbAsync(db =>
        {
            db.OeeSnapshots.Add(new OeeSnapshot(plant.MachineId, new DateOnly(2030, 10, 9), s1, null, at(6, 0, 0), at(14, 0, 0)));
            db.OeeSnapshots.Add(new OeeSnapshot(plant.MachineId, new DateOnly(2030, 10, 9), s1, null, at(6, 0, 0), at(14, 0, 0)));
            return db.SaveChangesAsync();
        }));
    }

    // The job was off from 06:30 to 09:05: the next run closes the three hours it missed.
    [Fact]
    public async Task Job_off_for_three_hours_catches_up_on_the_next_run()
    {
        var at = Day(10);
        var plant = await SampleShiftAsync(api, at);

        await CloseAsync(plant, at(6, 30, 0));
        Assert.Empty(await S1RowsAsync(plant, at(8, 0, 0)));

        await CloseAsync(plant, at(9, 5, 0));
        Assert.Equal([6, 7, 8], (await S1RowsAsync(plant, at(8, 0, 0))).Select(r => (int?)r.Hour));
    }

    // Thin test of the worker itself: the PeriodicTimer runs on the TimeProvider, so advancing a FakeTimeProvider
    // makes it tick without waiting a real minute.
    [Fact]
    public async Task Worker_closes_due_hours_when_the_clock_ticks()
    {
        var at = Day(11);
        var plant = await NewMachineAsync(api);
        var time = new FakeTimeProvider(new DateTimeOffset(at(7, 4, 30)));
        await using var host = api.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(time);
            s.Configure<OeeOptions>(o => o.CatchUpDays = 0);
        }));
        var worker = new OeeSnapshotWorker(host.Services.GetRequiredService<IServiceScopeFactory>(), time,
            NullLogger<OeeSnapshotWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.Empty(await S1RowsAsync(plant, at(8, 0, 0)));      // nothing before the first tick
            time.Advance(OeeSnapshotWorker.Period);                    // 07:05:30: the 06:00 hour is due

            var until = DateTime.UtcNow.AddSeconds(15);
            List<OeeSnapshot> rows;
            while ((rows = await S1RowsAsync(plant, at(8, 0, 0))).Count == 0 && DateTime.UtcNow < until)
                await Task.Delay(100);
            Assert.Equal([6], rows.Select(r => (int?)r.Hour));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
