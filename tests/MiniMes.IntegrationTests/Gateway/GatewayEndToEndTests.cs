using System.Text.Json;
using MiniMes.Application.Machines;
using MiniMes.Contracts.Machines;
using static MiniMes.IntegrationTests.Gateway.GatewayFixture;

namespace MiniMes.IntegrationTests.Gateway;

// Spec M4b step 7, the three end-to-end checks: (1) the right topics and schema, (2) broker down 20 s then back:
// every message, in order, (3) Modbus slave switched off: status offline.
[Collection(GatewayCollection.Name)]
public class GatewayEndToEndTests(GatewayFixture plant)
{
    // (1) What the gateway publishes is accepted by the MES's own reader (MachineMessageReader of the M4 ingest).
    [Fact]
    public async Task Messages_use_the_M4_topics_and_schema()
    {
        string[] expected =
        [
            Topic(Oven, "count"), Topic(Oven, "state"), Topic(Oven, "process/peakTemp"), Topic(Oven, "heartbeat"), Topic(Oven, "status"),
            Topic(Glue, "count"), Topic(Glue, "state"), Topic(Glue, "heartbeat"), Topic(Glue, "status"),
        ];
        await WaitForAsync(() => plant.Received.Select(m => m.Topic).ToHashSet(), seen => expected.All(seen.Contains),
            TimeSpan.FromSeconds(30), "every kind of message of both machines");

        foreach (var (topic, payload, _) in plant.Received.Where(m => m.Topic.StartsWith($"minimes/{Site}/{Area}/", StringComparison.Ordinal)))
        {
            var read = MachineMessageReader.Read(topic, payload);
            Assert.True(read.IsAccepted, $"{topic} {payload}: {read.RejectReason}");
            if (read.Input!.Message is { } msg)
                Assert.Equal(msg.Machine == Oven ? MachineSources.OpcUa : MachineSources.Modbus, msg.Source);
        }
        var glueCount = plant.Received.Where(m => m.Topic == Topic(Glue, "count")).Select(m => Parse(m.Payload)).First();
        Assert.Equal(100_000, glueCount.Counter);   // CDAB decoded right
        Assert.Contains(plant.Received, m => m.Topic == Topic(Oven, "state") && m.Retain);
        Assert.Contains(plant.Received, m => m.Topic == GatewayTopic.Status(Site, "GW-E2E")
            && JsonSerializer.Deserialize<GatewayStatusMessage>(m.Payload, MachineJson.Options)!.State == GatewayStates.Online);
    }

    // (2) Store-and-forward: the counter keeps moving while Mosquitto is stopped 20 s; afterwards the subscriber has
    // every value, each once (msgId), in the order they happened, and nothing that looks like a counter reset.
    [Fact]
    public async Task Broker_down_20_s_loses_nothing_and_keeps_the_order()
    {
        var start = 200_000u;
        plant.GlueSlave.Publish(start, state: 1, alarm: 0);
        await WaitForAsync(() => GlueCounters(), c => c.Contains(start), TimeSpan.FromSeconds(15), "baseline counter");

        await plant.Broker.StopAsync();
        var counter = start;
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            plant.GlueSlave.Publish(++counter, state: 1, alarm: 0);   // one board every 500 ms, polled every 200 ms
            await Task.Delay(500);
        }
        await plant.Broker.StartAsync();

        var last = counter;
        var after = await WaitForAsync(() => GlueCounters().SkipWhile(c => c != start).ToList(), c => c.Contains(last),
            TimeSpan.FromSeconds(60), $"counter {last} after the broker came back");

        // Each value once and in order: start, start+1, ..., last (a refresh of an unchanged value may repeat one).
        var distinctInOrder = after.Where((c, i) => i == 0 || c != after[i - 1]).TakeWhile(c => c <= last).ToList();
        Assert.Equal(Enumerable.Range(0, (int)(last - start) + 1).Select(i => (long)(start + i)), distinctInOrder);
        Assert.True(last - start >= 35, $"only {last - start} boards during the outage");
    }

    // (3) Rule G7: the gateway loses the PLC -> "offline" on the machine's status topic (retained), never a "Down";
    // the slave back -> "online" and data again.
    [Fact]
    public async Task Modbus_slave_switched_off_gives_status_offline()
    {
        await WaitForAsync(() => LastStatus(Glue), s => s == GatewayStates.Online, TimeSpan.FromSeconds(30), "glue online first");
        var statesBefore = plant.Received.Count(m => m.Topic == Topic(Glue, "state"));

        plant.GlueSlave.Stop();
        await WaitForAsync(() => LastStatus(Glue), s => s == GatewayStates.Offline, TimeSpan.FromSeconds(15), "glue offline");

        Assert.DoesNotContain(plant.Received.Where(m => m.Topic == Topic(Glue, "state")).Skip(statesBefore),
            m => Parse(m.Payload).State == MachineState.Down);
        plant.GlueSlave.Start();
        await WaitForAsync(() => LastStatus(Glue), s => s == GatewayStates.Online, TimeSpan.FromSeconds(30), "glue online again");
    }

    private List<long> GlueCounters()
        => plant.Received.Where(m => m.Topic == Topic(Glue, "count"))
            .Select(m => Parse(m.Payload))
            .DistinctBy(m => m.MsgId)          // QoS 1 = at least once: the MES drops copies by msgId, so do we
            .Select(m => m.Counter!.Value)
            .ToList();

    private string? LastStatus(string machine)
        => plant.Received.LastOrDefault(m => m.Topic == Topic(machine, "status")).Payload;

    private static MachineMessage Parse(string payload) => JsonSerializer.Deserialize<MachineMessage>(payload, MachineJson.Options)!;
}
