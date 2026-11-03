using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Application.Common;
using MiniMes.Application.Quality.Spc;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;
using MiniMes.Infrastructure.Persistence.Seed;
using MiniMes.IntegrationTests.Execution;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Quality;

// Spec M7 step 5: POST /api/quality/inspections (measurement + SPC + NG through the M3 scan in one transaction),
// the chart and the recompute of limits. Each test measures a plan of its own (PCB-A at TEST, baseline 3.302 /
// 0.040 like 3V3_RAIL), so the series and the open alerts of one test never mix with another's.
[Collection(ApiCollection.Name)]
public class InspectionTests(ApiFixture api)
{
    private readonly ScanTests scan = new(api);

    // Spec 1.2 WE4 table + one more point.
    private static readonly decimal[] Run9 = [3.318m, 3.325m, 3.311m, 3.322m, 3.309m, 3.330m, 3.316m, 3.321m, 3.319m];

    [Fact]
    public async Task A_value_in_spec_is_stored_ok_and_the_board_moves_on()
    {
        var plan = await PlanAsync();
        var (op, boards) = await AtTestAsync(1);

        var r = await InspectAsync(op, boards[0], (plan.Code, 3.300m));

        Assert.Equal(("Ok", (string?)null, (int?)40, false), (r.Result, r.ReasonCode, r.NextStep, r.Duplicate));
        var m = Assert.Single(r.Measurements);
        Assert.Equal((plan.Code, 3.300m, "Ok", (decimal?)3.135m, (decimal?)3.465m), (m.PlanCode, m.Value, m.Result, m.Lsl, m.Usl));
        Assert.Empty(r.Alerts);
        var stored = await api.WithDbAsync(db => db.Measurements.SingleAsync(x => x.Id == m.Id));
        Assert.Equal((MeasurementSource.Station, "EMP00123"), (stored.Source, stored.MeasuredBy));
        Assert.Equal(ScanResult.Ok, await LastTestScanAsync(boards[0]));
    }

    // Q1: out of spec -> Measurement NG and the board goes to the NG flow of M3, with the plan's defect code.
    [Fact]
    public async Task A_value_out_of_spec_makes_the_board_ng_through_the_m3_scan()
    {
        var plan = await PlanAsync();
        var (op, boards) = await AtTestAsync(1);

        var r = await InspectAsync(op, boards[0], (plan.Code, 3.470m));

        Assert.Equal("Ng", r.Result);
        Assert.StartsWith($"Out of specification: {plan.Code} 3.47 V (limits 3.135 to 3.465).", r.Message);
        Assert.Equal("Ng", Assert.Single(r.Measurements).Result);
        var (status, defect) = await api.WithDbAsync(async db => (
            await db.Serials.Where(s => s.SerialNo == boards[0]).Select(s => s.Status).SingleAsync(),
            await db.ScanLogs.Where(s => s.SerialNo == boards[0]).OrderByDescending(s => s.Id).Select(s => s.DefectCode).FirstAsync()));
        Assert.Equal((SerialStatus.Ng, MasterDataSeeder.OutOfSpecDefect), (status, defect));
    }

    // Spec 1.2 / DoD: 3.440 is inside USL (the board is OK) but beyond UCL 3.422 (WE1): two independent pairs of limits.
    [Fact]
    public async Task A_value_beyond_ucl_but_inside_usl_is_ok_and_raises_we1()
    {
        var plan = await PlanAsync();
        var (op, boards) = await AtTestAsync(1);

        var r = await InspectAsync(op, boards[0], (plan.Code, 3.440m));

        Assert.Equal("Ok", r.Result);
        var alert = Assert.Single(r.Alerts);
        Assert.Equal(("WE1", "Open", r.Measurements[0].Id), (alert.Rule, alert.Status, alert.MeasurementId!.Value));
    }

