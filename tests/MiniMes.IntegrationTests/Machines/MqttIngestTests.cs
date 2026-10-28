using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Contracts.Machines;
using MiniMes.Domain.Machines;
using MiniMes.Infrastructure.Machines;
using MiniMes.Infrastructure.Persistence;
using MQTTnet;
using MQTTnet.Protocol;
using MachineState = MiniMes.Domain.Machines.MachineState;
using WireState = MiniMes.Contracts.Machines.MachineState;

namespace MiniMes.IntegrationTests.Machines;

// Spec M4 step 9: the whole path with a real broker, as a machine sees it: MQTT client -> Mosquitto -> MqttIngestService
// -> runtime store -> batch writer -> SQL Server. Each test uses its own seeded machine (fresh database per run).
[Collection(MqttCollection.Name)]
public class MqttIngestTests(MqttApiFixture mqtt)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    // 100, 105, 3: a baseline, +5, then a reset counted as 3 (the machine made 3 since it was reset).
    [Fact]
    public async Task Counter_sequence_gives_deltas_and_a_reset()
    {
        var m = await MachineIdAsync("L1-SMT-01");
        var t0 = DateTimeOffset.UtcNow;

        foreach (var (counter, i) in new long[] { 100, 105, 3 }.Select((c, i) => (c, i)))
            await PublishAsync("L1-SMT-01", MachineKinds.Count, Message("L1-SMT-01", t0.AddSeconds(i)) with { Counter = counter });

        var events = await WaitForAsync(() => CountEventsAsync(m), e => e.Count == 3);
        Assert.Equal([(100L, 0L, false), (105L, 5L, false), (3L, 3L, true)], events.Select(e => (e.Counter!.Value, e.Delta!.Value, e.IsReset)));
    }

    // QoS 1 = at least once: the same msgId twice is stored and counted once.
    [Fact]
    public async Task Duplicate_msg_id_is_stored_once()
    {
        var m = await MachineIdAsync("L1-REFLOW-01");
        var t0 = DateTimeOffset.UtcNow;
        var twice = Message("L1-REFLOW-01", t0.AddSeconds(1)) with { Counter = 512 };

        await PublishAsync("L1-REFLOW-01", MachineKinds.Count, Message("L1-REFLOW-01", t0) with { Counter = 500 });
        await PublishAsync("L1-REFLOW-01", MachineKinds.Count, twice);
        await PublishAsync("L1-REFLOW-01", MachineKinds.Count, twice);
        await PublishAsync("L1-REFLOW-01", MachineKinds.Count, Message("L1-REFLOW-01", t0.AddSeconds(2)) with { Counter = 515 });

        var events = await WaitForAsync(() => CountEventsAsync(m), e => e.Count >= 3);
        await Task.Delay(1500);   // one more writer window: a second copy would have landed by now
        events = await CountEventsAsync(m);
        Assert.Single(events, e => e.MsgId == twice.MsgId);
        Assert.Equal(15L, events.Sum(e => e.Delta));
    }

    // A machine that stops talking (no heartbeat, no LWT: its software hangs) is Offline after 30 s.
    [Fact]
    public async Task Machine_without_heartbeat_goes_offline()
    {
        var m = await MachineIdAsync("L1-TEST-01");
        await PublishAsync("L1-TEST-01", MachineKinds.State, Message("L1-TEST-01", DateTimeOffset.UtcNow) with { State = WireState.Running });
        await WaitForAsync(() => OpenStateAsync(m), s => s == MachineState.Running);

        var state = await WaitForAsync(() => OpenStateAsync(m), s => s == MachineState.Offline, TimeSpan.FromSeconds(50));

        Assert.Equal(MachineState.Offline, state);
    }

    // "docker compose stop mosquitto", then start: the ingest reconnects by itself (backoff) and data flows again.
    [Fact]
    public async Task Ingest_reconnects_after_the_broker_restarts()
    {
        var m = await MachineIdAsync("L1-GLUE-01");
        var connection = mqtt.Factory.Services.GetRequiredService<MqttConnectionState>();

        await mqtt.Broker.StopAsync();
        await WaitForAsync(() => Task.FromResult(connection.ConnectedSince), c => c is null);
        await Task.Delay(TimeSpan.FromSeconds(3));
        await mqtt.Broker.StartAsync();
        await WaitForAsync(() => Task.FromResult(connection.ConnectedSince), c => c is not null, TimeSpan.FromSeconds(45));

        var t0 = DateTimeOffset.UtcNow;
        await PublishAsync("L1-GLUE-01", MachineKinds.Count, Message("L1-GLUE-01", t0) with { Counter = 40 });
        await PublishAsync("L1-GLUE-01", MachineKinds.Count, Message("L1-GLUE-01", t0.AddSeconds(1)) with { Counter = 47 });
        var events = await WaitForAsync(() => CountEventsAsync(m), e => e.Count == 2);
        Assert.Equal(7L, events.Sum(e => e.Delta));
    }

    // ---------- helpers ----------

    private static MachineMessage Message(string machine, DateTimeOffset ts) => new(Guid.NewGuid(), machine, ts);

    // A fresh client per message, like a machine that may have reconnected in between.
    private async Task PublishAsync(string machine, string kind, MachineMessage message)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", mqtt.Broker.Port).WithClientId("test-" + Guid.NewGuid().ToString("N")[..8]).Build());
        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(MachineTopic.Build("VN-BN01", "SMT-ASSY", "L1", machine, kind))
            .WithPayload(JsonSerializer.Serialize(message, MachineJson.Options))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());
        await client.DisconnectAsync(new MqttClientDisconnectOptions());
    }

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = mqtt.Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<int> MachineIdAsync(string code) => WithDbAsync(db => db.Machines.Where(x => x.Code == code).Select(x => x.Id).SingleAsync());

    private Task<List<MachineEvent>> CountEventsAsync(int machineId)
        => WithDbAsync(db => db.MachineEvents.AsNoTracking()
            .Where(e => e.MachineId == machineId && e.Kind == MachineEventKind.Count).OrderBy(e => e.MachineTs).ToListAsync());

    private Task<MachineState?> OpenStateAsync(int machineId)
        => WithDbAsync(db => db.MachineStateHistory.AsNoTracking()
            .Where(h => h.MachineId == machineId && h.EndAt == null).Select(h => (MachineState?)h.State).SingleOrDefaultAsync());

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? Wait);
        var value = await read();
        while (!done(value) && DateTime.UtcNow < until)
        {
            await Task.Delay(250);
            value = await read();
        }
        Assert.True(done(value), $"condition not reached in time, last value: {value}");
        return value;
    }
}
