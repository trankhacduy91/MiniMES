using MiniMes.Contracts.Machines;
using MiniMes.EdgeGateway.Config;
using MiniMes.EdgeGateway.Health;
using MiniMes.EdgeGateway.Pipeline;

namespace MiniMes.UnitTests.Gateway;

// Spec M4b 2.6: msgId from the content (rule G6); heartbeat every 5 s only for the machines the gateway can read (G7).
public class HealthAndMessageIdTests
{
    private static readonly DateTime T0 = new(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Same_content_same_msg_id()
    {
        Assert.Equal(MessageIdFactory.Create("L1-GLUE-01", "counter", T0, "100"), MessageIdFactory.Create("L1-GLUE-01", "counter", T0, "100"));
    }

    [Theory]
    [InlineData("L1-GLUE-02", "counter", 0, "100")]
    [InlineData("L1-GLUE-01", "state", 0, "100")]
    [InlineData("L1-GLUE-01", "counter", 1, "100")]   // 1 ms later
    [InlineData("L1-GLUE-01", "counter", 0, "101")]
    public void Any_difference_gives_another_msg_id(string machine, string tag, int ms, string value)
    {
        Assert.NotEqual(MessageIdFactory.Create("L1-GLUE-01", "counter", T0, "100"), MessageIdFactory.Create(machine, tag, T0.AddMilliseconds(ms), value));
    }

    [Fact]
    public void Heartbeat_every_5_s_for_connected_machines_with_their_state()
    {
        var health = new HealthPublisher(Options());
        health.ConnectionChanged("L1-GLUE-01", connected: true);
        health.ConnectionChanged("L1-REFLOW-01", connected: true);
        MachineState? StateOf(string m) => m == "L1-GLUE-01" ? MachineState.Running : null;

        var first = health.Due(T0, StateOf);
        var tooSoon = health.Due(T0.AddSeconds(4), StateOf);
        var next = health.Due(T0.AddSeconds(5), StateOf);

        var hb = Assert.Single(first);   // the oven has no state yet: no heartbeat for it
        Assert.Equal("minimes/VN-BN01/SMT-ASSY/L1/L1-GLUE-01/heartbeat", hb.Topic);
        Assert.Equal(MachineState.Running, hb.Message.State);
        Assert.Equal("modbus", hb.Message.Source);
        Assert.False(hb.Retain);
        Assert.Empty(tooSoon);
        Assert.Single(next);
    }

    [Fact]
    public void No_heartbeat_for_a_lost_machine()
    {
        var health = new HealthPublisher(Options());
        health.ConnectionChanged("L1-GLUE-01", connected: true);
        health.ConnectionChanged("L1-GLUE-01", connected: false);

        Assert.Empty(health.Due(T0, _ => MachineState.Running));
    }

    private static GatewayOptions Options() => new()
    {
        Gateway = new GatewayIdentity { Id = "GW01", Site = "VN-BN01", Area = "SMT-ASSY", Line = "L1" },
        Devices = [new() { Machine = "L1-REFLOW-01", Protocol = "opcua" }, new() { Machine = "L1-GLUE-01", Protocol = "modbus" }],
    };
}