    // DoD: the 7-point run opens exactly one alert, at the 7th point; the 8th and 9th add nothing (Q5).
    [Fact]
    public async Task A_run_of_seven_opens_one_alert_and_the_next_points_add_none()
    {
        var plan = await PlanAsync();
        var (op, boards) = await AtTestAsync(Run9.Length);

        var answers = new List<InspectionResponse>();
        for (var i = 0; i < Run9.Length; i++) answers.Add(await InspectAsync(op, boards[i], (plan.Code, Run9[i])));

        Assert.Equal([0, 0, 0, 0, 0, 0, 1, 0, 0], answers.Select(a => a.Alerts.Count));
        Assert.Equal("WE4", answers[6].Alerts[0].Rule);
        var alerts = await api.WithDbAsync(db => db.SpcAlerts.Where(a => a.InspectionPlanId == plan.Id).ToListAsync());
        Assert.Equal((SpcRuleCode.WE4, answers[6].Measurements[0].Id), (Assert.Single(alerts).RuleCode, alerts[0].MeasurementId!.Value));
    }

    // Route check first (2.3): a board that skipped ASSY cannot be measured at TEST; nothing is written.
    [Fact]
    public async Task A_board_not_at_this_step_is_blocked_and_nothing_is_stored()
    {
        var plan = await PlanAsync();
        var (wo, serials) = await scan.ReleasedAsync(1);
        var op = await api.LoginOperatorAsync();
        await scan.OpenSessionAsync("L1-TEST-01", wo.Id);

        var r = await InspectAsync(op, serials[0], (plan.Code, 3.300m));

        Assert.Equal(("Blocked", "SkippedStep"), (r.Result, r.ReasonCode));
        Assert.Empty(r.Measurements);
        Assert.False(await api.WithDbAsync(db => db.Measurements.AnyAsync(m => m.InspectionPlanId == plan.Id)));
    }

    // 2.3 trap 3: the same request again (a retry after a timeout) answers the first result, stores nothing new.
    // Two characteristics in one request share the request id (unique per request + plan).
    [Fact]
    public async Task A_resent_request_returns_the_first_answer()
    {
        var (rail, ripple) = (await PlanAsync(), await PlanAsync(lsl: null, usl: 0.050m));
        var (op, boards) = await AtTestAsync(1);
        var request = new RecordInspectionRequest(await TestStationAsync(), boards[0],
            [new InspectionValue(rail.Code, 3.301m), new InspectionValue(ripple.Code, 0.060m)], "Machine", Guid.NewGuid());

        var first = await PostAsync(op, request);
        var again = await PostAsync(op, request);

        Assert.Equal(("Ng", false), (first.Result, first.Duplicate));     // ripple above its USL
        Assert.Equal(("Ng", true, "Duplicate request ignored."), (again.Result, again.Duplicate, again.Message));
        Assert.Equal(first.Measurements, again.Measurements);
        Assert.Equal(2, await api.WithDbAsync(db => db.Measurements.CountAsync(m => m.ClientRequestId == request.ClientRequestId)));
        Assert.Equal(1, await api.WithDbAsync(db => db.ScanLogs.CountAsync(s => s.SerialNo == boards[0] && s.Result == ScanResult.Ng)));
    }

