using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Application.Quality.Spc;
using MiniMes.Domain.Andon;
using MiniMes.Domain.Machines;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Domain.Quality;
using MiniMes.Infrastructure.Quality;
using MiniMes.IntegrationTests.Execution;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Quality;

// [v2] Spec M7 step 6 (b): ProcessSpcSampler driven by a FakeTimeProvider. Each test has its own oven, tag and plan
// (REFLOW_PEAK_TEMP-like: 235-255 °C, baseline 245.0 / 0.8, a sample every 30 s), so the seeded L1-REFLOW-01 and
// its Andon calls are never touched.
[Collection(ApiCollection.Name)]
public class ProcessSpcSamplerTests(ApiFixture api)
{
    private static readonly DateTime T0 = new(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);

    private sealed record Oven(int MachineId, int TagId, int PlanId);

    // The rhythm: one point per 30 s period, the latest Good value at that moment; nothing in between.
    [Fact]
    public async Task Takes_one_point_per_period_from_the_latest_good_value()
    {
        var oven = await OvenAsync();
        await ValuesAsync(oven, Enumerable.Range(0, 20).Select(i => (T0.AddSeconds(5 * i), 245.0 + i * 0.01, DataQuality.Good)).ToArray());
        var clock = new FakeTimeProvider(new DateTimeOffset(T0.AddSeconds(12)));
        var sampler = Sampler(clock);

        Assert.Equal(SampleOutcome.Taken, await sampler.SampleTagAsync(oven.TagId, default));    // value at 10 s
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(SampleOutcome.NotDue, await sampler.SampleTagAsync(oven.TagId, default));   // 22 s: 12 s after the point
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(SampleOutcome.Taken, await sampler.SampleTagAsync(oven.TagId, default));    // 42 s: value at 40 s
        Assert.Equal(SampleOutcome.NotDue, await sampler.SampleTagAsync(oven.TagId, default));   // the job running again

        var points = await PointsAsync(oven);
        Assert.Equal([(T0.AddSeconds(10), 245.02m), (T0.AddSeconds(40), 245.08m)], points.Select(p => (p.SourceTs!.Value, p.Value)));
        Assert.All(points, p => Assert.Equal((MeasurementSource.Machine, (long?)null, p.SourceTs), (p.Source, p.SerialId, (DateTime?)p.MeasuredAt)));
    }

    // A Bad value is a broken sensor (M4), a value from the future a clock problem: neither becomes a point.
    [Fact]
    public async Task Bad_and_future_values_are_not_sampled()
    {
        var oven = await OvenAsync();
        await ValuesAsync(oven, (T0.AddSeconds(5), 245.1, DataQuality.Good), (T0.AddSeconds(8), -3276.8, DataQuality.Bad),
            (T0.AddMinutes(10), 250, DataQuality.Good));

        Assert.Equal(SampleOutcome.Taken, await Sampler(new FakeTimeProvider(new DateTimeOffset(T0.AddSeconds(10)))).SampleTagAsync(oven.TagId, default));

        Assert.Equal([245.1m], (await PointsAsync(oven)).Select(p => p.Value));
    }

    // Spec check: the source stops (OPC UA server off) -> no made-up point, a "data gap" alert after 3 periods, one
    // only; values back -> a point again and the gap alert closes.
    [Fact]
    public async Task A_silent_source_gives_one_data_gap_alert_and_no_point_until_values_return()
    {
        var oven = await OvenAsync();
        await ValuesAsync(oven, (T0, 245.0, DataQuality.Good));
        var clock = new FakeTimeProvider(new DateTimeOffset(T0));
        var sampler = Sampler(clock);
        await sampler.SampleTagAsync(oven.TagId, default);

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(SampleOutcome.Waiting, await sampler.SampleTagAsync(oven.TagId, default));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(SampleOutcome.Gap, await sampler.SampleTagAsync(oven.TagId, default));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(SampleOutcome.Gap, await sampler.SampleTagAsync(oven.TagId, default));
        Assert.Single(await PointsAsync(oven));
        var gap = Assert.Single(await AlertsAsync(oven));
        Assert.Equal((SpcRuleCode.DataGap, SpcAlertStatus.Open, (long?)null), (gap.RuleCode, gap.Status, gap.MeasurementId));

        await ValuesAsync(oven, (T0.AddSeconds(115), 245.2, DataQuality.Good));
        Assert.Equal(SampleOutcome.Taken, await sampler.SampleTagAsync(oven.TagId, default));
        Assert.Equal(SpcAlertStatus.Closed, Assert.Single(await AlertsAsync(oven)).Status);
    }

