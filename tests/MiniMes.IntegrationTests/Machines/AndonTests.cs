using System.Net;
using System.Net.Http.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MiniMes.Application.Andon;
using MiniMes.Application.Machines;
using MiniMes.Contracts.Andon;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Andon;
using MiniMes.Infrastructure.Machines;
using MiniMes.IntegrationTests.Infrastructure;
using Xunit.Abstractions;
using DomainState = MiniMes.Domain.Machines.MachineState;

namespace MiniMes.IntegrationTests.Machines;

// Spec M4 step 7: raise -> acknowledge -> resolve over HTTP, the roles, one open call per (machine, type) and the
// automatic raise. Each test gets its own machine on line L1, so open calls of other tests never interfere.
[Collection(ApiCollection.Name)]
public class AndonTests(ApiFixture api, ITestOutputHelper output)
{
    [Fact]
    public async Task Raise_acknowledge_resolve_happy_path()
    {
        var machine = await NewMachineAsync();
        var op = await api.LoginOperatorAsync();
        var sup = await api.LoginAsync("sup01");
        var mt = await api.LoginAsync("mt01");

        var raise = await op.PostAsJsonAsync("/api/andon", new RaiseAndonRequest(machine.Id, AndonTypes.Machine));
        Assert.Equal(HttpStatusCode.Created, raise.StatusCode);
        var raised = (await raise.Content.ReadFromJsonAsync<RaiseAndonResult>())!;
        Assert.True(raised.Created);
        Assert.Equal((machine.Code, "L1", "Machine", "Raised", "EMP00123"),
            (raised.Andon.Machine, raised.Andon.Line, raised.Andon.Type, raised.Andon.Status, raised.Andon.RaisedBy));

        var acked = await ReadAsync(await AckAsync(sup, raised.Andon));
        Assert.Equal(("Acknowledged", "sup01"), (acked.Status, acked.AckBy));

        var resolved = await ReadAsync(await ResolveAsync(mt, acked, await ReasonIdAsync("MCH-FAULT"), "nozzle 3 replaced"));
        Assert.Equal(("Resolved", "mt01", "MCH-FAULT", "nozzle 3 replaced"),
            (resolved.Status, resolved.ResolvedBy, resolved.DowntimeReason, resolved.ResolutionNote));
        Assert.Equal("sup01", resolved.AckBy);

        var open = await sup.GetFromJsonAsync<List<AndonDto>>("/api/andon?status=open&line=L1");
        Assert.DoesNotContain(open!, a => a.Id == raised.Andon.Id);
        var closed = await sup.GetFromJsonAsync<List<AndonDto>>("/api/andon?status=Resolved&line=L1");
        Assert.Contains(closed!, a => a.Id == raised.Andon.Id);
    }

    // The auto-raise (or a colleague) was first: the operator gets the open call back, not a second one.
    // Another type is another problem and gets its own call.
    [Fact]
    public async Task Raise_when_a_call_is_already_open_returns_that_call()
    {
        var machine = await NewMachineAsync();
        var op = await api.LoginOperatorAsync();
        var first = await RaiseAsync(op, machine.Id);

        var again = await op.PostAsJsonAsync("/api/andon", new RaiseAndonRequest(machine.Id, AndonTypes.Machine));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var result = (await again.Content.ReadFromJsonAsync<RaiseAndonResult>())!;
        Assert.Equal((false, first.Id), (result.Created, result.Andon.Id));
        Assert.True((await RaiseAsync(op, machine.Id, AndonTypes.Material)).Id != first.Id);
        Assert.Equal(2, await api.WithDbAsync(db => db.AndonCalls.CountAsync(a => a.MachineId == machine.Id)));
    }

