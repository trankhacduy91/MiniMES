using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Domain.Oee;
using MiniMes.Domain.Planning;
using MiniMes.Infrastructure.Persistence;
using MiniMes.Infrastructure.Persistence.Seed;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Oee;

// Spec M5 step 9 / DoD: the Development demo shifts give the numbers the demo script promises, shift and hours.
[Collection(ApiCollection.Name)]
public class OeeDemoSeederTests(ApiFixture api)
{
    [Fact]
    public async Task Demo_shifts_show_the_sample_numbers_and_the_two_warnings()
    {
        using (var scope = api.Factory.Services.CreateScope())
        {
            var seeder = ActivatorUtilities.CreateInstance<OeeDemoSeeder>(scope.ServiceProvider);
            await seeder.SeedAsync();
            await seeder.SeedAsync();   // once: the second run finds the work orders and writes nothing
        }

        var rows = await api.WithDbAsync(async db =>
        {
            var machine = await db.Machines.Where(m => m.Code == "L1-SMT-01").Select(m => m.Id).SingleAsync();
            var s1 = await db.Shifts.Where(s => s.Code == "S1").Select(s => s.Id).SingleAsync();
            DateOnly[] dates = [OeeDemoSeeder.Sample.Date, OeeDemoSeeder.FastMachine.Date, OeeDemoSeeder.MissingScans.Date];
            return await db.OeeSnapshots.AsNoTracking()
                .Where(s => s.MachineId == machine && s.ShiftId == s1 && dates.Contains(s.ShiftDate)).ToListAsync();
        });
        OeeSnapshot Total(DemoDay d) => rows.Single(r => r.ShiftDate == Date(d) && r.Hour == null);
        IEnumerable<OeeSnapshot> Hours(DemoDay d) => rows.Where(r => r.ShiftDate == Date(d) && r.Hour != null);

        // 01/10: the hand calculation of docs/oee-hand-calcs.md, and no badge in any hour either.
        var sample = Total(DemoDay.Sample);
        Assert.Equal((405 * 60, 360 * 60, 1000L, 970L, 1000L), (sample.PlannedSeconds, sample.RunSeconds, sample.TotalCount, sample.GoodCount, sample.ScanCount));
        Assert.Equal(19_400.0 / 24_300.0, sample.Oee!.Value, 9);
        Assert.Equal(8, Hours(DemoDay.Sample).Count());
        Assert.All(Hours(DemoDay.Sample).Append(sample), r => Assert.Equal(OeeWarning.None, r.Warnings));

        // 02/10: a board every ~15 s of run time against 20 s in the WO: P = 1,440 x 20 / 21,600 = 133 %, kept raw.
        var fast = Total(DemoDay.Fast);
        Assert.Equal(OeeWarning.PerformanceOver100, fast.Warnings);
        Assert.Equal(4.0 / 3.0, fast.Performance!.Value, 9);

        // 05/10: the scanner stops near the end: only that hour and the shift are flagged; Q = 910 / 1,000.
        var missing = Total(DemoDay.Missing);
        Assert.Equal((OeeWarning.CounterMismatch, 1000L, 940L), (missing.Warnings, missing.TotalCount, missing.ScanCount));
        Assert.Equal(0.91, missing.Quality!.Value, 9);
        Assert.Equal([(byte)13], Hours(DemoDay.Missing).Where(h => h.Warnings != OeeWarning.None).Select(h => h.Hour!.Value));

        // History, not work: closed, so no station is offered their boards.
        string[] demo = [OeeDemoSeeder.Sample.WoNo, OeeDemoSeeder.FastMachine.WoNo, OeeDemoSeeder.MissingScans.WoNo];
        Assert.All(await api.WithDbAsync(db => db.WorkOrders.Where(w => demo.Contains(w.WoNo)).Select(w => w.Status).ToListAsync()),
            s => Assert.Equal(WorkOrderStatus.Closed, s));
    }

    private enum DemoDay { Sample, Fast, Missing }

    private static DateOnly Date(DemoDay d) => d switch
    {
        DemoDay.Sample => OeeDemoSeeder.Sample.Date,
        DemoDay.Fast => OeeDemoSeeder.FastMachine.Date,
        _ => OeeDemoSeeder.MissingScans.Date,
    };
}