    // Q18: beyond USL is an OutOfSpec alert + an Andon Quality call on the oven; no board is made NG. A longer
    // excursion stays one alert and one call.
    [Fact]
    public async Task Beyond_usl_raises_one_out_of_spec_alert_and_one_andon_quality()
    {
        var oven = await OvenAsync();
        await ValuesAsync(oven, (T0, 256.0, DataQuality.Good), (T0.AddSeconds(30), 256.5, DataQuality.Good));
        var clock = new FakeTimeProvider(new DateTimeOffset(T0));
        var sampler = Sampler(clock);

        await sampler.SampleTagAsync(oven.TagId, default);
        clock.Advance(TimeSpan.FromSeconds(30));
        await sampler.SampleTagAsync(oven.TagId, default);

        Assert.All(await PointsAsync(oven), p => Assert.Equal(MeasureResult.Ng, p.Result));
        Assert.Equal(1, (await AlertsAsync(oven)).Count(a => a.RuleCode == SpcRuleCode.OutOfSpec));
        var calls = await api.WithDbAsync(db => db.AndonCalls.Where(c => c.MachineId == oven.MachineId).ToListAsync());
        Assert.Equal((AndonType.Quality, AndonCall.SystemUser), (Assert.Single(calls).Type, calls[0].RaisedBy));
    }

    // Spec 2.3b demo, "drift L1-REFLOW-01 0.3": +0.3 °C per minute from 245, sampled every 30 s. The run rule sees
    // it at the 7th point (≈ 247 °C, z ≈ +1.3), long before USL 255 (≈ 33 minutes away).
    [Fact]
    public async Task A_slow_oven_drift_is_seen_by_the_run_rule_long_before_usl()
    {
        var oven = await OvenAsync();
        await ValuesAsync(oven, Enumerable.Range(1, 12).Select(i => (T0.AddSeconds(30 * i), 245.0 + 0.15 * i, DataQuality.Good)).ToArray());
        var clock = new FakeTimeProvider(new DateTimeOffset(T0));
        var sampler = Sampler(clock);

        for (var i = 1; i <= 12; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(SampleOutcome.Taken, await sampler.SampleTagAsync(oven.TagId, default));
        }

        var first = (await AlertsAsync(oven)).OrderBy(a => a.Id).First();
        var at = await api.WithDbAsync(db => db.Measurements.SingleAsync(m => m.Id == first.MeasurementId));
        Assert.Equal((SpcRuleCode.WE4, 246.05m), (first.RuleCode, at.Value));
        Assert.True(at.Value < 255m);
    }

    // The job started twice at once (two API instances, a restart overlapping): one point per machine value, held
    // by the unique (MachineTagId, SourceTs).
    [Fact]
    public async Task Two_samplers_at_once_store_one_point()
    {
        var oven = await OvenAsync();
        await ValuesAsync(oven, (T0, 245.3, DataQuality.Good));
        var clock = new FakeTimeProvider(new DateTimeOffset(T0.AddSeconds(1)));

        var outcomes = await ScanTests.TogetherAsync(() => Sampler(clock).SampleTagAsync(oven.TagId, default),
            () => Sampler(clock).SampleTagAsync(oven.TagId, default));

        Assert.Contains(SampleOutcome.Taken, outcomes);
        Assert.Single(await PointsAsync(oven));
    }

    // ---------- helpers ----------

    private ProcessSpcSampler Sampler(TimeProvider clock)
        => new(api.Factory.Services.GetRequiredService<IServiceScopeFactory>(), clock, Options.Create(new SpcOptions()),
            NullLogger<ProcessSpcSampler>.Instance);

    private Task<Oven> OvenAsync() => api.WithDbAsync(async db =>
    {
        var code = ApiFixture.Unique("T7");
        var wc = await db.WorkCenters.Where(w => w.Code == "WC-SMT").Select(w => w.Id).SingleAsync();
        var machine = new Machine(wc, code, "Test oven " + code, "ReflowOven", code, MachineDataSource.OpcUa);
        db.Machines.Add(machine);
        var plan = InspectionPlan.ForMachine(code, "Peak temperature " + code, "°C", 235m, 255m, 245m);
        db.InspectionPlans.Add(plan);
        await db.SaveChangesAsync();
        db.ControlLimitSets.Add(new ControlLimitSet(plan.Id, 245.0m, 0.8m, null, 0, null, null, "Test baseline", "qc01", T0));
        var tag = new MachineTag(machine.Id, "peakTemp", "°C", 0, 400);
        tag.LinkSpc(plan.Id, 30);
        db.MachineTags.Add(tag);
        await db.SaveChangesAsync();
        return new Oven(machine.Id, tag.Id, plan.Id);
    });

    private Task ValuesAsync(Oven oven, params (DateTime Ts, double Value, DataQuality Quality)[] values) => api.WithDbAsync(db =>
    {
        db.ProcessValues.AddRange(values.Select(v => new ProcessValue(oven.TagId, v.Value, v.Quality, v.Ts, v.Ts, Guid.NewGuid())));
        return db.SaveChangesAsync();
    });

    private Task<List<Measurement>> PointsAsync(Oven oven)
        => api.WithDbAsync(db => db.Measurements.Where(m => m.MachineTagId == oven.TagId).OrderBy(m => m.SourceTs).ToListAsync());

    private Task<List<SpcAlert>> AlertsAsync(Oven oven)
        => api.WithDbAsync(db => db.SpcAlerts.Where(a => a.InspectionPlanId == oven.PlanId).ToListAsync());
}