    // The race the "already open" check cannot stop: both requests pass it, the filtered unique index refuses the
    // second INSERT and the loser returns the winner's call. Several rounds, the invariant must hold in each.
    [Fact]
    public async Task Two_raises_at_the_same_moment_open_one_call()
    {
        var op1 = await api.LoginOperatorAsync();
        var op2 = await api.LoginOperatorAsync();
        for (var round = 0; round < 5; round++)
        {
            var machine = await NewMachineAsync();
            var body = new RaiseAndonRequest(machine.Id, AndonTypes.Machine);

            var responses = await TogetherAsync(() => op1.PostAsJsonAsync("/api/andon", body), () => op2.PostAsJsonAsync("/api/andon", body));

            output.WriteLine($"round {round}: {string.Join(", ", responses.Select(r => (int)r.StatusCode))}");
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<RaiseAndonResult>())!.Andon.Id));
            Assert.Equal(ids[0], ids[1]);
            Assert.Equal(1, await api.WithDbAsync(db => db.AndonCalls.CountAsync(a => a.MachineId == machine.Id)));
        }
    }

    // Two supervisors press Acknowledge on the same call: one wins, the other is told someone else took it.
    [Fact]
    public async Task Two_acknowledges_at_the_same_moment_one_200_one_409()
    {
        var sup = await api.LoginAsync("sup01");
        var mt = await api.LoginAsync("mt01");
        for (var round = 0; round < 5; round++)
        {
            var call = await RaiseAsync(await api.LoginOperatorAsync(), (await NewMachineAsync()).Id);

            var responses = await TogetherAsync(() => AckAsync(sup, call), () => AckAsync(mt, call));

            output.WriteLine($"round {round}: {string.Join(", ", responses.Select(r => (int)r.StatusCode))}");
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        }
    }

    // Closed is closed: the right version but the wrong status is a business rule (422), not a conflict.
    [Fact]
    public async Task Acknowledge_of_a_resolved_call_is_422()
    {
        var sup = await api.LoginAsync("sup01");
        var call = await RaiseAsync(await api.LoginOperatorAsync(), (await NewMachineAsync()).Id);
        var resolved = await ReadAsync(await ResolveAsync(sup, call, await ReasonIdAsync("FDR-JAM")));
        Assert.Equal("sup01", resolved.AckBy);   // resolved straight from Raised

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AckAsync(sup, resolved)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ResolveAsync(sup, resolved, await ReasonIdAsync("FDR-JAM"))).StatusCode);
    }

    [Fact]
    public async Task Roles_operator_raises_responders_answer()
    {
        var op = await api.LoginOperatorAsync();
        var machine = await NewMachineAsync();
        var call = await RaiseAsync(op, machine.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await AckAsync(op, call)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ResolveAsync(op, call, await ReasonIdAsync("MCH-FAULT"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/andon/{call.Id}/cancel", new AndonTransitionRequest(call.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await (await api.LoginAsync("planner01")).PostAsJsonAsync("/api/andon", new RaiseAndonRequest(machine.Id, AndonTypes.Quality))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous().GetAsync("/api/andon")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await AckAsync(await api.LoginAsync("mt01"), call)).StatusCode);
    }

    [Fact]
    public async Task Resolve_without_or_with_an_unknown_downtime_reason_is_400()
    {
        var sup = await api.LoginAsync("sup01");
        var call = await RaiseAsync(await api.LoginOperatorAsync(), (await NewMachineAsync()).Id);

        var missing = await sup.PostAsJsonAsync($"/api/andon/{call.Id}/resolve", new ResolveAndonRequest(null, null, call.RowVersion));
        var unknown = await ResolveAsync(sup, call, 999_999);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (missing.StatusCode, unknown.StatusCode));
        Assert.Equal(AndonStatus.Raised, await api.WithDbAsync(db => db.AndonCalls.Where(a => a.Id == call.Id).Select(a => a.Status).SingleAsync()));
    }

    [Fact]
    public async Task Bad_input_is_400_and_unknown_ids_are_404()
    {
        var op = await api.LoginOperatorAsync();
        var sup = await api.LoginAsync("sup01");

        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync("/api/andon", new RaiseAndonRequest(1, "Fire"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.PostAsJsonAsync("/api/andon", new RaiseAndonRequest(999_999, AndonTypes.Machine))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sup.PostAsJsonAsync("/api/andon/999999/acknowledge", new AndonTransitionRequest("AAAAAAAAAAA="))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.PostAsJsonAsync("/api/andon/1/acknowledge", new AndonTransitionRequest("not base64!"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.GetAsync("/api/andon?status=closed")).StatusCode);
    }

    [Fact]
    public async Task Cancel_closes_a_call_raised_by_mistake()
    {
        var sup = await api.LoginAsync("sup01");
        var call = await RaiseAsync(await api.LoginOperatorAsync(), (await NewMachineAsync()).Id);

        var cancelled = await ReadAsync(await sup.PostAsJsonAsync($"/api/andon/{call.Id}/cancel", new AndonTransitionRequest(call.RowVersion)));

        Assert.Equal(("Cancelled", "sup01", null), (cancelled.Status, cancelled.ResolvedBy, cancelled.DowntimeReasonId));
    }

    // AndonWatcherService: Down for X seconds -> exactly one call from "system"; still exactly one on the next ticks,
    // and none again after it was resolved while the machine stays Down (one automatic call per Down period).
    [Fact]
    public async Task Watcher_raises_one_automatic_call_per_down_period()
    {
        var machine = await NewMachineAsync();
        var store = api.Factory.Services.GetRequiredService<MachineRuntimeStore>();
        var watcher = ActivatorUtilities.CreateInstance<AndonWatcherService>(api.Factory.Services);
        var now = DateTime.UtcNow;

        store.Seed(machine.Id, DomainState.Down, now.AddSeconds(-30), null, null);   // 30 s < X = 60 s
        await watcher.TickAsync(default);
        Assert.Empty(await CallsOfAsync(machine.Id));

        store.Seed(machine.Id, DomainState.Down, now.AddSeconds(-61), null, null);
        await watcher.TickAsync(default);
        await watcher.TickAsync(default);
        var call = Assert.Single(await CallsOfAsync(machine.Id));
        Assert.Equal((AndonCall.SystemUser, AndonType.Machine, AndonStatus.Raised), (call.RaisedBy, call.Type, call.Status));

        // The operator presses the button too: gets the automatic call back.
        var manual = await RaiseAsync(await api.LoginOperatorAsync(), machine.Id);
        Assert.Equal(call.Id, manual.Id);

        await ResolveAsync(await api.LoginAsync("sup01"), manual, await ReasonIdAsync("MCH-FAULT"));
        await watcher.TickAsync(default);
        Assert.Single(await CallsOfAsync(machine.Id));

        // A new Down period (it starts after the first call was raised) is a new problem. X = 0 so no waiting.
        store.Seed(machine.Id, DomainState.Down, DateTime.UtcNow, null, null);
        var impatient = ActivatorUtilities.CreateInstance<AndonWatcherService>(api.Factory.Services,
            Options.Create(new AndonOptions { AutoRaiseAfterSeconds = 0 }));
        await impatient.TickAsync(default);
        Assert.Equal(2, (await CallsOfAsync(machine.Id)).Count);
    }

    // ---------- [v2] step 7b: escalation and statistics ----------

    // EscalateAndonsCommand against the real database: each call goes to the level its waiting time deserves; an
    // acknowledged call never moves; a second run changes nothing (EscalationLevel is stored, not recomputed).
    [Fact]
    public async Task Escalation_follows_the_seed_rules_and_is_idempotent()
    {
        var now = DateTime.UtcNow;
        var fresh = await InsertCallAsync(AndonType.Machine, now.AddMinutes(-2));
        var level1 = await InsertCallAsync(AndonType.Machine, now.AddMinutes(-4));
        var level2 = await InsertCallAsync(AndonType.Machine, now.AddMinutes(-11));
        var quality = await InsertCallAsync(AndonType.Quality, now.AddMinutes(-6));
        var material = await InsertCallAsync(AndonType.Material, now.AddMinutes(-60));
        var acked = await InsertCallAsync(AndonType.Machine, now.AddMinutes(-30), ackAfter: TimeSpan.FromMinutes(1));

        await EscalateAsync();
        var first = await LevelsAsync(fresh, level1, level2, quality, material, acked);
        await EscalateAsync();

        Assert.Equal([0, 1, 2, 1, 0, 0], first);
        Assert.Equal(first, await LevelsAsync(fresh, level1, level2, quality, material, acked));
        var escalatedAt = await api.WithDbAsync(db => db.AndonCalls.Where(a => a.Id == level2).Select(a => a.LastEscalatedAt).SingleAsync());
        Assert.NotNull(escalatedAt);
    }

    [Fact]
    public async Task Seed_has_the_escalation_rules_of_the_spec()
    {
        var rules = await api.WithDbAsync(db => db.AndonEscalationRules.AsNoTracking()
            .OrderBy(r => r.AndonType).ThenBy(r => r.Level).ToListAsync());

        Assert.Equal(
            [(AndonType.Machine, 0, 0, "Supervisor,Maintenance"), (AndonType.Machine, 1, 3, "Admin"),
             (AndonType.Machine, 2, 10, "Admin"), (AndonType.Quality, 1, 5, "QC")],
            rules.Select(r => (r.AndonType, r.Level, r.AfterMinutes, r.NotifyRoles)));
    }

    // Calls in January 2020 only (no other test writes there): Cancelled is left out, reaction = AckAt - RaisedAt.
    [Fact]
    public async Task Stats_count_final_levels_and_average_reaction_time()
    {
        var t0 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await api.WithDbAsync(async db =>
        {
            var m1 = (await NewMachineAsync()).Id;
            var m2 = (await NewMachineAsync()).Id;
            var a = new AndonCall(m1, AndonType.Machine, "system", t0);              // level 1, ack after 240 s
            a.Escalate(1, t0.AddMinutes(3)); a.Acknowledge("sup01", t0.AddMinutes(4));
            var b = new AndonCall(m1, AndonType.Material, "EMP00123", t0);           // level 0, ack after 60 s
            b.Acknowledge("sup01", t0.AddMinutes(1));
            var c = new AndonCall(m1, AndonType.Quality, "EMP00123", t0);            // cancelled: left out
            c.Escalate(1, t0.AddMinutes(5)); c.Cancel("sup01", t0.AddMinutes(6));
            var d = new AndonCall(m1, AndonType.Other, "EMP00123", t0.AddHours(1));  // never acknowledged
            var e = new AndonCall(m2, AndonType.Machine, "system", t0.AddHours(2));  // level 2, resolved after 720 s
            e.Escalate(2, t0.AddHours(2).AddMinutes(10)); e.Resolve("mt01", await ReasonIdAsync("MCH-FAULT"), null, t0.AddHours(2).AddMinutes(12));
            var outside = new AndonCall(m2, AndonType.Material, "EMP00123", t0.AddDays(1));   // To is exclusive
            db.AndonCalls.AddRange(a, b, c, d, e, outside);
            return await db.SaveChangesAsync();
        });
        var sup = await api.LoginAsync("sup01");

        var stats = await sup.GetFromJsonAsync<AndonStatsDto>("/api/andon/stats?from=2020-01-01T00:00:00Z&to=2020-01-02T00:00:00Z");

        Assert.Equal((4, 3, 340.0), (stats!.Total, stats.Acknowledged, stats.AvgReactionSeconds));
        Assert.Equal([new AndonLevelCountDto(0, 2), new AndonLevelCountDto(1, 1), new AndonLevelCountDto(2, 1)], stats.ByLevel);
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), stats.From);
    }

    [Fact]
    public async Task Stats_need_a_responder_role_and_a_valid_range()
    {
        var empty = await (await api.LoginAsync("admin")).GetFromJsonAsync<AndonStatsDto>("/api/andon/stats?from=2019-01-01T00:00:00Z&to=2019-01-02T00:00:00Z");
        Assert.Equal((0, null), (empty!.Total, empty.AvgReactionSeconds));

        var sup = await api.LoginAsync("sup01");
        Assert.Equal(HttpStatusCode.Forbidden, (await (await api.LoginOperatorAsync()).GetAsync("/api/andon/stats?from=2020-01-01&to=2020-01-02")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.GetAsync("/api/andon/stats?from=2020-01-02&to=2020-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.GetAsync("/api/andon/stats?from=2020-01-01&to=2021-06-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.GetAsync("/api/andon/stats")).StatusCode);
    }

    // ---------- helpers ----------

    private async Task<long> InsertCallAsync(AndonType type, DateTime raisedAt, TimeSpan? ackAfter = null)
    {
        var machineId = (await NewMachineAsync()).Id;
        return await api.WithDbAsync(async db =>
        {
            var call = new AndonCall(machineId, type, AndonCall.SystemUser, raisedAt);
            if (ackAfter is { } after) call.Acknowledge("sup01", raisedAt + after);
            db.AndonCalls.Add(call);
            await db.SaveChangesAsync();
            return call.Id;
        });
    }

    private async Task EscalateAsync()
    {
        using var scope = api.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new EscalateAndonsCommand());
    }

    private Task<int[]> LevelsAsync(params long[] ids)
        => api.WithDbAsync(async db =>
        {
            var levels = await db.AndonCalls.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.EscalationLevel);
            return ids.Select(id => levels[id]).ToArray();
        });

    private async Task<MachineDto> NewMachineAsync()
    {
        var admin = await api.LoginAsync("admin");
        var wc = await admin.PostAsJsonAsync("/api/work-centers", new CreateWorkCenterRequest(1, ApiFixture.Unique("WC"), "Work center", "SMT"));
        wc.EnsureSuccessStatusCode();
        var code = ApiFixture.Unique("M");
        var m = await admin.PostAsJsonAsync("/api/machines",
            new CreateMachineRequest((await wc.Content.ReadFromJsonAsync<WorkCenterDto>())!.Id, code, "Andon test", "PickAndPlace", code, "Mqtt"));
        m.EnsureSuccessStatusCode();
        return (await m.Content.ReadFromJsonAsync<MachineDto>())!;
    }

    private static async Task<AndonDto> RaiseAsync(HttpClient client, int machineId, string type = AndonTypes.Machine)
    {
        var response = await client.PostAsJsonAsync("/api/andon", new RaiseAndonRequest(machineId, type));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RaiseAndonResult>())!.Andon;
    }

    private static Task<HttpResponseMessage> AckAsync(HttpClient client, AndonDto call)
        => client.PostAsJsonAsync($"/api/andon/{call.Id}/acknowledge", new AndonTransitionRequest(call.RowVersion));

    private static Task<HttpResponseMessage> ResolveAsync(HttpClient client, AndonDto call, int reasonId, string? note = null)
        => client.PostAsJsonAsync($"/api/andon/{call.Id}/resolve", new ResolveAndonRequest(reasonId, note, call.RowVersion));

    private static async Task<AndonDto> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<AndonDto>())!;
    }

    private Task<int> ReasonIdAsync(string code)
        => api.WithDbAsync(db => db.DowntimeReasons.Where(r => r.Code == code).Select(r => r.Id).SingleAsync());

    private Task<List<AndonCall>> CallsOfAsync(int machineId)
        => api.WithDbAsync(db => db.AndonCalls.AsNoTracking().Where(a => a.MachineId == machineId).OrderBy(a => a.Id).ToListAsync());

    private static async Task<HttpResponseMessage[]> TogetherAsync(params Func<Task<HttpResponseMessage>>[] calls)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = calls.Select(call => Task.Run(async () => { await gate.Task; return await call(); })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }
}
