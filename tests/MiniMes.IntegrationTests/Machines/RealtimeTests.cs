using System.Net;
using System.Net.Http.Json;
using MediatR;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Application.Andon;
using MiniMes.Application.Machines;
using MiniMes.Contracts.Andon;
using MiniMes.Contracts.Machines;
using MiniMes.Contracts.Realtime;
using MiniMes.Domain.Andon;
using MiniMes.Domain.Machines;
using MiniMes.Infrastructure.Machines;
using MiniMes.IntegrationTests.Infrastructure;
using DataQuality = MiniMes.Contracts.Machines.DataQuality;
using WireState = MiniMes.Contracts.Machines.MachineState;

namespace MiniMes.IntegrationTests.Machines;

// Spec M4 step 6: a real HubConnection against the test host over WebSockets. The .NET client sends the token as an
// Authorization header (also on the WebSocket handshake); ?access_token= is what browsers must use, tested apart.
[Collection(ApiCollection.Name)]
public class RealtimeTests(ApiFixture api)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private T Service<T>() where T : notnull => api.Factory.Services.GetRequiredService<T>();

    [Fact]
    public async Task Hub_refuses_a_connection_without_a_token()
    {
        await using var hub = Connect(token: null);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    // Browser clients cannot set headers on a WebSocket: the JwtBearer hook reads ?access_token= on /hubs.
    [Fact]
    public async Task Token_in_the_query_string_is_accepted_on_the_hub()
    {
        var token = (await api.LoginAsync("sup01")).DefaultRequestHeaders.Authorization!.Parameter;
        await using var hub = Connect(token: null, query: "?access_token=" + token);

        await hub.StartAsync();

        Assert.Equal(HubConnectionState.Connected, hub.State);
    }

    // State changes are pushed at once, and only to the machine's line group.
    [Fact]
    public async Task State_change_reaches_the_line_group_only()
    {
        var machine = await KnownMachineAsync("L1-TEST-01");
        await using var line1 = await JoinAsync("L1");
        await using var line2 = await JoinAsync("L2");
        var got1 = Next<MachineStateChangedDto>(line1, nameof(IMachineClient.MachineStateChanged));
        var got2 = Next<MachineStateChangedDto>(line2, nameof(IMachineClient.MachineStateChanged));
        Service<MachineRuntimeStore>().Apply(machine.Id, MachineKinds.State,
            new MachineMessage(Guid.NewGuid(), machine.Code, DateTimeOffset.UtcNow, State: WireState.Running), DateTime.UtcNow);

        await Service<MachineStateService>().MarkOfflineAsync(machine.Id, "test", default);

        var dto = await got1.WaitAsync(Wait);
        Assert.Equal(("L1-TEST-01", "L1", WireState.Offline), (dto.Machine, dto.Line, dto.State));
        await Task.Delay(500);
        Assert.False(got2.IsCompleted);                               // L2 hears nothing about L1
    }

    // Step 7: an Andon change is pushed at once (not throttled) to the machine's line, with the status after the change.
    [Fact]
    public async Task Andon_changes_are_pushed_to_the_line_group()
    {
        var machine = await KnownMachineAsync("L1-SMT-01");
        await using var line1 = await JoinAsync("L1");
        await using var line2 = await JoinAsync("L2");
        var pushes = new List<AndonDto>();
        line1.On<AndonDto>(nameof(IMachineClient.AndonChanged), d => { lock (pushes) pushes.Add(d); });
        var got2 = Next<AndonDto>(line2, nameof(IMachineClient.AndonChanged));
        var raise = await (await api.LoginOperatorAsync()).PostAsJsonAsync("/api/andon", new RaiseAndonRequest(machine.Id, AndonTypes.Other));
        var call = (await raise.Content.ReadFromJsonAsync<RaiseAndonResult>())!.Andon;

        (await (await api.LoginAsync("sup01")).PostAsJsonAsync($"/api/andon/{call.Id}/cancel", new AndonTransitionRequest(call.RowVersion)))
            .EnsureSuccessStatusCode();

        await WaitUntilAsync(() => { lock (pushes) return pushes.Count(p => p.Id == call.Id) >= 2; });
        lock (pushes)
            Assert.Equal(["Raised", "Cancelled"], pushes.Where(p => p.Id == call.Id).Select(p => p.Status));
        await Task.Delay(500);
        Assert.False(got2.IsCompleted);
    }

    // [v2] Step 7b: a call nobody acknowledged goes to the role groups of its level, which the server joined from the
    // JWT on connect (no JoinLine needed). Level 1 of a Machine call = Admin: the Supervisor does not get it, QC neither.
    [Fact]
    public async Task Escalation_reaches_the_role_groups_of_the_level_only()
    {
        var machine = await KnownMachineAsync("L1-REFLOW-01");
        await using var admin = await ConnectAsAsync("admin");
        await using var supervisor = await ConnectAsAsync("sup01");
        await using var qc = await ConnectAsAsync("qc01");
        // The command escalates every Raised call of the shared database: only pushes about this test's call count.
        var (toAdmin, toSupervisor, toQc) = (new List<AndonDto>(), new List<AndonDto>(), new List<AndonDto>());
        foreach (var (hub, into) in new[] { (admin, toAdmin), (supervisor, toSupervisor), (qc, toQc) })
            hub.On<AndonDto>(nameof(IMachineClient.AndonEscalated), d => { lock (into) into.Add(d); });
        var id = await api.WithDbAsync(async db =>
        {
            foreach (var open in db.AndonCalls.Where(a => a.MachineId == machine.Id && a.Status <= AndonStatus.Acknowledged))
                open.Cancel("test", DateTime.UtcNow);
            var call = new AndonCall(machine.Id, AndonType.Machine, AndonCall.SystemUser, DateTime.UtcNow.AddMinutes(-4));
            db.AndonCalls.Add(call);
            await db.SaveChangesAsync();
            return call.Id;
        });

        using (var scope = api.Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISender>().Send(new EscalateAndonsCommand());

        await WaitUntilAsync(() => { lock (toAdmin) return toAdmin.Any(d => d.Id == id); });
        lock (toAdmin) Assert.Equal((1, "Raised"), toAdmin.Where(d => d.Id == id).Select(d => (d.EscalationLevel, d.Status)).Single());
        await Task.Delay(500);
        lock (toSupervisor) Assert.DoesNotContain(toSupervisor, d => d.Id == id);
        lock (toQc) Assert.DoesNotContain(toQc, d => d.Id == id);
    }

    // Throttle: 10 counts between two publisher ticks are one push carrying the latest total.
    [Fact]
    public async Task Counter_pushes_are_throttled_to_the_latest_value()
    {
        var machine = await KnownMachineAsync("L1-SMT-01");
        await using var hub = await JoinAsync("L1");
        var received = new List<MachineCounterDto>();
        hub.On<MachineCounterDto>(nameof(IMachineClient.MachineCounterUpdated), d => { lock (received) received.Add(d); });
        var store = Service<MachineRuntimeStore>();
        var publisher = Service<RealtimePublisher>();
        await publisher.PublishDirtyAsync(default);                   // flush what other tests left
        var start = (store.Get(machine.Id)?.LastCounter ?? 0) + 1000;

        foreach (var i in Enumerable.Range(0, 10))
            store.Apply(machine.Id, MachineKinds.Count,
                new MachineMessage(Guid.NewGuid(), machine.Code, DateTimeOffset.UtcNow.AddMilliseconds(i), Counter: start + i), DateTime.UtcNow.AddMilliseconds(i));
        await publisher.PublishDirtyAsync(default);
        await publisher.PublishDirtyAsync(default);                   // nothing new: nothing sent

        await WaitUntilAsync(() => received.Any(r => r.Counter == start + 9));
        await Task.Delay(300);
        lock (received)
            Assert.Equal([start + 9], received.Where(r => r.Machine == machine.Code && r.Counter >= start).Select(r => r.Counter));
    }

    // The REST snapshot: master data of the line + live values from the runtime store.
    [Fact]
    public async Task Line_status_returns_the_machines_of_the_line_with_live_values()
    {
        var reflow = await KnownMachineAsync("L1-REFLOW-01");
        var tag = (await Service<MachineDirectory>().ResolveTagAsync(reflow.Id, "peakTemp", default))!;
        Service<MachineRuntimeStore>().ApplyProcess(tag,
            new MachineMessage(Guid.NewGuid(), reflow.Code, DateTimeOffset.UtcNow, Tag: "peakTemp", Value: 245.3), DateTime.UtcNow);
        var store = Service<MachineRuntimeStore>();
        var now = DateTime.UtcNow;
        if (store.Get(reflow.Id)?.LastCounter is null)
            store.Apply(reflow.Id, MachineKinds.Count, new MachineMessage(Guid.NewGuid(), reflow.Code, now, Counter: 1000), now);
        var hourBefore = store.CountThisHour(store.Get(reflow.Id)!, now);
        store.Apply(reflow.Id, MachineKinds.Count,
            new MachineMessage(Guid.NewGuid(), reflow.Code, now, Counter: store.Get(reflow.Id)!.LastCounter + 7), now);
        var client = await api.LoginOperatorAsync();

        var status = await client.GetFromJsonAsync<LineStatusDto>("/api/lines/L1/status");

        Assert.Contains(status!.Machines, m => m.Code == "L1-SMT-01" && m.DataSource == "Mqtt");
        var oven = Assert.Single(status.Machines, m => m.Code == "L1-REFLOW-01");
        Assert.Equal(("OpcUa", 245.3, DataQuality.Good), (oven.DataSource, oven.Process.Single().Value, oven.Process.Single().Quality));
        Assert.Equal(hourBefore + 7, oven.CountThisHour);                                            // step 8
        Assert.Contains(status.Machines, m => m.Code == "L1-GLUE-01" && m.DataSource == "Modbus");   // every work center of the line
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/lines/NOPE/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonymous().GetAsync("/api/lines/L1/status")).StatusCode);
    }

    // ---------- helpers ----------

    // The directory caches machines from the first topic it resolves (as the ingest would on the first message).
    private async Task<MachineRef> KnownMachineAsync(string code)
        => (await Service<MachineDirectory>().ResolveAsync(new ParsedTopic("VN-BN01", "SMT-ASSY", "L1", code, MachineKinds.State, null), default))!;

    private HubConnection Connect(string? token, string query = "")
    {
        var server = api.Factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, RealtimeHub.Path.TrimStart('/') + query), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                // TestServer's own WebSocket client: it does not see ClientWebSocketOptions, so the header the real
                // client would put on the handshake is added here.
                o.WebSocketFactory = async (ctx, ct) =>
                {
                    var ws = server.CreateWebSocketClient();
                    if (token is not null) ws.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
                    return await ws.ConnectAsync(ctx.Uri, ct);
                };
                if (token is not null) o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
    }

    private async Task<HubConnection> JoinAsync(string line)
    {
        var hub = await ConnectAsAsync("sup01");
        await hub.InvokeAsync(RealtimeHub.JoinLine, line);
        return hub;
    }

    private async Task<HubConnection> ConnectAsAsync(string user)
    {
        var client = await api.LoginAsync(user);
        var hub = Connect(client.DefaultRequestHeaders.Authorization!.Parameter);
        await hub.StartAsync();
        return hub;
    }

    private static Task<T> Next<T>(HubConnection hub, string method)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<T>(method, d => tcs.TrySetResult(d));
        return tcs.Task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.True(condition(), "expected push not received");
    }
}