    [Fact]
    public async Task Requests_with_wrong_plans_or_values_are_refused()
    {
        var plan = await PlanAsync();
        var smtPlan = await PlanAsync(operation: "SMT");
        var (op, boards) = await AtTestAsync(1);
        var station = await TestStationAsync();
        Task<HttpResponseMessage> Post(params InspectionValue[] values)
            => op.PostAsJsonAsync("/api/quality/inspections", new RecordInspectionRequest(station, boards[0], values));

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new InspectionValue("NO-SUCH-PLAN", 3.3m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new InspectionValue(QualitySeeder.ReflowPlan, 245m))).StatusCode);   // machine plan
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new InspectionValue(plan.Code, 3.4650004m))).StatusCode);          // 7 decimals
        Assert.Equal(HttpStatusCode.BadRequest, (await Post()).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(new InspectionValue(smtPlan.Code, 3.3m))).StatusCode);     // not at TEST
        Assert.False(await api.WithDbAsync(db => db.Measurements.AnyAsync(m => m.SerialId != null &&
            db.Serials.Any(s => s.Id == m.SerialId && s.SerialNo == boards[0]))));
    }

    // No baseline yet: the values are kept, nothing is judged (Q3: limits come from a stable period first).
    [Fact]
    public async Task Without_a_baseline_points_are_stored_and_no_rule_runs()
    {
        var plan = await PlanAsync(baseline: false);
        var (op, boards) = await AtTestAsync(1);

        var r = await InspectAsync(op, boards[0], (plan.Code, 3.440m));

        Assert.Equal(("Ok", 0), (r.Result, r.Alerts.Count));
    }

    // Two inspections at once on one plan: both succeed, one WE1 alert per round. Two points beyond 3σ on one side
    // are also a WE2: only WE1 is counted. NOTE: this does not reach the race itself. Without READ_COMMITTED_SNAPSHOT
    // the second request's window read waits for the first one's uncommitted measurement, so the two run one after
    // the other (mutation "no catch" stays green here). The race is forced in the next test.
    [Fact]
    public async Task Two_points_at_once_open_one_alert_and_both_requests_succeed()
    {
        var plan = await PlanAsync();
        var (op, boards) = await AtTestAsync(10);
        var op2 = await api.LoginOperatorAsync();

        for (var round = 0; round < 5; round++)
        {
            await api.WithDbAsync(db => db.SpcAlerts.Where(a => a.InspectionPlanId == plan.Id && a.Status == SpcAlertStatus.Open)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, SpcAlertStatus.Closed)));
            var (a, b) = (boards[2 * round], boards[2 * round + 1]);
            var answers = await ScanTests.TogetherAsync(() => InspectAsync(op, a, (plan.Code, 3.450m)), () => InspectAsync(op2, b, (plan.Code, 3.450m)));

            Assert.All(answers, x => Assert.Equal("Ok", x.Result));
            Assert.Equal(1, answers.Sum(x => x.Alerts.Count(al => al.Rule == "WE1")));
        }
        Assert.Equal(5, await api.WithDbAsync(db => db.SpcAlerts.CountAsync(a => a.InspectionPlanId == plan.Id && a.RuleCode == SpcRuleCode.WE1)));
        Assert.Equal(10, await api.WithDbAsync(db => db.Measurements.CountAsync(m => m.InspectionPlanId == plan.Id)));
    }

    // 2.3 trap 2, forced: two evaluations both see "no open WE1" before either inserts (QueryBarrier holds the
    // check until both ran it). The filtered unique index keeps one alert; the loser rolls back to its savepoint and
    // returns no alert instead of failing.
    [Fact]
    public async Task Two_evaluations_that_both_saw_no_open_alert_keep_one_alert_and_neither_fails()
    {
        var plan = await PlanAsync();
        var serial = await SerialIdAsync();
        var station = await TestStationAsync();
        var t0 = DateTime.UtcNow.AddMinutes(-1);
        var points = await api.WithDbAsync(async db =>
        {
            var rows = new[] { 0, 1 }.Select(i => Measurement.OfSerial(plan, MeasurementSource.Manual, serial, station, 3.450m, "qc01", t0.AddSeconds(i))).ToList();
            db.Measurements.AddRange(rows);
            await db.SaveChangesAsync();
            return rows.Select(r => r.Id).ToArray();
        });
        api.Factory.QueryBarrier.Hold(c => c.CommandText.Contains("[SpcAlerts]")
            && QueryBarrier.HasParameter(c, plan.Id) && QueryBarrier.HasParameter(c, "WE1"), parties: 2);

        var opened = await ScanTests.TogetherAsync(() => EvaluateAsync(points[0]), () => EvaluateAsync(points[1]));

        Assert.True(api.Factory.QueryBarrier.WasReleased, api.Factory.QueryBarrier.LastSeen);
        Assert.Equal(1, opened.Sum(o => o.Count(a => a == SpcRuleCode.WE1)));
        Assert.Equal(1, await api.WithDbAsync(db => db.SpcAlerts.CountAsync(a => a.InspectionPlanId == plan.Id && a.RuleCode == SpcRuleCode.WE1)));
    }

    private async Task<SpcRuleCode[]> EvaluateAsync(long measurementId)
    {
        using var scope = api.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var point = await db.Measurements.AsNoTracking().SingleAsync(m => m.Id == measurementId);
        await using var tx = await db.BeginTransactionAsync();
        var alerts = await scope.ServiceProvider.GetRequiredService<ISpcEvaluator>().EvaluateAsync(tx, point, CancellationToken.None);
        await tx.CommitAsync();
        return alerts.Select(a => a.RuleCode).ToArray();
    }

    // Spec 2.4: points oldest first, limits, the violation painted at the 7th point, window statistics.
    [Fact]
    public async Task Chart_returns_points_limits_violations_and_open_alerts()
    {
        var plan = await PlanAsync();
        var (op, boards) = await AtTestAsync(8);
        var ids = new List<long>();
        for (var i = 0; i < 8; i++) ids.Add((await InspectAsync(op, boards[i], (plan.Code, Run9[i]))).Measurements[0].Id);
        var qc = await api.LoginAsync("qc01");

        var chart = (await qc.GetFromJsonAsync<SpcChartDto>($"/api/quality/spc/{plan.Id}/chart?take=5"))!;
        var all = (await qc.GetFromJsonAsync<SpcChartDto>($"/api/quality/spc/{plan.Id}/chart"))!;

        Assert.Equal(ids[3..], chart.Points.Select(p => p.MeasurementId));               // the latest 5, oldest first
        Assert.Equal((3.422m, 3.182m), (chart.Limits!.Ucl, chart.Limits.Lcl));
        // Judged with the points before the window: the run still fires at the 7th and 8th point.
        Assert.Equal([(ids[6], "WE4"), (ids[7], "WE4")], chart.Violations.Select(v => (v.MeasurementId, v.Rule)));
        Assert.Equal(all.Violations, chart.Violations);
        Assert.Null(chart.Points[0].MovingRange);
        Assert.Equal(0.013, chart.Points[1].MovingRange!.Value, precision: 6);         // |3.309 − 3.322|
        Assert.Equal(5, chart.Window!.Count);
        Assert.Equal("WE4", Assert.Single(chart.OpenAlerts).Rule);
        Assert.Equal(boards[7], chart.Points[^1].SerialNo);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync($"/api/quality/spc/{plan.Id}/chart")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await qc.GetAsync("/api/quality/spc/999999/chart")).StatusCode);
    }

    // Q3: a new baseline only from at least 20 points, by QC, with a reason; the old one is kept as history.
    [Fact]
    public async Task Recompute_needs_twenty_points_and_keeps_the_old_baseline()
    {
        var plan = await PlanAsync();
        var t0 = new DateTime(2026, 11, 3, 1, 0, 0, DateTimeKind.Utc);
        var serial = await SerialIdAsync();
        var station = await TestStationAsync();
        // Spec 1.2's 10 points, twice: mean 3.320; σ over 20 points = √(2 × 0.0102 / 19) = 0.032767.
        decimal[] ten = [3.32m, 3.36m, 3.28m, 3.33m, 3.31m, 3.37m, 3.27m, 3.32m, 3.35m, 3.29m];
        await api.WithDbAsync(db =>
        {
            db.Measurements.AddRange(ten.Concat(ten).Select((v, i) =>
                Measurement.OfSerial(plan, MeasurementSource.Manual, serial, station, v, "qc01", t0.AddMinutes(i))));
            return db.SaveChangesAsync();
        });
        var qc = await api.LoginAsync("qc01");
        var url = $"/api/quality/spc/{plan.Id}/limits/recompute";

        var tooFew = await qc.PostAsJsonAsync(url, new RecomputeLimitsRequest(t0, t0.AddMinutes(19), "Phase I"));   // 19 points
        var byOperator = await (await api.LoginOperatorAsync()).PostAsJsonAsync(url, new RecomputeLimitsRequest(t0, t0.AddHours(1), "Phase I"));
        var ok = await qc.PostAsJsonAsync(url, new RecomputeLimitsRequest(t0, t0.AddHours(1), "New stencil, 20 stable boards"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooFew.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byOperator.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var limits = (await ok.Content.ReadFromJsonAsync<ControlLimitsDto>())!;
        Assert.Equal((3.32m, 0.032767m, 20, "qc01"), (limits.Mean, limits.Sigma, limits.SampleCount, limits.CalculatedBy));
        Assert.Equal(3.418301m, limits.Ucl);                                             // 3.32 + 3 × 0.032767
        var sets = await api.WithDbAsync(db => db.ControlLimitSets.Where(c => c.InspectionPlanId == plan.Id).OrderBy(c => c.Id).ToListAsync());
        Assert.Equal([(3.302m, false), (3.32m, true)], sets.Select(s => (s.Mean, s.IsActive)));
    }

    // ---------- helpers ----------

    private async Task<InspectionPlan> PlanAsync(string operation = "TEST", decimal? lsl = 3.135m, decimal? usl = 3.465m, bool baseline = true)
        => await api.WithDbAsync(async db =>
        {
            var pcbA = await db.Items.Where(i => i.Code == "PCB-A").Select(i => i.Id).SingleAsync();
            var ng = await db.DefectCodes.Where(d => d.Code == MasterDataSeeder.OutOfSpecDefect).Select(d => d.Id).SingleAsync();
            var plan = InspectionPlan.ForStation(ApiFixture.Unique("IN"), "Inspection test", pcbA, operation, "V", lsl, usl, null, ng);
            db.InspectionPlans.Add(plan);
            await db.SaveChangesAsync();
            if (baseline)
            {
                db.ControlLimitSets.Add(new ControlLimitSet(plan.Id, 3.302m, 0.040m, null, 0, null, null, "Test baseline", "qc01", DateTime.UtcNow));
                await db.SaveChangesAsync();
            }
            return plan;
        });

    // Boards of a new WO that passed SMT and ASSY, the session at TEST open on that WO.
    private async Task<(HttpClient Op, List<string> Boards)> AtTestAsync(int count)
    {
        var (wo, serials) = await scan.ReleasedAsync(count);
        var op = await api.LoginOperatorAsync();
        foreach (var station in new[] { "L1-SMT-01", "L1-ASM-01" })
        {
            await scan.OpenSessionAsync(station, wo.Id);
            foreach (var s in serials) await scan.ScanAsync(op, station, s);
        }
        await scan.OpenSessionAsync("L1-TEST-01", wo.Id);
        return (op, serials);
    }

    private Task<int> TestStationAsync() => scan.StationIdAsync("L1-TEST-01");

    // A board of its own for points written straight to the table (never "any serial": run alone, there is none).
    private async Task<long> SerialIdAsync()
    {
        var (_, serials) = await scan.ReleasedAsync(1);
        return await api.WithDbAsync(db => db.Serials.Where(s => s.SerialNo == serials[0]).Select(s => s.Id).SingleAsync());
    }

    private async Task<InspectionResponse> InspectAsync(HttpClient op, string serialNo, params (string Plan, decimal Value)[] values)
        => await PostAsync(op, new RecordInspectionRequest(await TestStationAsync(), serialNo,
            values.Select(v => new InspectionValue(v.Plan, v.Value)).ToList(), "Machine"));

    private static async Task<InspectionResponse> PostAsync(HttpClient op, RecordInspectionRequest request)
    {
        var response = await op.PostAsJsonAsync("/api/quality/inspections", request);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<InspectionResponse>())!;
    }

    private Task<ScanResult> LastTestScanAsync(string serialNo) => api.WithDbAsync(async db =>
    {
        var station = await db.Stations.Where(s => s.Code == "L1-TEST-01").Select(s => s.Id).SingleAsync();
        return await db.ScanLogs.Where(s => s.SerialNo == serialNo && s.StationId == station)
            .OrderByDescending(s => s.Id).Select(s => s.Result).FirstAsync();
    });
}
